#!/usr/bin/env python3
"""Fine-tune Llama 3.2 Vision 11B with a non-quantized LoRA adapter for Ollama."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import torch
from datasets import Image as DatasetImage
from datasets import load_dataset
from peft import LoraConfig, TaskType
from transformers import AutoModelForImageTextToText, AutoProcessor
from trl import SFTConfig, SFTTrainer


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prepared-root", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--model-id", default="meta-llama/Llama-3.2-11B-Vision-Instruct")
    parser.add_argument("--epochs", type=float, default=4)
    parser.add_argument("--batch-size", type=int, default=1)
    parser.add_argument("--gradient-accumulation", type=int, default=16)
    parser.add_argument("--learning-rate", type=float, default=2e-5)
    parser.add_argument("--eval-steps", type=int, default=100)
    parser.add_argument("--resume-from-checkpoint", default=None)
    return parser.parse_args()


def load_split(path: Path):
    dataset = load_dataset("json", data_files=str(path), split="train")
    return dataset.cast_column("image", DatasetImage(decode=True))


def main() -> None:
    args = parse_args()
    if not torch.cuda.is_available():
        raise SystemExit("A CUDA GPU is required. Use a Terra High GPU runtime with at least 48 GB VRAM.")
    if not (args.prepared_root / "train.jsonl").is_file():
        raise SystemExit("Prepared dataset is missing. Run prepare_llama32_vision_dataset.py first.")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    processor = AutoProcessor.from_pretrained(args.model_id)
    if processor.tokenizer.pad_token is None:
        processor.tokenizer.pad_token = processor.tokenizer.eos_token

    model = AutoModelForImageTextToText.from_pretrained(args.model_id, torch_dtype=torch.bfloat16)
    model.config.use_cache = False

    lora = LoraConfig(
        task_type=TaskType.CAUSAL_LM,
        r=32,
        lora_alpha=64,
        lora_dropout=0.05,
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj", "gate_proj", "up_proj", "down_proj"],
    )
    config = SFTConfig(
        output_dir=str(args.output_dir),
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        per_device_eval_batch_size=1,
        gradient_accumulation_steps=args.gradient_accumulation,
        learning_rate=args.learning_rate,
        lr_scheduler_type="cosine",
        warmup_ratio=0.03,
        bf16=True,
        tf32=True,
        gradient_checkpointing=True,
        gradient_checkpointing_kwargs={"use_reentrant": False},
        optim="adamw_torch_fused",
        logging_strategy="steps",
        logging_steps=5,
        logging_first_step=True,
        eval_strategy="steps",
        eval_steps=args.eval_steps,
        save_strategy="steps",
        save_steps=args.eval_steps,
        save_total_limit=2,
        load_best_model_at_end=True,
        metric_for_best_model="eval_loss",
        greater_is_better=False,
        max_length=None,
        assistant_only_loss=True,
        remove_unused_columns=False,
        report_to=["tensorboard"],
    )
    trainer = SFTTrainer(
        model=model,
        args=config,
        train_dataset=load_split(args.prepared_root / "train.jsonl"),
        eval_dataset=load_split(args.prepared_root / "validation.jsonl"),
        processing_class=processor,
        peft_config=lora,
    )
    trainer.train(resume_from_checkpoint=args.resume_from_checkpoint)
    metrics = trainer.evaluate()
    adapter_dir = args.output_dir / "adapter"
    trainer.save_model(str(adapter_dir))
    processor.save_pretrained(adapter_dir)
    (args.output_dir / "evaluation_metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()
