[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DatasetRoot,

    [Parameter(Mandatory)]
    [string]$WorkRoot,

    [string]$PythonExe = "py"
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSCommandPath

if (-not $env:HF_TOKEN) {
    throw "HF_TOKEN is not set. In this PowerShell session run: `$env:HF_TOKEN = 'your-new-hugging-face-token'"
}

$reportPath = Join-Path $DatasetRoot "metadata\dataset_report.json"
if (-not (Test-Path -LiteralPath $reportPath)) {
    throw "Completed dataset report was not found: $reportPath"
}

& $PythonExe -c "import torch; assert torch.cuda.is_available(), 'CUDA GPU was not detected'; print('CUDA ready:', torch.cuda.get_device_name(0))"
if ($LASTEXITCODE -ne 0) { throw "A CUDA-enabled PyTorch installation and a GPU with at least 48 GB VRAM are required." }

& $PythonExe -m pip install --upgrade pip
& $PythonExe -m pip install -r (Join-Path $scriptRoot "requirements-terra-high.txt")
if ($LASTEXITCODE -ne 0) { throw "Python dependency installation failed." }

$preparedRoot = Join-Path $DatasetRoot "training\llama32_vision"
$outputDir = Join-Path $WorkRoot "llama-ps-game-recognizer"
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

& $PythonExe (Join-Path $scriptRoot "prepare_llama32_vision_dataset.py") --dataset-root $DatasetRoot --min-games 250
if ($LASTEXITCODE -ne 0) { throw "Dataset preparation failed." }

& $PythonExe -m accelerate.commands.launch --num_processes 1 --mixed_precision bf16 `
    (Join-Path $scriptRoot "train_llama32_vision_lora.py") `
    --prepared-root $preparedRoot `
    --output-dir $outputDir `
    --epochs 4 `
    --batch-size 1 `
    --gradient-accumulation 16 `
    --learning-rate 2e-5 `
    --eval-steps 100
if ($LASTEXITCODE -ne 0) { throw "Training failed. Check the console output above." }

Write-Host "Training complete. Ollama adapter: $(Join-Path $outputDir 'adapter')" -ForegroundColor Green
