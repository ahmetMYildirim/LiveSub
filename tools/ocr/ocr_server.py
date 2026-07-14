"""
ocr_server.py — Persistent multi-engine OCR FastAPI server for PsGameTranslator.

Serves PaddleOCR (default, loaded at startup), RapidOCR and EasyOCR (lazy-loaded
on first use) from a single process on a single port.

Usage:
    python ocr_server.py [--host 127.0.0.1] [--port 8765]

Dependencies:
    pip install -r requirements.txt          # fastapi, uvicorn, paddleocr, rapidocr
    pip install easyocr                      # optional, heavy (pulls torch)

Endpoints:
    GET  /health              → {"status", "model_loaded", "engines": {name: state}}
    POST /ocr?engine=paddle   → multipart file upload → {"text", "confidence", "lines"}
         engine ∈ {paddle, rapid, easy}; default paddle.
"""

import argparse
import os
import sys
import tempfile
import threading
import types

# PaddlePaddle's default GPU allocator grabs a large fraction of available VRAM
# up front regardless of actual need — this process shares the GPU with the
# translation server, so a growth-based allocator (only takes what it actually
# uses) avoids starving it. Must be set before `paddle` is imported anywhere
# (including transitively), so it's set here, at the very top of the module.
os.environ.setdefault("FLAGS_allocator_strategy", "auto_growth")

# ── ModelScope/torch avoidance ──────────────────────────────────────────────────
#
# paddlex (a paddleocr dependency) unconditionally does `import modelscope` at
# module load time, purely to offer ModelScope as one of several possible model-
# download hosts. modelscope in turn unconditionally imports torch, and torch's
# Windows DLL loader has a known, order-dependent flakiness that intermittently
# raises "OSError: [WinError 127] ... shm.dll ...", crashing the whole server
# before OCR ever runs — even though this app never needs ModelScope's hub
# (models are served from the local cache / other hosts). Installing a
# lightweight stand-in lets `import modelscope` succeed instantly without
# touching torch; if a real ModelScope download is ever actually requested,
# the stub lazily imports the real package at that point instead.


def _stub_modelscope_to_avoid_torch_import() -> None:
    if "modelscope" in sys.modules:
        return

    def _lazy_snapshot_download(*args, **kwargs):
        del sys.modules["modelscope"]
        import modelscope as _real  # noqa: PLC0415
        return _real.snapshot_download(*args, **kwargs)

    stub = types.ModuleType("modelscope")
    stub.snapshot_download = _lazy_snapshot_download  # type: ignore[attr-defined]
    sys.modules["modelscope"] = stub


_stub_modelscope_to_avoid_torch_import()

# ── Dependency guard ────────────────────────────────────────────────────────────

try:
    from contextlib import asynccontextmanager
    from fastapi import FastAPI, File, HTTPException, Query, UploadFile
    from fastapi.responses import JSONResponse
    import uvicorn
except ImportError as exc:
    print(
        f"Missing dependency: {exc}\n"
        "Run: pip install fastapi uvicorn python-multipart",
        file=sys.stderr,
    )
    sys.exit(2)


def _module_installed(name: str) -> bool:
    import importlib.util
    return importlib.util.find_spec(name) is not None


# ── Paddle new-IR workaround (same as paddle_ocr.py) ───────────────────────────

def _disable_new_ir() -> None:
    """
    PaddleOCR 3.7.0 enables PIR by default on CPU, triggering a Windows oneDNN
    bug. Disable it before the model loads.
    """
    try:
        import paddle.inference as pi  # noqa: PLC0415
        pi.Config.enable_new_ir = lambda self, v=True: None
    except Exception:
        pass


# Set from the --device CLI flag in __main__ before uvicorn starts serving;
# read by _load_paddle() when the paddle engine is first lazily loaded.
_DEVICE_OVERRIDE = "auto"
# Set by _load_paddle() once the engine actually loads — reported on /health.
_ACTUAL_PADDLE_DEVICE = None

# ── Engine registry ─────────────────────────────────────────────────────────────
#
# Each engine entry: module (for install detection), lazy loader, recognizer.
# Engines are loaded at most once and reused; recognition is serialized per
# engine (none of these libraries are thread-safe).

class _Engine:
    def __init__(self, key: str, module: str, loader, recognizer):
        self.key = key
        self.module = module
        self._loader = loader
        self._recognizer = recognizer
        self._instance = None
        self._lock = threading.Lock()
        self._load_error: str | None = None

    @property
    def installed(self) -> bool:
        return _module_installed(self.module)

    @property
    def loaded(self) -> bool:
        return self._instance is not None

    @property
    def state(self) -> str:
        if self._load_error:
            return f"failed: {self._load_error}"
        if self.loaded:
            return "loaded"
        return "available" if self.installed else "not_installed"

    def ensure_loaded(self):
        if self._instance is not None:
            return self._instance
        with self._lock:
            if self._instance is None:
                print(f"Loading {self.key} engine…", flush=True)
                try:
                    self._instance = self._loader()
                except Exception as exc:  # noqa: BLE001
                    self._load_error = str(exc)
                    raise
                print(f"{self.key} engine loaded.", flush=True)
        return self._instance

    def recognize(self, image_path: str) -> list[dict]:
        """Returns [{"text", "confidence", "box": [x1,y1,x2,y2]}, ...]."""
        instance = self.ensure_loaded()
        with self._lock:
            return self._recognizer(instance, image_path)


def _load_paddle():
    try:
        import paddle  # noqa: F401, PLC0415 — must be imported before paddleocr
        from paddleocr import PaddleOCR  # noqa: PLC0415
    except ImportError as exc:
        raise RuntimeError(
            f"PaddleOCR not installed: {exc}. Run: pip install paddleocr paddlepaddle"
        ) from exc

    _disable_new_ir()

    def _gpu_available() -> bool:
        try:
            return paddle.device.is_compiled_with_cuda() and paddle.device.cuda.device_count() > 0
        except Exception:
            return False

    # --device auto (default) picks GPU when this environment's paddlepaddle
    # build supports it (paddlepaddle-gpu) and a CUDA GPU is present, otherwise
    # CPU. --device gpu forces GPU even if detection looks uncertain (fails
    # loudly if paddle truly can't use it); --device cpu always uses CPU even
    # on a GPU-capable install (useful if the GPU is busy with something else).
    if _DEVICE_OVERRIDE == "gpu":
        device = "gpu"
    elif _DEVICE_OVERRIDE == "cpu":
        device = "cpu"
    else:
        device = "gpu" if _gpu_available() else "cpu"
    print(f"PaddleOCR device: {device} (mode={_DEVICE_OVERRIDE})", flush=True)
    global _ACTUAL_PADDLE_DEVICE
    _ACTUAL_PADDLE_DEVICE = device

    # Speed-optimized for live game-subtitle OCR:
    #  - mobile det/rec models (~5-10x faster than server models on CPU)
    #  - document orientation / unwarping / textline orientation disabled
    #    (game subtitles are always horizontal screen-space text)
    return PaddleOCR(
        text_detection_model_name="PP-OCRv5_mobile_det",
        text_recognition_model_name="en_PP-OCRv5_mobile_rec",
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        enable_mkldnn=False,  # oneDNN bug on Windows → HTTP 500 when enabled (CPU only)
        device=device,
    )


def _recognize_paddle(ocr, image_path: str) -> list[dict]:
    raw = list(ocr.predict(image_path))
    lines: list[dict] = []

    for result in (raw or []):
        if result is None:
            continue
        rec_texts = result.get("rec_texts", []) or []
        rec_scores = result.get("rec_scores", []) or []
        rec_boxes = result.get("rec_boxes")
        rec_polys = result.get("rec_polys")

        def _box_at(i: int) -> list[int]:
            """Return [x1, y1, x2, y2] for line i, or [-1]*4 when unavailable."""
            try:
                if rec_boxes is not None and len(rec_boxes) > i:
                    b = rec_boxes[i]
                    return [int(b[0]), int(b[1]), int(b[2]), int(b[3])]
                if rec_polys is not None and len(rec_polys) > i:
                    p = rec_polys[i]
                    xs = [int(pt[0]) for pt in p]
                    ys = [int(pt[1]) for pt in p]
                    return [min(xs), min(ys), max(xs), max(ys)]
            except Exception:
                pass
            return [-1, -1, -1, -1]

        for i, (text, score) in enumerate(zip(rec_texts, rec_scores)):
            lines.append({
                "text": str(text),
                "confidence": round(float(score), 4),
                "box": _box_at(i),
            })

    return lines


def _load_rapid():
    from rapidocr_onnxruntime import RapidOCR  # noqa: PLC0415
    return RapidOCR()


def _recognize_rapid(engine, image_path: str) -> list[dict]:
    result, _elapsed = engine(image_path)
    lines: list[dict] = []
    for item in (result or []):
        # item = [box (4 points), text, score]
        box_points, text, score = item[0], item[1], item[2]
        try:
            xs = [int(pt[0]) for pt in box_points]
            ys = [int(pt[1]) for pt in box_points]
            box = [min(xs), min(ys), max(xs), max(ys)]
        except Exception:
            box = [-1, -1, -1, -1]
        lines.append({
            "text": str(text),
            "confidence": round(float(score), 4),
            "box": box,
        })
    return lines


def _load_easy():
    import easyocr  # noqa: PLC0415
    return easyocr.Reader(["en"], gpu=False, verbose=False)


def _recognize_easy(reader, image_path: str) -> list[dict]:
    result = reader.readtext(image_path)
    lines: list[dict] = []
    for box_points, text, score in (result or []):
        try:
            xs = [int(pt[0]) for pt in box_points]
            ys = [int(pt[1]) for pt in box_points]
            box = [min(xs), min(ys), max(xs), max(ys)]
        except Exception:
            box = [-1, -1, -1, -1]
        lines.append({
            "text": str(text),
            "confidence": round(float(score), 4),
            "box": box,
        })
    return lines


ENGINES: dict[str, _Engine] = {
    "paddle": _Engine("paddle", "paddleocr", _load_paddle, _recognize_paddle),
    "rapid": _Engine("rapid", "rapidocr_onnxruntime", _load_rapid, _recognize_rapid),
    "easy": _Engine("easy", "easyocr", _load_easy, _recognize_easy),
}


# ── App ─────────────────────────────────────────────────────────────────────────

@asynccontextmanager
async def _lifespan(app: "FastAPI"):
    # PaddleOCR is the default engine — load it eagerly so the first request is
    # fast. Rapid/Easy stay lazy to keep RAM low until actually selected.
    paddle_engine = ENGINES["paddle"]
    if paddle_engine.installed:
        try:
            paddle_engine.ensure_loaded()
        except Exception as exc:  # noqa: BLE001
            print(f"PaddleOCR failed to load: {exc}", file=sys.stderr, flush=True)
    else:
        print(
            "PaddleOCR is not installed — 'paddle' engine unavailable. "
            "Run: pip install paddleocr paddlepaddle",
            file=sys.stderr,
            flush=True,
        )
    print("OCR server ready.", flush=True)
    yield


app = FastAPI(title="PsGameTranslator OCR Server", version="2.0.0", lifespan=_lifespan)


# ── Endpoints ───────────────────────────────────────────────────────────────────

@app.get("/health")
async def health() -> dict:
    return {
        "status": "ok",
        # Back-compat: existing clients gate on model_loaded == paddle readiness.
        "model_loaded": ENGINES["paddle"].loaded,
        "engines": {key: engine.state for key, engine in ENGINES.items()},
        "paddleDevice": _ACTUAL_PADDLE_DEVICE,
    }


@app.post("/ocr")
def ocr_endpoint(
    file: UploadFile = File(...),
    engine: str = Query(default="paddle"),
) -> JSONResponse:
    # Sync endpoint → FastAPI runs it in a worker thread, so the event loop
    # (and /health) stays responsive while OCR is in progress.
    selected = ENGINES.get(engine.lower())
    if selected is None:
        raise HTTPException(
            status_code=400,
            detail=f"Unknown OCR engine '{engine}'. Valid engines: {', '.join(ENGINES)}",
        )

    if not selected.installed:
        install_hint = {
            "paddle": "pip install paddleocr paddlepaddle",
            "rapid": "pip install rapidocr-onnxruntime",
            "easy": "pip install easyocr",
        }.get(selected.key, "")
        raise HTTPException(
            status_code=501,
            detail=(
                f"OCR engine '{selected.key}' is not installed on the server. "
                f"Run: {install_hint}"
            ),
        )

    image_bytes = file.file.read()

    # Engines accept a file path, so write to a temp file.
    suffix = os.path.splitext(file.filename or ".png")[1] or ".png"
    with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
        tmp.write(image_bytes)
        tmp_path = tmp.name

    try:
        lines = selected.recognize(tmp_path)
    except Exception as exc:  # noqa: BLE001
        raise HTTPException(
            status_code=500,
            detail=f"OCR runtime error ({selected.key}): {exc}",
        ) from exc
    finally:
        try:
            os.unlink(tmp_path)
        except OSError:
            pass

    confidences = [item["confidence"] for item in lines]
    full_text = "\n".join(item["text"] for item in lines)
    avg_confidence = sum(confidences) / len(confidences) if confidences else 0.0

    return JSONResponse(
        content={
            "text": full_text,
            "confidence": round(avg_confidence, 4),
            "lines": lines,
            "engine": selected.key,
        }
    )


# ── Entry point ─────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="PsGameTranslator OCR Server")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument(
        "--device", choices=["auto", "cpu", "gpu"], default="auto",
        help="PaddleOCR compute device. 'auto' uses the GPU when paddlepaddle-gpu "
             "is installed and a CUDA GPU is present, otherwise CPU.")
    args = parser.parse_args()

    _DEVICE_OVERRIDE = args.device
    uvicorn.run(app, host=args.host, port=args.port, log_level="info")
