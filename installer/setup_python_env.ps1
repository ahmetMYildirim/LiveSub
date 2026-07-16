<#
.SYNOPSIS
    Provisions the Python environment LiveSub's OCR sidecar needs.

.DESCRIPTION
    LiveSub's app binary is self-contained .NET, but PaddleOCR runs as a Python
    sidecar. PsGameTranslator.Ocr.PythonResolver looks for ".venv\Scripts\python.exe"
    walking UP from the app directory — which is exactly why the app works from a
    source checkout (the repo's .venv is found) but not from an install (there is
    no .venv, so it falls back to whatever system Python exists, with whatever
    packages happen to be there — a missing python-multipart silently killed the
    OCR server and produced no subtitles at all).

    Creating "<install dir>\.venv" here makes the installed app resolve its own
    dedicated, fully-provisioned environment, identical to the dev experience.

.PARAMETER AppDir
    The LiveSub installation directory (the .venv is created inside it).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppDir
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $Message) Write-Host "==> $Message" }
function Fail { param([string] $Message) Write-Host "ERROR: $Message" -ForegroundColor Red; exit 1 }

# PaddleOCR publishes no wheels for 3.13+, so prefer known-good interpreters over
# whatever "python" happens to be first on PATH.
function Find-Python {
    $candidates = @()
    foreach ($v in @('Python311', 'Python312', 'Python310', 'Python39')) {
        $candidates += Join-Path $env:LOCALAPPDATA "Programs\Python\$v\python.exe"
        $candidates += Join-Path $env:ProgramFiles "Python\$v\python.exe"
    }
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $onPath = Get-Command python -ErrorAction SilentlyContinue
    if ($onPath) {
        $ver = & $onPath.Source --version 2>&1
        if ($ver -match 'Python 3\.(9|10|11|12)\.') { return $onPath.Source }
    }
    return $null
}

$python = Find-Python
if (-not $python) {
    Fail @"
Python 3.9-3.12 was not found.

LiveSub's PaddleOCR engine runs on Python. Install Python 3.11 from
https://www.python.org/downloads/release/python-3119/ (tick "Add python.exe to PATH"),
then re-run this script:

    powershell -ExecutionPolicy Bypass -File "$AppDir\installer\setup_python_env.ps1" -AppDir "$AppDir"
"@
}

Write-Step "Using Python: $python"
& $python --version

$venv = Join-Path $AppDir '.venv'
$venvPython = Join-Path $venv 'Scripts\python.exe'

if (Test-Path $venvPython) {
    Write-Step "Reusing existing environment: $venv"
} else {
    Write-Step "Creating environment: $venv"
    & $python -m venv $venv
    if (-not (Test-Path $venvPython)) { Fail "Failed to create the virtual environment at $venv." }
}

Write-Step 'Upgrading pip'
& $venvPython -m pip install --upgrade pip --quiet

# OCR only. The translation requirements pull in torch (~4 GB) and are not needed
# unless the user wants the local OPUS-MT backend — cloud providers (DeepL etc.)
# and the bundled ONNX game recogniser need no Python at all.
$ocrRequirements = Join-Path $AppDir 'tools\ocr\requirements.txt'
if (-not (Test-Path $ocrRequirements)) { Fail "Requirements file not found: $ocrRequirements" }

Write-Step 'Installing PaddleOCR and its dependencies (~1.5 GB, this takes a while)'
& $venvPython -m pip install -r $ocrRequirements
if ($LASTEXITCODE -ne 0) { Fail 'pip install failed. Check your internet connection and try again.' }

Write-Step 'Verifying'
$check = & $venvPython -c @"
import importlib.util as u
missing = [m for m in ['paddle', 'paddleocr', 'fastapi', 'uvicorn', 'multipart', 'PIL'] if not u.find_spec(m)]
print('MISSING:' + ','.join(missing) if missing else 'OK')
"@ 2>&1 | Select-String -Pattern '^(OK|MISSING:)' | Select-Object -First 1

if ($check -notmatch '^OK') {
    Fail "Environment is incomplete ($check). Try re-running this script."
}

Write-Host ''
Write-Step 'Done. LiveSub will now use this environment for PaddleOCR.'
exit 0
