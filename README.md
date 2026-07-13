# PsGameTranslator

A modular .NET 8 WPF application architecture for capturing game text, recognizing it with OCR, translating it, and displaying it as an overlay.

This repository currently contains only the initial architecture. Capture, OCR, translation, and overlay implementations have not been added yet.

## Projects

- `PsGameTranslator.App` - WPF entry point, dependency injection, configuration, and MVVM navigation.
- `PsGameTranslator.Core` - Shared domain models with no project dependencies.
- `PsGameTranslator.Capture` - Win32 window enumeration and the future capture contract.
- `PsGameTranslator.Ocr` - OCR service contract.
- `PsGameTranslator.Overlay` - Overlay service contract.
- `PsGameTranslator.Infrastructure` - App settings, JSON configuration, and Serilog setup.

## Requirements

- Visual Studio 2022
- .NET 8 SDK
- .NET Desktop Development workload
- Python 3.11 (for OCR and translation servers)

## Build

```powershell
dotnet restore
dotnet build PsGameTranslator.sln
```

## Run

Set `PsGameTranslator.App` as the startup project and press `F5`.

## Architecture

```text
App
├── Capture ─────────> Core
├── Ocr ─────────────> Core
├── Overlay ─────────> Core
├── Infrastructure ──> Core
└── Core
```

`Core` does not depend on any other project.

## Python Environment Setup

Both the OCR server and the translation server share the project `.venv`.

```powershell
# 1. Create Python 3.11 virtual environment (once)
py -3.11 -m venv .venv

# 2. Activate
.\.venv\Scripts\Activate.ps1

# 3. Install OCR server dependencies
pip install -r tools\ocr\requirements.txt

# 4. Install translation server dependencies
pip install -r tools\translation\requirements.txt
```

## OCR Server

```powershell
# Start (loads PaddleOCR model once, serves on 127.0.0.1:8765)
python tools\ocr\ocr_server.py
```

## Translation Server (OPUS-MT)

The translation server uses `Helsinki-NLP/opus-mt-tc-big-en-tr` — a local
MarianMT model that translates English game subtitles into Turkish.
The model is downloaded automatically from HuggingFace on first run (~300 MB).

```powershell
# Start (loads model once, serves on 127.0.0.1:8770)
python tools\translation\translation_server.py

# Or use the helper script (activates venv + installs requirements automatically)
.\tools\translation\start_translation_server.ps1
```

### Test the translation server

```powershell
# Using the test script
.\tools\translation\test_translation.ps1

# Or manually with Invoke-RestMethod
$body = '{"text":"More marks of the dragon''s fury.","sourceLanguage":"en","targetLanguage":"tr"}'
Invoke-RestMethod -Uri http://127.0.0.1:8770/translate -Method POST `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
```

Expected response:

```json
{
  "translation": "Ejderhanın öfkesinin daha fazla izi.",
  "provider": "opus-mt-tc-big-en-tr",
  "durationMs": 450,
  "success": true,
  "error": null
}
```

### Health check

```powershell
Invoke-RestMethod http://127.0.0.1:8770/health
```

## Future Work

- Implement screenshot capture.
- Implement capture-region selection.
- Select and integrate an OCR provider.
- Add a translation service contract and provider.
- Implement a transparent click-through overlay.
- Add game-profile persistence and validation.
- Add automated tests.
