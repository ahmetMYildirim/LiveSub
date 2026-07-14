#!/usr/bin/env bash
set -euo pipefail

# Usage: HF_TOKEN=... bash run_terra_high.sh /absolute/path/to/PsGameTranslator-IGDB-2016-2026 /workspace/game-training
DATASET_ROOT="${1:?Pass the completed IGDB dataset root as argument 1}"
WORK_ROOT="${2:-$PWD/ps-game-training}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

: "${HF_TOKEN:?Set HF_TOKEN after accepting the Meta Llama 3.2 license on Hugging Face.}"
mkdir -p "$WORK_ROOT"

python3 - <<'PY'
import torch
if not torch.cuda.is_available():
    raise SystemExit("Terra High GPU/CUDA runtime is required.")
print(f"CUDA ready: {torch.cuda.get_device_name(0)}")
PY

python3 -m pip install --upgrade pip
python3 -m pip install -r "$SCRIPT_DIR/requirements-terra-high.txt"

PREPARED_ROOT="$DATASET_ROOT/training/llama32_vision"
OUTPUT_DIR="$WORK_ROOT/llama-ps-game-recognizer"
python3 "$SCRIPT_DIR/prepare_llama32_vision_dataset.py" --dataset-root "$DATASET_ROOT" --min-games 250

accelerate launch --num_processes 1 --mixed_precision bf16 "$SCRIPT_DIR/train_llama32_vision_lora.py" \
  --prepared-root "$PREPARED_ROOT" \
  --output-dir "$OUTPUT_DIR" \
  --epochs 4 \
  --batch-size 1 \
  --gradient-accumulation 16 \
  --learning-rate 2e-5 \
  --eval-steps 100

echo "Training complete. Ollama adapter: $OUTPUT_DIR/adapter"
