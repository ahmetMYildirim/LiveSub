"""
translation_server.py — Persistent OPUS-MT FastAPI translation server for PsGameTranslator.

Loads Helsinki-NLP/opus-mt-tc-big-en-tr once at startup and serves POST /translate
requests, eliminating per-request model reload latency.

Usage:
    python tools/translation/translation_server.py [--host 127.0.0.1] [--port 8770]

Dependencies:
    pip install -r tools/translation/requirements.txt

Endpoints:
    GET  /health     → {"status": "ok", "model_loaded": true, "model": "..."}
    POST /translate  → JSON body  → {"translation", "provider", "durationMs", "success", "error"}
"""

import argparse
import re
import sys
import time

# ── Dependency guard ─────────────────────────────────────────────────────────────

try:
    from contextlib import asynccontextmanager
    from fastapi import FastAPI
    from fastapi.responses import JSONResponse
    from pydantic import BaseModel
    import uvicorn
except ImportError as exc:
    print(
        f"Missing dependency: {exc}\n"
        "Run: pip install fastapi uvicorn",
        file=sys.stderr,
    )
    sys.exit(2)

try:
    from transformers import AutoModelForSeq2SeqLM, AutoTokenizer
    import torch
except ImportError as exc:
    print(
        f"Missing dependency: {exc}\n"
        "Run: pip install transformers sentencepiece sacremoses torch",
        file=sys.stderr,
    )
    sys.exit(2)

# peft is optional — only needed when loading a LoRA fine-tuned adapter.
try:
    from peft import PeftModel
    _peft_available = True
except ImportError:
    _peft_available = False

# ── Constants ────────────────────────────────────────────────────────────────────

import os

# Selectable via --model / TRANSLATION_MODEL; any HF seq2seq translation model.
# MarianMT (Helsinki-NLP/opus-mt-*) and NLLB models are supported.
MODEL_NAME  = os.environ.get("TRANSLATION_MODEL", "Helsinki-NLP/opus-mt-tc-big-en-tr")
PROVIDER_ID = MODEL_NAME.split("/")[-1]

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8770

FAST_MAX_NEW_TOKENS = 80
# Greedy decoding (num_beams=1) is the biggest quality drop for a small model
# like opus-mt-tc-big-en-tr — it frequently picks an awkward or wrong first
# word and cannot recover. 2 beams already recovers most of that quality; we
# stay at 2 (not 3+) on purpose because OPUS shares the GPU with the game, so
# extra beams cost latency that would push more lines past the request timeout.
FAST_NUM_BEAMS = 1
FAST_EARLY_STOPPING = True
FAST_NO_REPEAT_NGRAM = 3
QUALITY_MAX_NEW_TOKENS = 128
QUALITY_NUM_BEAMS = 5
QUALITY_EARLY_STOPPING = True
QUALITY_NO_REPEAT_NGRAM = 3

# ── Model state (loaded once at startup) ────────────────────────────────────────

_tokenizer = None
_model = None
_device: str = "cpu"
_is_nllb: bool = "nllb" in MODEL_NAME.lower()
# When a sibling "<model>-ct2" directory exists (built with
# ct2-transformers-converter), it's preferred over the plain transformers
# model: ~2-3x faster generation and roughly half the VRAM/disk footprint
# (int8_float16 quantization) for equivalent translation quality. _tokenizer
# is still loaded normally either way — CT2's Translator only does the
# encoder/decoder compute, tokenization stays on the HF tokenizer.
_ct2_translator = None

# NLLB uses FLORES language codes; map the app's ISO codes.
_NLLB_LANG = {"en": "eng_Latn", "tr": "tur_Latn"}
_cache: dict[str, dict] = {}
_stats = {
    "requests": 0,
    "success": 0,
    "errors": 0,
    "cacheHits": 0,
    "fastRequests": 0,
    "qualityRequests": 0,
}

# ── Lifespan ─────────────────────────────────────────────────────────────────────

@asynccontextmanager
async def lifespan(app: FastAPI):
    global _tokenizer, _model, _device, _ct2_translator

    print(f"[translation_server] Loading model {MODEL_NAME} …", flush=True)
    t0 = time.perf_counter()

    _device = "cuda" if torch.cuda.is_available() else "cpu"

    ct2_dir = MODEL_NAME.rstrip("/\\") + "-ct2"
    if os.path.isdir(ct2_dir) and os.path.isfile(os.path.join(ct2_dir, "model.bin")):
        try:
            import ctranslate2
            _tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME)
            _ct2_translator = ctranslate2.Translator(ct2_dir, device=_device)
            elapsed = (time.perf_counter() - t0) * 1000
            print(
                f"[translation_server] CTranslate2 model loaded in {elapsed:.0f} ms  "
                f"device={_device}  dir={ct2_dir}",
                flush=True,
            )
            yield
            return
        except ImportError:
            print(
                "[translation_server] ct2 model found but 'ctranslate2' package is not "
                "installed (pip install ctranslate2) — falling back to transformers.",
                flush=True,
            )
            _ct2_translator = None

    # Detect a local LoRA adapter directory (has adapter_config.json).
    adapter_config_path = os.path.join(MODEL_NAME, "adapter_config.json")
    if os.path.isfile(adapter_config_path):
        if not _peft_available:
            print(
                "[translation_server] ERROR: peft is required for fine-tuned adapters.\n"
                "Run: pip install peft",
                file=sys.stderr,
            )
            sys.exit(2)
        import json as _json
        with open(adapter_config_path) as _f:
            _adapter_cfg = _json.load(_f)
        _base_model = _adapter_cfg.get("base_model_name_or_path", "Helsinki-NLP/opus-mt-tc-big-en-tr")
        print(f"[translation_server] Loading LoRA adapter from {MODEL_NAME} (base: {_base_model}) …", flush=True)
        _tokenizer = AutoTokenizer.from_pretrained(_base_model)
        _base = AutoModelForSeq2SeqLM.from_pretrained(_base_model).to(_device)
        _model = PeftModel.from_pretrained(_base, MODEL_NAME).to(_device)
    else:
        _tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME)
        _model     = AutoModelForSeq2SeqLM.from_pretrained(MODEL_NAME).to(_device)

    # fp16 roughly halves matmul time on CUDA for a model this size, with no
    # meaningful quality loss for translation — GPU only, CPU has no benefit
    # (and some ops aren't implemented for fp16 on CPU).
    if _device == "cuda":
        _model = _model.half()

    _model.eval()

    elapsed = (time.perf_counter() - t0) * 1000
    print(
        f"[translation_server] Model loaded in {elapsed:.0f} ms  device={_device}",
        flush=True,
    )
    yield
    # Nothing to clean up for a simple inference server.

# ── App ───────────────────────────────────────────────────────────────────────────

app = FastAPI(title="PsGameTranslator Translation Server", lifespan=lifespan)

# ── Request / response schemas ────────────────────────────────────────────────────

class TranslateRequest(BaseModel):
    text: str
    sourceLanguage: str = "en"
    targetLanguage: str = "tr"
    mode: str = "fast"

class TranslateResponse(BaseModel):
    translation: str
    provider: str
    durationMs: int
    success: bool
    error: str | None
    fromCache: bool = False
    mode: str = "fast"

# ── Endpoints ─────────────────────────────────────────────────────────────────────

@app.get("/health")
def health():
    loaded = _model is not None or _ct2_translator is not None
    return {
        "status": "ok",
        "provider": PROVIDER_ID,
        "modelLoaded": loaded,
        "model_loaded": loaded,  # backward-compatible alias
        "model": MODEL_NAME,
        "device": _device,
        "backend": "ctranslate2" if _ct2_translator is not None else "transformers",
    }


@app.get("/stats")
def stats():
    return {
        **_stats,
        "cacheSize": len(_cache),
        "provider": PROVIDER_ID,
        "modelLoaded": _model is not None or _ct2_translator is not None,
        "device": _device,
    }


@app.post("/clear-cache")
def clear_cache():
    cleared = len(_cache)
    _cache.clear()
    return {"success": True, "cleared": cleared}


@app.post("/translate", response_model=TranslateResponse)
def translate(request: TranslateRequest):
    text = (request.text or "").strip()
    mode = (request.mode or "fast").strip().lower()
    if mode not in {"fast", "quality"}:
        mode = "fast"

    _stats["requests"] += 1
    _stats["fastRequests" if mode == "fast" else "qualityRequests"] += 1

    if not text:
        _stats["errors"] += 1
        return TranslateResponse(
            translation="",
            provider=PROVIDER_ID,
            durationMs=0,
            success=False,
            error="Input text is null or empty.",
            mode=mode,
        )

    if (_model is None and _ct2_translator is None) or _tokenizer is None:
        _stats["errors"] += 1
        return TranslateResponse(
            translation="",
            provider=PROVIDER_ID,
            durationMs=0,
            success=False,
            error="Model is not loaded yet. Try again in a moment.",
            mode=mode,
        )

    cache_key = _cache_key(request.sourceLanguage, request.targetLanguage, text, mode)
    cached = _cache.get(cache_key)
    if cached is not None:
        _stats["cacheHits"] += 1
        return TranslateResponse(
            translation=cached["translation"],
            provider=PROVIDER_ID,
            durationMs=0,
            success=True,
            error=None,
            fromCache=True,
            mode=mode,
        )

    print(
        f"[translation_server] translate  len={len(text)}  text=\"{text[:60]}{'…' if len(text) > 60 else ''}\"",
        flush=True,
    )

    t0 = time.perf_counter()
    try:
        if _ct2_translator is not None:
            # CTranslate2 path: tokenize with the HF tokenizer (subword tokens,
            # not IDs), let CT2 do the encoder/decoder compute, then decode the
            # returned tokens with the same HF tokenizer. ~2-3x faster and half
            # the VRAM of the transformers path for equivalent output quality
            # (verified against the fp16 transformers path on real subtitle
            # lines before this was wired in).
            src_tokens = _tokenizer.convert_ids_to_tokens(_tokenizer.encode(text))
            beam_size = QUALITY_NUM_BEAMS if mode == "quality" else FAST_NUM_BEAMS
            max_len = QUALITY_MAX_NEW_TOKENS if mode == "quality" else FAST_MAX_NEW_TOKENS
            result = _ct2_translator.translate_batch(
                [src_tokens],
                beam_size=beam_size,
                max_decoding_length=max_len,
            )
            out_tokens = result[0].hypotheses[0]
            translation = _tokenizer.decode(
                _tokenizer.convert_tokens_to_ids(out_tokens), skip_special_tokens=True
            )
        else:
            if _is_nllb:
                _tokenizer.src_lang = _NLLB_LANG.get(request.sourceLanguage, "eng_Latn")

            inputs = _tokenizer(
                text,
                return_tensors="pt",
                padding=True,
                truncation=True,
                max_length=512,
            ).to(_device)

            generate_kwargs = {}
            if _is_nllb:
                tgt = _NLLB_LANG.get(request.targetLanguage, "tur_Latn")
                generate_kwargs["forced_bos_token_id"] = _tokenizer.convert_tokens_to_ids(tgt)

            with torch.no_grad():
                if mode == "quality":
                    output_ids = _model.generate(
                        **inputs,
                        max_new_tokens=QUALITY_MAX_NEW_TOKENS,
                        num_beams=QUALITY_NUM_BEAMS,
                        do_sample=False,
                        early_stopping=QUALITY_EARLY_STOPPING,
                        no_repeat_ngram_size=QUALITY_NO_REPEAT_NGRAM,
                        **generate_kwargs,
                    )
                else:
                    output_ids = _model.generate(
                        **inputs,
                        max_new_tokens=FAST_MAX_NEW_TOKENS,
                        num_beams=FAST_NUM_BEAMS,
                        do_sample=False,
                        early_stopping=FAST_EARLY_STOPPING,
                        no_repeat_ngram_size=FAST_NO_REPEAT_NGRAM,
                        **generate_kwargs,
                    )

            translation = _tokenizer.decode(output_ids[0], skip_special_tokens=True)
        elapsed_ms  = int((time.perf_counter() - t0) * 1000)

        print(
            f"[translation_server] done  {elapsed_ms} ms  → \"{translation[:60]}{'…' if len(translation) > 60 else ''}\"",
            flush=True,
        )

        _stats["success"] += 1
        _cache[cache_key] = {
            "translation": translation,
            "durationMs": elapsed_ms,
            "mode": mode,
        }

        return TranslateResponse(
            translation=translation,
            provider=PROVIDER_ID,
            durationMs=elapsed_ms,
            success=True,
            error=None,
            fromCache=False,
            mode=mode,
        )

    except Exception as exc:
        elapsed_ms = int((time.perf_counter() - t0) * 1000)
        print(f"[translation_server] ERROR  {elapsed_ms} ms  {exc}", flush=True)
        _stats["errors"] += 1
        return TranslateResponse(
            translation="",
            provider=PROVIDER_ID,
            durationMs=elapsed_ms,
            success=False,
            error=str(exc),
            mode=mode,
        )


def _cache_key(source_language: str, target_language: str, text: str, mode: str) -> str:
    normalized = re.sub(r"\s+", " ", text.strip().lower())
    return f"{source_language}|{target_language}|{normalized}|{mode}"

# ── Entry point ───────────────────────────────────────────────────────────────────

def _parse_args():
    parser = argparse.ArgumentParser(description="Machine translation server")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument(
        "--model", default=None,
        help="HuggingFace model id (MarianMT/NLLB). Overrides TRANSLATION_MODEL env var.")
    return parser.parse_args()


if __name__ == "__main__":
    args = _parse_args()
    if args.model:
        MODEL_NAME = args.model
        PROVIDER_ID = MODEL_NAME.split("/")[-1]
        _is_nllb = "nllb" in MODEL_NAME.lower()
    print(
        f"[translation_server] Starting on http://{args.host}:{args.port}  model={MODEL_NAME}",
        flush=True,
    )
    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")
