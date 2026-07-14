#!/usr/bin/env python3
"""Convert the IGDB collector manifests into a TRL vision-language dataset."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


PROMPT = (
    "You are looking at a screenshot from a video game. Look carefully at the HUD, UI style, "
    "art style, character models, and visible text or logos. What is the exact, real title of "
    "this specific game? Answer with ONLY the game's title. If you cannot identify it with "
    "confidence, answer Unknown."
)


def read_jsonl(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def write_jsonl(path: Path, rows: list[dict]) -> None:
    with path.open("w", encoding="utf-8") as file:
        for row in rows:
            file.write(json.dumps(row, ensure_ascii=False) + "\n")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset-root", type=Path, required=True)
    parser.add_argument("--min-games", type=int, default=250)
    args = parser.parse_args()

    root = args.dataset_root.resolve()
    metadata = root / "metadata"
    report_path = metadata / "dataset_report.json"
    if not report_path.exists():
        raise SystemExit("Dataset collection is not complete: metadata/dataset_report.json is missing.")

    report = json.loads(report_path.read_text(encoding="utf-8"))
    if report.get("accepted_games", 0) < args.min_games:
        raise SystemExit(
            f"Dataset has {report.get('accepted_games', 0)} accepted games; need at least {args.min_games} before training."
        )

    output = root / "training" / "llama32_vision"
    output.mkdir(parents=True, exist_ok=True)
    summary: dict[str, int] = {}
    for split in ("train", "validation", "test"):
        rows: list[dict] = []
        for item in read_jsonl(metadata / f"{split}.jsonl"):
            image_path = (root / item["image"]).resolve()
            if not image_path.is_file():
                raise SystemExit(f"Missing image referenced by manifest: {image_path}")
            rows.append(
                {
                    "image": str(image_path),
                    "game_id": item["game_id"],
                    "title": item["title"],
                    "messages": [
                        {
                            "role": "user",
                            "content": [{"type": "image"}, {"type": "text", "text": PROMPT}],
                        },
                        {
                            "role": "assistant",
                            "content": [{"type": "text", "text": item["title"]}],
                        },
                    ],
                }
            )
        write_jsonl(output / f"{split}.jsonl", rows)
        summary[split] = len(rows)

    catalog = read_jsonl(metadata / "games.jsonl")
    (output / "game_catalog.json").write_text(json.dumps(catalog, ensure_ascii=False, indent=2), encoding="utf-8")
    (output / "dataset_summary.json").write_text(
        json.dumps({"source_report": report, "examples_by_split": summary, "prompt": PROMPT}, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
