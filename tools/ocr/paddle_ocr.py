"""
paddle_ocr.py — PaddleOCR 3.7.0 adapter for PsGameTranslator.

Usage:
    python paddle_ocr.py <image_path>

Output (stdout):
    JSON with keys: text, confidence, lines[]

Exit codes:
    0  success
    1  bad arguments
    2  PaddleOCR not installed
    3  runtime error
"""

import sys
import json
import os
import types


def _stub_modelscope_to_avoid_torch_import() -> None:
    """
    paddlex (a paddleocr dependency) unconditionally imports `modelscope` at
    module load time to offer it as a model-download host. modelscope in turn
    unconditionally imports torch, whose Windows DLL loader has a known,
    order-dependent flakiness (OSError: WinError 127 loading shm.dll) that can
    crash the process before OCR ever runs — even though this app never needs
    ModelScope's hub. This stub lets `import modelscope` succeed instantly
    without touching torch; a real ModelScope download (if ever requested)
    lazily imports the real package at that point instead.
    """
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


def _disable_new_ir() -> None:
    """
    PaddleOCR 3.7.0 enables PIR (new IR) by default on CPU, which triggers
    a Windows oneDNN bug: ConvertPirAttribute2RuntimeAttribute not support
    pir::ArrayAttribute<pir::DoubleAttribute>.
    Disabling it here (before PaddleOCR imports paddle) avoids the crash.
    """
    try:
        import paddle.inference as paddle_inference  # noqa: PLC0415
        paddle_inference.Config.enable_new_ir = lambda self, v=True: None
    except Exception:
        pass  # not fatal — will surface during predict() if still broken


def main() -> None:
    if len(sys.argv) < 2:
        _error_exit("No image path provided. Usage: paddle_ocr.py <image_path>", code=1)

    image_path = os.path.abspath(sys.argv[1])

    try:
        import paddle  # noqa: PLC0415 — import must happen before paddleocr
        _disable_new_ir()
        from paddleocr import PaddleOCR  # type: ignore[import]  # noqa: PLC0415
    except ImportError:
        _error_exit(
            "PaddleOCR is not installed. "
            "Run: pip install paddleocr paddlepaddle",
            code=2,
        )
        return  # unreachable — satisfies type checker

    try:
        ocr = PaddleOCR(
            use_textline_orientation=False,
            lang="en",
            enable_mkldnn=False,
        )
        raw = list(ocr.predict(image_path))
    except Exception as exc:  # noqa: BLE001
        _error_exit(f"PaddleOCR runtime error: {exc}", code=3)
        return

    lines: list[dict] = []
    confidences: list[float] = []

    # PaddleOCR 3.7.0 predict() returns a list of result objects per image.
    # Each result is a dict-like object with 'rec_texts' and 'rec_scores'.
    for result in (raw or []):
        if result is None:
            continue
        rec_texts  = result.get("rec_texts",  []) or []
        rec_scores = result.get("rec_scores", []) or []
        rec_boxes  = result.get("rec_boxes")
        rec_polys  = result.get("rec_polys")

        def _box_at(i: int) -> list[int]:
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
            text  = str(text)
            score = float(score)
            lines.append({"text": text, "confidence": score, "box": _box_at(i)})
            confidences.append(score)

    full_text     = "\n".join(item["text"] for item in lines)
    avg_confidence = sum(confidences) / len(confidences) if confidences else 0.0

    print(json.dumps(
        {"text": full_text, "confidence": avg_confidence, "lines": lines},
        ensure_ascii=False,
    ))


def _error_exit(message: str, code: int = 3) -> None:
    print(json.dumps({"error": message}), file=sys.stdout)
    sys.exit(code)


if __name__ == "__main__":
    main()
