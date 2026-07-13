# PsGameTranslator

A .NET 8 WPF app that captures game subtitles in real time (Windows.Graphics.Capture),
recognizes them with OCR (PaddleOCR), translates them (local OPUS-MT model, or DeepL /
Google / Gemini / Groq / Ollama), and displays the result as a transparent overlay on
top of the game.

## Projects

- `PsGameTranslator.App` — WPF entry point, dependency injection, MVVM shell.
- `PsGameTranslator.Core` — Shared domain models, no project dependencies.
- `PsGameTranslator.Capture` — Window capture (Windows.Graphics.Capture + GDI fallback).
- `PsGameTranslator.Ocr` — OCR service contract and provider(s).
- `PsGameTranslator.Overlay` — Transparent overlay window and settings.
- `PsGameTranslator.Infrastructure` — Translation pipeline, glossary, settings, logging.
- `PsGameTranslator.Tests` — Unit tests.

## Requirements

- Visual Studio 2022, .NET 8 SDK, .NET Desktop Development workload
- Python 3.11 (for the OCR and translation servers)

## Build & run

```powershell
dotnet restore
dotnet build PsGameTranslator.sln
```

Set `PsGameTranslator.App` as the startup project and press `F5`.

## Python environment

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r tools\ocr\requirements.txt
pip install -r tools\translation\requirements.txt
```

The app starts the OCR server (`tools/ocr/ocr_server.py`, port 8765) and the
translation server (`tools/translation/translation_server.py`, port 8770)
automatically; they can also be run manually for debugging.

## Docs

Technical guides (resource optimization, general performance theory, architecture
patterns) live in [`docs/`](docs/).
