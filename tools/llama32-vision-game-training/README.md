# PsGameTranslator vision-model training

This package trains `meta-llama/Llama-3.2-11B-Vision-Instruct` with a non-quantized LoRA adapter, then imports the adapter into Ollama as `llama-ps-game-recognizer:latest`.

`llama3.2-vision:11b` was selected because it accepts image input in Ollama and Ollama officially supports Llama 3.2 adapters. Qwen3-VL is available in Ollama, but its fine-tuned adapter import is not currently documented by Ollama, so it is not the reliable deployment choice here.

## Before training

1. Wait for the IGDB collector to finish. `metadata/dataset_report.json` must exist and report at least 250 accepted games.
2. Upload or mount the completed `PsGameTranslator-IGDB-2016-2026` directory in Terra High.
3. Use a CUDA GPU with at least 48 GB VRAM. This is standard LoRA, not QLoRA, because Ollama recommends non-quantized adapters for import.
4. Accept Meta's Llama 3.2 license on Hugging Face, then create a read token.

## Terra High command

```bash
export HF_TOKEN='your-hugging-face-read-token'
bash tools/llama32-vision-game-training/run_terra_high.sh \
  /workspace/data/PsGameTranslator-IGDB-2016-2026 \
  /workspace/game-training
```

Training logs `loss` every 5 steps and `eval_loss` every 100 steps. The collector's train/validation/test split is preserved; covers are never included in training samples.

## Import into Ollama

After training, copy the `adapter` directory to the machine that runs PsGameTranslator and run:

```bash
bash tools/llama32-vision-game-training/create_ollama_model.sh /path/to/adapter
```

Set `OllamaVisionModel` to `llama-ps-game-recognizer:latest` in the app settings.

## Windows Terminal / PowerShell

Use this if the Windows machine itself has a CUDA GPU with at least 48 GB VRAM. After accepting the Llama license, create a new Hugging Face read token and run:

```powershell
cd "C:\Users\ahmet\Documents\Codex\2026-07-02\create-a-clean-net-8-wpf\tools\llama32-vision-game-training"
$env:HF_TOKEN = 'your-new-hugging-face-read-token'

.\run_windows_training.ps1 `
  -DatasetRoot "C:\Users\ahmet\Desktop\PsGameTranslator-IGDB-2016-2026" `
  -WorkRoot "C:\Users\ahmet\Documents\PsGameTranslatorTraining"
```

After training completes:

```powershell
.\create_ollama_model.ps1 `
  -AdapterDir "C:\Users\ahmet\Documents\PsGameTranslatorTraining\llama-ps-game-recognizer\adapter"
```
