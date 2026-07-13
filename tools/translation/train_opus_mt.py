"""
Fine-tunes the local OPUS-MT (MarianMT) translation model on a dataset of
DeepL / Google Translate output pairs, using LoRA (via peft) for a small,
fast-to-train adapter instead of touching every model weight.

Every line of progress meant for the WPF app is printed as:
    PROGRESS_JSON:{...}
so PsGameTranslator.App.Services.TrainingService can parse it line-by-line
while it streams to the live log console. Anything else printed is plain
human-readable log output, shown in the console too but not parsed.

Requires (on top of what translation_server.py already needs):
    pip install peft sacrebleu psutil datasets

Usage:
    python train_opus_mt.py --estimate-only --batch-size 8 --max-length 128
    python train_opus_mt.py --dataset-files dataset_deepl.jsonl dataset_google.jsonl \
        --output-dir ../../models/opus-mt-finetuned --epochs 3 --batch-size 8
"""

from __future__ import annotations

import argparse
import json
import os
import random
import sys
import time
from dataclasses import dataclass


def emit(payload: dict) -> None:
    """Structured progress line for the C# side. Never buffered."""
    print("PROGRESS_JSON:" + json.dumps(payload, ensure_ascii=False), flush=True)


def log(message: str) -> None:
    print(f"[train_opus_mt] {message}", flush=True)


def emit_error(message: str) -> None:
    emit({"type": "error", "message": message})
    log(f"ERROR: {message}")


@dataclass
class Example:
    source: str
    target: str
    provider: str


def load_dataset_files(paths: list[str]) -> list[Example]:
    examples: list[Example] = []
    for path in paths:
        if not os.path.exists(path):
            log(f"Dataset file not found, skipping: {path}")
            continue
        count_before = len(examples)
        with open(path, "r", encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                source = (obj.get("source") or obj.get("Source") or "").strip()
                target = (obj.get("target") or obj.get("Target") or "").strip()
                provider = (obj.get("provider") or obj.get("Provider") or "unknown").strip()
                if source and target:
                    examples.append(Example(source, target, provider))
        log(f"Loaded {len(examples) - count_before} pairs from {os.path.basename(path)}")

    # Drop exact-duplicate (source, target) pairs — repeated NPC barks would
    # otherwise dominate the gradient with the same sentence over and over.
    seen: set[tuple[str, str]] = set()
    deduped: list[Example] = []
    for ex in examples:
        key = (ex.source.lower(), ex.target.lower())
        if key in seen:
            continue
        seen.add(key)
        deduped.append(ex)
    if len(deduped) != len(examples):
        log(f"Deduplicated {len(examples) - len(deduped)} repeated pairs")

    before_garbage = len(deduped)
    deduped = [ex for ex in deduped if not _is_probably_ocr_garbage(ex.source)]
    if len(deduped) != before_garbage:
        log(f"Dropped {before_garbage - len(deduped)} likely-OCR-garbage pairs")

    before_partial = len(deduped)
    deduped = _drop_typewriter_partials(deduped)
    if len(deduped) != before_partial:
        log(f"Dropped {before_partial - len(deduped)} typewriter partial-sentence duplicates")

    return deduped


def _is_probably_ocr_garbage(source: str) -> bool:
    """Mirrors the C# SubtitleCandidateValidator heuristics on the Python side —
    a stray OCR fragment ('ieh', 'Shoul') is worse than useless as training
    data: it teaches the model to treat noise as valid input."""
    s = source.strip()
    if len(s) < 4:
        return True
    letters = sum(c.isalpha() for c in s)
    if letters < len(s) * 0.6:
        return True
    tokens = s.split()
    if len(tokens) == 1 and s[-1] not in ".!?\"'" and len(s) < 8:
        return True
    return False


def _drop_typewriter_partials(examples: list[Example]) -> list[Example]:
    """
    A game with a "typewriter" subtitle reveal produces several OCR reads of
    the same growing line before it finishes printing. Only the direct
    substring/PathMerge case (one Source is a prefix of a later, longer one)
    is caught here — OCR noise at the very start/end of a read can still
    slip past this as a near-duplicate, which is a known limitation; exact
    prefix matching is deliberately conservative so it never discards two
    genuinely different short lines.
    """
    sources = [ex.source for ex in examples]
    keep = [True] * len(examples)
    for i, si in enumerate(sources):
        if not keep[i]:
            continue
        for j, sj in enumerate(sources):
            if i == j or not keep[j]:
                continue
            if len(sj) > len(si) and sj.lower().startswith(si.lower()):
                keep[i] = False
                break
    return [ex for ex, k in zip(examples, keep) if k]


def estimate_vram_mb(param_count: int, batch_size: int, max_length: int, lora_r: int) -> dict:
    """
    Rough estimate, not a guarantee — real usage depends on the specific model
    architecture, attention implementation, and CUDA allocator fragmentation.
    Formula: base weights (fp16, frozen) + LoRA adapter weights/gradients/optimizer
    state (fp32 Adam: param + grad + 2 momentum terms) + activation memory that
    scales with batch_size * max_length. This intentionally errs on the
    generous side so a user is warned before an out-of-memory crash rather
    than surprised by one.
    """
    bytes_per_param_frozen = 2  # fp16 base model, frozen (no grad/optimizer state)
    base_mb = (param_count * bytes_per_param_frozen) / (1024 * 1024)

    # LoRA adapters are a small fraction of total params; approximate as 1-2%
    # of base params per typical r=8-16 config, trained in fp32 with Adam
    # (param + grad + 2 optimizer moments = 4x).
    lora_param_fraction = min(0.05, 0.002 * lora_r)
    lora_params = param_count * lora_param_fraction
    lora_mb = (lora_params * 4 * 4) / (1024 * 1024)  # fp32, x4 for Adam state

    # Activations scale with batch * sequence length; this coefficient is a
    # coarse empirical approximation for a MarianMT-sized encoder-decoder.
    activation_mb = (batch_size * max_length * 1536) / (1024 * 1024)

    overhead_mb = 700  # CUDA context, cuDNN workspace, fragmentation headroom
    total_mb = base_mb + lora_mb + activation_mb + overhead_mb
    return {
        "base_model_mb": round(base_mb),
        "lora_adapter_mb": round(lora_mb),
        "activations_mb": round(activation_mb),
        "overhead_mb": overhead_mb,
        "estimated_total_mb": round(total_mb),
    }


def report_live_vram(torch_module) -> None:
    if not torch_module.cuda.is_available():
        return
    allocated = torch_module.cuda.memory_allocated() / (1024 * 1024)
    reserved = torch_module.cuda.memory_reserved() / (1024 * 1024)
    total = torch_module.cuda.get_device_properties(0).total_memory / (1024 * 1024)
    emit({
        "type": "vram_usage",
        "allocated_mb": round(allocated),
        "reserved_mb": round(reserved),
        "total_mb": round(total),
    })


def report_ram() -> None:
    try:
        import psutil
        process = psutil.Process(os.getpid())
        rss_mb = process.memory_info().rss / (1024 * 1024)
        total_mb = psutil.virtual_memory().total / (1024 * 1024)
        emit({"type": "ram_usage", "used_mb": round(rss_mb), "total_mb": round(total_mb)})
    except ImportError:
        pass  # psutil is optional — RAM reporting just won't show up.


def token_level_prf1(predictions: list[str], references: list[str]) -> dict:
    """
    Simple whitespace-token overlap precision/recall/F1, averaged per example.
    This is NOT a standard MT metric (BLEU/chrF are) — it's the familiar
    accuracy-style number that was specifically asked for, reported alongside
    real BLEU rather than instead of it.
    """
    precisions, recalls, f1s = [], [], []
    for pred, ref in zip(predictions, references):
        pred_tokens = pred.lower().split()
        ref_tokens = ref.lower().split()
        if not pred_tokens or not ref_tokens:
            precisions.append(0.0)
            recalls.append(0.0)
            f1s.append(0.0)
            continue
        pred_set, ref_set = set(pred_tokens), set(ref_tokens)
        overlap = len(pred_set & ref_set)
        precision = overlap / len(pred_set) if pred_set else 0.0
        recall = overlap / len(ref_set) if ref_set else 0.0
        f1 = (2 * precision * recall / (precision + recall)) if (precision + recall) > 0 else 0.0
        precisions.append(precision)
        recalls.append(recall)
        f1s.append(f1)

    n = max(1, len(precisions))
    return {
        "precision": round(sum(precisions) / n, 4),
        "recall": round(sum(recalls) / n, 4),
        "f1": round(sum(f1s) / n, 4),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-model", default=os.environ.get("TRANSLATION_MODEL", "Helsinki-NLP/opus-mt-tc-big-en-tr"))
    parser.add_argument("--dataset-files", nargs="*", default=[])
    parser.add_argument("--test-files", nargs="*", default=[],
                         help="Held-out files evaluated ONCE after training — never seen during train/val.")
    parser.add_argument("--output-dir", default="./models/opus-mt-finetuned")
    parser.add_argument("--epochs", type=int, default=3)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--learning-rate", type=float, default=2e-4)
    parser.add_argument("--max-length", type=int, default=128)
    parser.add_argument("--val-split", type=float, default=0.1)
    parser.add_argument("--lora-r", type=int, default=8)
    parser.add_argument("--lora-alpha", type=int, default=16)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--estimate-only", action="store_true",
                         help="Print the VRAM estimate for these settings and exit — no data/model loading.")
    args = parser.parse_args()

    random.seed(args.seed)

    try:
        import torch
    except ImportError:
        emit_error("PyTorch is not installed. Run: pip install torch --index-url https://download.pytorch.org/whl/cu121")
        return 1

    device = "cuda" if torch.cuda.is_available() else "cpu"
    if device == "cpu":
        log("WARNING: no CUDA GPU detected — this will be very slow (hours instead of minutes).")

    if args.estimate_only:
        # A MarianMT opus-mt-tc-big checkpoint is ~230M params; approximate
        # without downloading the model so this returns instantly.
        approx_param_count = 230_000_000
        estimate = estimate_vram_mb(approx_param_count, args.batch_size, args.max_length, args.lora_r)
        emit({"type": "vram_estimate", "device": device, **estimate})
        return 0

    if not args.dataset_files:
        emit_error("No dataset files given — nothing to train on.")
        return 1

    try:
        from transformers import (
            AutoModelForSeq2SeqLM,
            AutoTokenizer,
            DataCollatorForSeq2Seq,
            Seq2SeqTrainer,
            Seq2SeqTrainingArguments,
            TrainerCallback,
        )
        from peft import LoraConfig, get_peft_model, TaskType
        import sacrebleu
    except ImportError as exc:
        emit_error(f"Missing package: {exc}. Run: pip install transformers peft sacrebleu psutil datasets")
        return 1

    examples = load_dataset_files(args.dataset_files)
    if len(examples) < 10:
        emit_error(f"Only {len(examples)} usable pairs found — need at least 10 to train.")
        return 1

    by_provider: dict[str, int] = {}
    for ex in examples:
        by_provider[ex.provider] = by_provider.get(ex.provider, 0) + 1
    emit({"type": "dataset_loaded", "total_pairs": len(examples), "by_provider": by_provider})

    random.shuffle(examples)
    val_count = max(1, int(len(examples) * args.val_split))
    val_examples = examples[:val_count]
    train_examples = examples[val_count:]
    emit({"type": "dataset_split", "train_pairs": len(train_examples), "val_pairs": len(val_examples)})

    log(f"Loading base model {args.base_model} on {device} …")
    load_start = time.time()
    tokenizer = AutoTokenizer.from_pretrained(args.base_model)
    model = AutoModelForSeq2SeqLM.from_pretrained(args.base_model).to(device)
    log(f"Base model loaded in {time.time() - load_start:.1f}s")

    param_count = sum(p.numel() for p in model.parameters())
    estimate = estimate_vram_mb(param_count, args.batch_size, args.max_length, args.lora_r)
    emit({"type": "vram_estimate", "device": device, **estimate})

    lora_config = LoraConfig(
        task_type=TaskType.SEQ_2_SEQ_LM,
        r=args.lora_r,
        lora_alpha=args.lora_alpha,
        lora_dropout=0.05,
        target_modules=["k_proj", "v_proj", "q_proj", "out_proj"],
    )
    model = get_peft_model(model, lora_config)
    trainable, total = model.get_nb_trainable_parameters()
    emit({"type": "lora_ready", "trainable_params": trainable, "total_params": total})

    def to_hf_dataset(rows: list[Example]):
        from datasets import Dataset
        return Dataset.from_dict({
            "source": [r.source for r in rows],
            "target": [r.target for r in rows],
        })

    def preprocess(batch):
        model_inputs = tokenizer(batch["source"], max_length=args.max_length, truncation=True)
        labels = tokenizer(text_target=batch["target"], max_length=args.max_length, truncation=True)
        model_inputs["labels"] = labels["input_ids"]
        return model_inputs

    train_ds = to_hf_dataset(train_examples).map(preprocess, batched=True, remove_columns=["source", "target"])
    val_ds = to_hf_dataset(val_examples).map(preprocess, batched=True, remove_columns=["source", "target"])

    class ProgressCallback(TrainerCallback):
        def __init__(self, total_epochs: int):
            self.total_epochs = total_epochs
            self.last_vram_report = 0.0

        def on_log(self, args_, state, control, logs=None, **kwargs):
            if not logs:
                return
            if "loss" in logs:
                emit({
                    "type": "train_progress",
                    "epoch": round(state.epoch or 0, 2),
                    "total_epochs": self.total_epochs,
                    "step": state.global_step,
                    "total_steps": state.max_steps,
                    "train_loss": logs["loss"],
                })
            if "eval_loss" in logs:
                emit({
                    "type": "eval_progress",
                    "epoch": round(state.epoch or 0, 2),
                    "eval_loss": logs["eval_loss"],
                })
            now = time.time()
            if now - self.last_vram_report > 5:
                report_live_vram(torch)
                report_ram()
                self.last_vram_report = now

    training_args = Seq2SeqTrainingArguments(
        output_dir=args.output_dir,
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        per_device_eval_batch_size=args.batch_size,
        learning_rate=args.learning_rate,
        eval_strategy="epoch",
        save_strategy="epoch",
        save_total_limit=2,
        logging_steps=5,
        predict_with_generate=True,
        fp16=(device == "cuda"),
        report_to=[],
        load_best_model_at_end=True,
        metric_for_best_model="eval_loss",
    )

    data_collator = DataCollatorForSeq2Seq(tokenizer, model=model)

    trainer = Seq2SeqTrainer(
        model=model,
        args=training_args,
        train_dataset=train_ds,
        eval_dataset=val_ds,
        data_collator=data_collator,
        callbacks=[ProgressCallback(args.epochs)],
    )

    emit({"type": "training_started", "epochs": args.epochs, "train_pairs": len(train_examples)})
    try:
        trainer.train()
    except torch.cuda.OutOfMemoryError:
        emit_error(
            "CUDA out of memory. Lower batch size or max sequence length and try again "
            "(the VRAM estimate before training is a guide, not a guarantee)."
        )
        return 1
    except Exception as exc:  # noqa: BLE001 — surface any training failure to the UI
        emit_error(f"Training failed: {exc}")
        return 1

    log("Training finished, running validation BLEU + token-overlap metrics …")
    model.eval()
    predictions: list[str] = []
    references: list[str] = [ex.target for ex in val_examples]
    with torch.no_grad():
        for i in range(0, len(val_examples), args.batch_size):
            batch = val_examples[i:i + args.batch_size]
            inputs = tokenizer([ex.source for ex in batch], return_tensors="pt",
                                padding=True, truncation=True, max_length=args.max_length).to(device)
            generated = model.generate(**inputs, max_length=args.max_length)
            predictions.extend(tokenizer.batch_decode(generated, skip_special_tokens=True))

    bleu = sacrebleu.corpus_bleu(predictions, [references])
    prf1 = token_level_prf1(predictions, references)
    emit({
        "type": "eval_metrics",
        "bleu": round(bleu.score, 2),
        "precision": prf1["precision"],
        "recall": prf1["recall"],
        "f1": prf1["f1"],
        "val_pairs": len(val_examples),
    })

    os.makedirs(args.output_dir, exist_ok=True)
    # Save the LoRA weights merged into the base model rather than the raw
    # adapter. The unmerged PeftModel wraps every linear layer with an extra
    # forward pass at inference time, which roughly doubles per-request
    # latency in the translation server for no quality benefit once training
    # is done — merging once here keeps runtime inference exactly as fast as
    # the unmodified base model.
    save_model = model.merge_and_unload() if hasattr(model, "merge_and_unload") else model
    save_model.save_pretrained(args.output_dir)
    tokenizer.save_pretrained(args.output_dir)
    emit({"type": "done", "output_dir": os.path.abspath(args.output_dir)})

    # ── Held-out test set evaluation (runs after model is saved) ─────────────
    if args.test_files:
        test_examples = load_dataset_files(args.test_files)
        if len(test_examples) >= 2:
            log(f"Running held-out test evaluation on {len(test_examples)} pairs …")
            test_predictions: list[str] = []
            test_references: list[str] = [ex.target for ex in test_examples]
            model.eval()
            with torch.no_grad():
                for i in range(0, len(test_examples), args.batch_size):
                    batch = test_examples[i:i + args.batch_size]
                    inputs = tokenizer(
                        [ex.source for ex in batch],
                        return_tensors="pt",
                        padding=True,
                        truncation=True,
                        max_length=args.max_length,
                    ).to(device)
                    generated = model.generate(**inputs, max_length=args.max_length)
                    test_predictions.extend(tokenizer.batch_decode(generated, skip_special_tokens=True))

            test_bleu = sacrebleu.corpus_bleu(test_predictions, [test_references])
            test_prf1 = token_level_prf1(test_predictions, test_references)
            emit({
                "type": "test_metrics",
                "bleu": round(test_bleu.score, 2),
                "precision": test_prf1["precision"],
                "recall": test_prf1["recall"],
                "f1": test_prf1["f1"],
                "test_pairs": len(test_examples),
            })
        else:
            log("Test files provided but too few examples to evaluate — skipping.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
