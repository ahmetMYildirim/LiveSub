# IGDB game-recognition dataset

This creates a new dataset for games released from 2016 onward. It intentionally does **not** reuse the old dataset.

The dataset contains `train`, `validation`, and `test` folders of gameplay screenshots plus a separate `covers` catalog. Covers are never placed in a training split. `metadata/*.jsonl` is a ready-to-adapt vision-language manifest for Terra High/Hugging Face training.

## Credentials

Create an IGDB/Twitch application, then obtain a Client ID and OAuth access token. Keep them out of source code and out of Git:

```powershell
$env:IGDB_CLIENT_ID = 'your-client-id'
$env:IGDB_ACCESS_TOKEN = 'your-current-access-token'
```

Or set `IGDB_CLIENT_SECRET`; the script will request a fresh OAuth token automatically and avoids manual token-expiry failures.

## Create the dataset

```powershell
py -m venv .venv-igdb-dataset
.\.venv-igdb-dataset\Scripts\python.exe -m pip install -r .\tools\igdb-game-dataset\requirements.txt
.\.venv-igdb-dataset\Scripts\python.exe .\tools\igdb-game-dataset\build_igdb_dataset.py `
  --output-root "$HOME\Desktop\PsGameTranslator-IGDB-2016-2026" `
  --target-games 500 --min-screenshots 12 --max-screenshots-per-game 20
```

The defaults prioritize data quality: main games only, popularity-ranked candidates, no editions, a cover, at least 12 valid screenshots, exact-duplicate removal across games, and near-duplicate scene clustering before splitting.
