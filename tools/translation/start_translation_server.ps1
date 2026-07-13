#Requires -Version 5.1
<#
.SYNOPSIS
    Activates the project .venv, installs translation requirements, and starts
    the OPUS-MT translation server on 127.0.0.1:8770.
#>

param(
    [string]$Model = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Model precedence: -Model parameter > TRANSLATION_MODEL env var > server default.
if (-not $Model -and $env:TRANSLATION_MODEL) {
    $Model = $env:TRANSLATION_MODEL
}

$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot "..\..") | Select-Object -ExpandProperty Path
$venvDir    = Join-Path $repoRoot ".venv"
$activatePs = Join-Path $venvDir "Scripts\Activate.ps1"
$serverScript = Join-Path $repoRoot "tools\translation\translation_server.py"
$requirementsFile = Join-Path $repoRoot "tools\translation\requirements.txt"

# ── Activate venv ─────────────────────────────────────────────────────────────────

if (Test-Path $activatePs) {
    Write-Host "[start] Activating .venv ..." -ForegroundColor Cyan
    & $activatePs
    if (-not $?) {
        Write-Error "Failed to activate .venv. Check execution policy: Set-ExecutionPolicy -Scope CurrentUser RemoteSigned"
        exit 1
    }
} else {
    Write-Warning ".venv not found at: $venvDir"
    Write-Host ""
    Write-Host "Create it first:" -ForegroundColor Yellow
    Write-Host "  py -3.11 -m venv .venv" -ForegroundColor White
    Write-Host "  .\.venv\Scripts\Activate.ps1" -ForegroundColor White
    Write-Host "  pip install -r tools\translation\requirements.txt" -ForegroundColor White
    exit 1
}

# ── Install requirements ──────────────────────────────────────────────────────────

Write-Host "[start] Installing / verifying requirements ..." -ForegroundColor Cyan
pip install -r $requirementsFile --quiet
if (-not $?) {
    Write-Error "pip install failed. Check your internet connection or venv."
    exit 1
}

# ── Start server ──────────────────────────────────────────────────────────────────

Write-Host "[start] Starting translation server on http://127.0.0.1:8770 ..." -ForegroundColor Green
Write-Host "        Press Ctrl+C to stop." -ForegroundColor Gray
if ($Model) {
    Write-Host "[start] Model: $Model" -ForegroundColor Cyan
    python $serverScript --host 127.0.0.1 --port 8770 --model $Model
} else {
    python $serverScript --host 127.0.0.1 --port 8770
}
