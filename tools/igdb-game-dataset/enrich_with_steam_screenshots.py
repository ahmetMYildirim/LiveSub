#!/usr/bin/env python3
"""Create a leakage-aware V2 dataset by adding Steam store screenshots.

The source dataset is never modified. Steam title matches are conservative: if a
store result cannot be confidently mapped to the IGDB title it is skipped.
All screenshots (old and new) are reclustered before train/validation/test are
written, so near-identical scenes cannot leak between splits.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import re
import shutil
import time
import unicodedata
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import datetime, timezone
from difflib import SequenceMatcher
from io import BytesIO
from pathlib import Path
from typing import Any

import imagehash
import requests
from PIL import Image, ImageOps


STORE_SEARCH = "https://store.steampowered.com/api/storesearch/"
APP_DETAILS = "https://store.steampowered.com/api/appdetails"
USER_AGENT = "PsGameTranslatorDatasetBuilder/2.0"
Image.MAX_IMAGE_PIXELS = 50_000_000


@dataclass(frozen=True)
class Asset:
    source_url: str
    source: str
    jpeg: bytes
    sha256: str
    perceptual_hash: imagehash.ImageHash


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--max-steam-screenshots", type=int, default=32)
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--near-duplicate-distance", type=int, default=6)
    parser.add_argument("--seed", type=int, default=20260715)
    parser.add_argument("--limit-games", type=int, default=0, help="For a small smoke test only.")
    return parser.parse_args()


def jsonl(path: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def canonical(value: str) -> str:
    value = unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode().lower()
    value = re.sub(r"\b(game of the year|goty|complete|ultimate|deluxe|remastered|definitive|edition)\b", "", value)
    return re.sub(r"[^a-z0-9]+", " ", value).strip()


def title_score(left: str, right: str) -> float:
    left, right = canonical(left), canonical(right)
    if not left or not right:
        return 0.0
    if left == right:
        return 1.0
    left_words, right_words = set(left.split()), set(right.split())
    overlap = len(left_words & right_words) / max(1, len(left_words | right_words))
    return max(overlap, SequenceMatcher(None, left, right).ratio())


def request_json(url: str, **params: Any) -> Any | None:
    try:
        response = requests.get(url, params=params, headers={"User-Agent": USER_AGENT}, timeout=30)
        response.raise_for_status()
        return response.json()
    except requests.RequestException:
        return None


def steam_app_id(title: str) -> int | None:
    payload = request_json(STORE_SEARCH, term=title, l="english", cc="us") or {}
    choices = payload.get("items", [])
    ranked = sorted(
        (
            (title_score(title, str(item.get("name", ""))), item)
            for item in choices
            if item.get("type") in {"app", "game"}
        ),
        key=lambda value: value[0],
        reverse=True,
    )
    if not ranked or ranked[0][0] < 0.90:
        return None
    return int(ranked[0][1]["id"])


def steam_urls(title: str, maximum: int) -> tuple[int | None, list[str]]:
    app_id = steam_app_id(title)
    if app_id is None:
        return None, []
    payload = request_json(APP_DETAILS, appids=app_id, l="english", cc="us") or {}
    data = payload.get(str(app_id), {})
    if not data.get("success"):
        return app_id, []
    screenshots = data.get("data", {}).get("screenshots", [])
    urls = [str(item["path_full"]) for item in screenshots if item.get("path_full")]
    random.Random(app_id).shuffle(urls)
    return app_id, urls[:maximum]


def asset_from_bytes(url: str, source: str, content: bytes) -> Asset | None:
    try:
        with Image.open(BytesIO(content)) as image:
            image.verify()
        with Image.open(BytesIO(content)) as image:
            image = ImageOps.exif_transpose(image).convert("RGB")
            if image.width < 320 or image.height < 180:
                return None
            thumb = image.copy()
            thumb.thumbnail((256, 256))
            stream = BytesIO()
            image.save(stream, format="JPEG", quality=92)
            jpeg = stream.getvalue()
            return Asset(url, source, jpeg, hashlib.sha256(jpeg).hexdigest(), imagehash.phash(thumb))
    except Exception:
        return None


def asset_from_path(path: Path, url: str) -> Asset | None:
    try:
        return asset_from_bytes(url, "igdb", path.read_bytes())
    except OSError:
        return None


def download(url: str) -> tuple[str, bytes | None]:
    try:
        response = requests.get(url, headers={"User-Agent": USER_AGENT}, timeout=45)
        response.raise_for_status()
        return url, response.content
    except requests.RequestException:
        return url, None


def cluster(assets: list[Asset], distance: int) -> list[list[Asset]]:
    parents = list(range(len(assets)))

    def find(index: int) -> int:
        while parents[index] != index:
            parents[index] = parents[parents[index]]
            index = parents[index]
        return index

    def union(left: int, right: int) -> None:
        left, right = find(left), find(right)
        if left != right:
            parents[right] = left

    for left in range(len(assets)):
        for right in range(left + 1, len(assets)):
            if assets[left].perceptual_hash - assets[right].perceptual_hash <= distance:
                union(left, right)
    result: dict[int, list[Asset]] = {}
    for index, item in enumerate(assets):
        result.setdefault(find(index), []).append(item)
    return list(result.values())


def split_clusters(clusters: list[list[Asset]], game_id: int, seed: int) -> dict[str, list[Asset]]:
    shuffled = clusters[:]
    random.Random(seed + game_id).shuffle(shuffled)
    output = {"train": [*shuffled[2]], "validation": [*shuffled[1]], "test": [*shuffled[0]]}
    desired = {"train": 0.70, "validation": 0.15, "test": 0.15}
    for group in shuffled[3:]:
        total = sum(len(items) for items in output.values())
        name = min(output, key=lambda key: len(output[key]) / max(1, total * desired[key]))
        output[name].extend(group)
    return output


def write_jsonl(path: Path, values: list[dict[str, Any]]) -> None:
    with path.open("w", encoding="utf-8") as stream:
        for value in values:
            stream.write(json.dumps(value, ensure_ascii=False) + "\n")


def main() -> None:
    args = arguments()
    if args.output_root.exists() and any(args.output_root.rglob("*")):
        raise SystemExit(f"Output already exists and is not empty: {args.output_root}")
    source = args.source_root
    games = jsonl(source / "metadata" / "games.jsonl")
    if args.limit_games:
        games = games[:args.limit_games]
    manifests = {name: jsonl(source / "metadata" / f"{name}.jsonl") for name in ("train", "validation", "test")}
    original_by_id: dict[int, list[dict[str, Any]]] = {}
    for values in manifests.values():
        for value in values:
            original_by_id.setdefault(int(value["game_id"]), []).append(value)

    args.output_root.mkdir(parents=True)
    for name in ("train", "validation", "test", "covers", "metadata"):
        (args.output_root / name).mkdir()
    output_manifest: dict[str, list[dict[str, Any]]] = {name: [] for name in ("train", "validation", "test")}
    accepted: list[dict[str, Any]] = []
    unmatched: list[dict[str, Any]] = []
    global_hashes: set[str] = set()

    for index, game in enumerate(games, start=1):
        game_id = int(game["igdb_id"])
        originals: list[Asset] = []
        for row in original_by_id.get(game_id, []):
            item = asset_from_path(source / row["image"], str(row.get("source_url", "")))
            if item is not None and item.sha256 not in global_hashes and all(item.sha256 != old.sha256 for old in originals):
                originals.append(item)
        app_id, urls = steam_urls(str(game["title"]), args.max_steam_screenshots)
        steam: list[Asset] = []
        if urls:
            with ThreadPoolExecutor(max_workers=args.workers) as executor:
                futures = [executor.submit(download, url) for url in urls]
                for future in as_completed(futures):
                    url, content = future.result()
                    item = asset_from_bytes(url, "steam", content) if content else None
                    if item is not None and item.sha256 not in global_hashes and all(item.sha256 != old.sha256 for old in [*originals, *steam]):
                        steam.append(item)
        else:
            unmatched.append({"igdb_id": game_id, "title": game["title"], "steam_app_id": app_id, "reason": "no_confident_steam_match_or_screenshots"})

        assets = [*originals, *steam]
        groups = cluster(assets, args.near_duplicate_distance)
        if len(groups) < 3:
            unmatched.append({"igdb_id": game_id, "title": game["title"], "steam_app_id": app_id, "reason": "fewer_than_three_scene_clusters"})
            continue
        groups_by_split = split_clusters(groups, game_id, args.seed)
        for split, items in groups_by_split.items():
            destination = args.output_root / split / str(game["label"])
            destination.mkdir(parents=True, exist_ok=True)
            for item_index, item in enumerate(items, start=1):
                relative = Path(split) / str(game["label"]) / f"{item_index:03d}.jpg"
                (args.output_root / relative).write_bytes(item.jpeg)
                output_manifest[split].append({
                    "image": relative.as_posix(), "game_id": game_id, "title": game["title"],
                    "source": item.source, "source_url": item.source_url, "sha256": item.sha256,
                    "phash": str(item.perceptual_hash),
                })
        cover_source = source / str(game["cover"])
        cover_relative = Path("covers") / f"{game_id}.jpg"
        shutil.copy2(cover_source, args.output_root / cover_relative)
        accepted.append({**game, "cover": cover_relative.as_posix(), "igdb_screenshot_count": len(originals), "steam_screenshot_count": len(steam), "screenshot_count": len(assets), "distinct_scene_clusters": len(groups), "steam_app_id": app_id})
        global_hashes.update(item.sha256 for item in assets)
        print(f"[{index:4d}/{len(games)}] {game['title']} | IGDB={len(originals)} Steam={len(steam)} total={len(assets)} clusters={len(groups)}", flush=True)
        time.sleep(0.12)

    metadata = args.output_root / "metadata"
    write_jsonl(metadata / "games.jsonl", accepted)
    write_jsonl(metadata / "steam_unmatched.jsonl", unmatched)
    for split, values in output_manifest.items():
        write_jsonl(metadata / f"{split}.jsonl", values)
    report = {
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "source": "IGDB screenshots enriched with Steam store screenshots",
        "accepted_games": len(accepted),
        "screenshots_by_split": {name: len(values) for name, values in output_manifest.items()},
        "leakage_controls": [
            "Covers remain catalog metadata and never enter train/validation/test.",
            "Exact hashes are unique across accepted games.",
            "Near-duplicate scenes are clustered and assigned to one split only.",
            "Steam results require a conservative title match before use.",
        ],
    }
    (metadata / "dataset_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2), flush=True)


if __name__ == "__main__":
    main()
