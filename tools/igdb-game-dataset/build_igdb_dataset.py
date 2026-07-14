#!/usr/bin/env python3
"""Build a leakage-aware IGDB screenshot dataset for game recognition.

Cover art is downloaded only as catalog metadata. It never enters train/validation/test.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import re
import shutil
import sys
import time
import unicodedata
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import date, datetime, timezone
from io import BytesIO
from pathlib import Path
from typing import Any

import imagehash
import requests
from PIL import Image, ImageOps


API_URL = "https://api.igdb.com/v4/games"
TOKEN_URL = "https://id.twitch.tv/oauth2/token"
PROMPT = "Identify the video game shown in this gameplay screenshot. Reply with only the game title."
Image.MAX_IMAGE_PIXELS = 50_000_000


@dataclass(frozen=True)
class Asset:
    source_url: str
    jpeg: bytes
    sha256: str
    perceptual_hash: imagehash.ImageHash


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--target-games", type=int, default=500)
    parser.add_argument("--start-date", type=date.fromisoformat, default=date(2016, 1, 1))
    parser.add_argument("--end-date", type=date.fromisoformat, default=date.today())
    parser.add_argument("--min-screenshots", type=int, default=12)
    parser.add_argument("--max-screenshots-per-game", type=int, default=20)
    parser.add_argument("--min-rating-count", type=int, default=100)
    parser.add_argument("--near-duplicate-distance", type=int, default=6)
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--seed", type=int, default=20260714)
    return parser.parse_args()


def unix_seconds(value: date) -> int:
    return int(datetime(value.year, value.month, value.day, tzinfo=timezone.utc).timestamp())


def normalized_url(url: str, image_type: str) -> str:
    url = f"https:{url}" if url.startswith("//") else url
    if image_type == "cover":
        return url.replace("t_thumb", "t_cover_big")
    return re.sub(r"/t_[^/]+/", "/t_1080p/", url)


def safe_label(game: dict[str, Any]) -> str:
    name = unicodedata.normalize("NFKD", game["name"]).encode("ascii", "ignore").decode().lower()
    name = re.sub(r"[^a-z0-9]+", "-", name).strip("-")[:70] or "untitled"
    return f"{game['id']}_{name}"


def fetch_games(args: argparse.Namespace) -> list[dict[str, Any]]:
    client_id = os.getenv("IGDB_CLIENT_ID")
    access_token = os.getenv("IGDB_ACCESS_TOKEN")
    client_secret = os.getenv("IGDB_CLIENT_SECRET")
    if not client_id:
        raise RuntimeError("Set IGDB_CLIENT_ID as a local environment variable before running.")
    if client_secret:
        token_response = requests.post(
            TOKEN_URL,
            params={"client_id": client_id, "client_secret": client_secret, "grant_type": "client_credentials"},
            timeout=30,
        )
        token_response.raise_for_status()
        access_token = token_response.json().get("access_token")
    if not access_token:
        raise RuntimeError("Set IGDB_ACCESS_TOKEN or IGDB_CLIENT_SECRET as a local environment variable before running.")

    headers = {"Client-ID": client_id, "Authorization": f"Bearer {access_token}", "Accept": "application/json"}
    query_base = (
        "fields id,name,slug,first_release_date,total_rating,total_rating_count,cover.url,screenshots.url; "
        f"where game_type = 0 & version_parent = null & first_release_date >= {unix_seconds(args.start_date)} "
        f"& first_release_date <= {unix_seconds(args.end_date)} & cover != null & screenshots != null "
        f"& total_rating_count >= {args.min_rating_count}; sort total_rating_count desc;"
    )
    candidates: list[dict[str, Any]] = []
    offset = 0
    wanted_candidates = max(args.target_games * 4, 500)
    while len(candidates) < wanted_candidates and offset < 5000:
        response = requests.post(API_URL, headers=headers, data=f"{query_base} limit 500; offset {offset};", timeout=30)
        response.raise_for_status()
        page = response.json()
        if not page:
            break
        candidates.extend(
            game for game in page
            if game.get("cover", {}).get("url") and len(game.get("screenshots", [])) >= args.min_screenshots
        )
        offset += len(page)
        time.sleep(0.28)  # Stay beneath IGDB's four API requests per second limit.
    return candidates


def download(url: str) -> tuple[str, bytes | None, str | None]:
    try:
        response = requests.get(url, timeout=45, headers={"User-Agent": "PsGameTranslatorDatasetBuilder/1.0"})
        response.raise_for_status()
        return url, response.content, None
    except requests.RequestException as error:
        return url, None, str(error)


def to_asset(url: str, content: bytes, minimum_width: int = 320, minimum_height: int = 180) -> Asset | None:
    try:
        with Image.open(BytesIO(content)) as verified:
            verified.verify()
        with Image.open(BytesIO(content)) as opened:
            image = ImageOps.exif_transpose(opened).convert("RGB")
            if image.width < minimum_width or image.height < minimum_height:
                return None
            hash_image = image.copy()
            hash_image.thumbnail((256, 256))
            encoded = BytesIO()
            image.save(encoded, format="JPEG", quality=92)
            jpeg = encoded.getvalue()
            return Asset(url, jpeg, hashlib.sha256(jpeg).hexdigest(), imagehash.phash(hash_image))
    except Exception:
        return None


def cluster_assets(assets: list[Asset], distance: int) -> list[list[Asset]]:
    """Union near-duplicate screenshots so a scene cannot cross split boundaries."""
    parent = list(range(len(assets)))

    def find(index: int) -> int:
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    def union(left: int, right: int) -> None:
        left, right = find(left), find(right)
        if left != right:
            parent[right] = left

    for left in range(len(assets)):
        for right in range(left + 1, len(assets)):
            if assets[left].perceptual_hash - assets[right].perceptual_hash <= distance:
                union(left, right)

    groups: dict[int, list[Asset]] = {}
    for index, asset in enumerate(assets):
        groups.setdefault(find(index), []).append(asset)
    return list(groups.values())


def assign_splits(clusters: list[list[Asset]], game_id: int, seed: int) -> dict[str, list[Asset]]:
    if len(clusters) < 3:
        raise ValueError("need at least three distinct screenshot clusters")
    shuffled = clusters[:]
    random.Random(seed + game_id).shuffle(shuffled)
    result = {"train": [*shuffled[2]], "validation": [*shuffled[1]], "test": [*shuffled[0]]}
    target = {"train": 0.70, "validation": 0.15, "test": 0.15}
    for cluster in shuffled[3:]:
        total = sum(len(items) for items in result.values())
        split = min(result, key=lambda key: len(result[key]) / max(1, total * target[key]))
        result[split].extend(cluster)
    return result


def write_jsonl(path: Path, rows: list[dict[str, Any]]) -> None:
    with path.open("w", encoding="utf-8") as file:
        for row in rows:
            file.write(json.dumps(row, ensure_ascii=False) + "\n")


def main() -> int:
    args = parse_args()
    if args.target_games < 1 or args.min_screenshots < 3 or args.max_screenshots_per_game < args.min_screenshots:
        raise SystemExit("Invalid target/screenshot settings.")
    if args.output_root.exists() and any(path.is_file() for path in args.output_root.rglob("*")):
        raise SystemExit(f"Refusing to mix with an existing dataset: {args.output_root}")
    args.output_root.mkdir(parents=True, exist_ok=True)
    for split in ("train", "validation", "test"):
        (args.output_root / split).mkdir(exist_ok=True)
    (args.output_root / "covers").mkdir(exist_ok=True)
    metadata_dir = args.output_root / "metadata"
    metadata_dir.mkdir(exist_ok=True)

    candidates = fetch_games(args)
    print(f"IGDB returned {len(candidates)} eligible popularity-ranked candidates.")
    global_hashes: set[str] = set()
    games: list[dict[str, Any]] = []
    manifest: dict[str, list[dict[str, Any]]] = {"train": [], "validation": [], "test": []}
    rejected: list[dict[str, Any]] = []

    for game in candidates:
        if len(games) >= args.target_games:
            break
        screenshot_urls = [normalized_url(item["url"], "screenshot") for item in game["screenshots"] if item.get("url")]
        random.Random(args.seed + game["id"]).shuffle(screenshot_urls)
        screenshot_urls = screenshot_urls[:args.max_screenshots_per_game]
        assets: list[Asset] = []
        with ThreadPoolExecutor(max_workers=args.workers) as executor:
            futures = [executor.submit(download, url) for url in screenshot_urls]
            downloaded = [future.result() for future in as_completed(futures)]
        for source_url, content, _error in downloaded:
            if content is None:
                continue
            asset = to_asset(source_url, content)
            if asset is not None and asset.sha256 not in global_hashes and all(item.sha256 != asset.sha256 for item in assets):
                assets.append(asset)
        clusters = cluster_assets(assets, args.near_duplicate_distance)
        if len(assets) < args.min_screenshots or len(clusters) < 3:
            rejected.append({"id": game["id"], "name": game["name"], "reason": "insufficient_unique_screenshots"})
            continue

        cover_url = normalized_url(game["cover"]["url"], "cover")
        _url, cover_bytes, cover_error = download(cover_url)
        cover = to_asset(cover_url, cover_bytes, minimum_width=1, minimum_height=1) if cover_bytes else None
        if cover is None:
            rejected.append({"id": game["id"], "name": game["name"], "reason": f"cover_download_failed: {cover_error}"})
            continue

        label = safe_label(game)
        cover_relative = Path("covers") / f"{game['id']}.jpg"
        (args.output_root / cover_relative).write_bytes(cover.jpeg)
        split_assets = assign_splits(clusters, game["id"], args.seed)
        for split, images in split_assets.items():
            destination = args.output_root / split / label
            destination.mkdir(parents=True, exist_ok=True)
            for index, asset in enumerate(images, start=1):
                relative = Path(split) / label / f"{index:03d}.jpg"
                (args.output_root / relative).write_bytes(asset.jpeg)
                manifest[split].append({
                    "image": relative.as_posix(), "game_id": game["id"], "title": game["name"],
                    "prompt": PROMPT, "answer": game["name"], "source_url": asset.source_url,
                    "sha256": asset.sha256, "phash": str(asset.perceptual_hash),
                })
        global_hashes.update(asset.sha256 for asset in assets)
        games.append({
            "igdb_id": game["id"], "title": game["name"], "label": label,
            "first_release_date": game.get("first_release_date"), "total_rating": game.get("total_rating"),
            "total_rating_count": game.get("total_rating_count"), "cover": cover_relative.as_posix(),
            "screenshot_count": len(assets), "distinct_scene_clusters": len(clusters),
        })
        print(f"[{len(games):4d}/{args.target_games}] {game['name']} ({len(assets)} screenshots, {len(clusters)} scene clusters)")

    write_jsonl(metadata_dir / "games.jsonl", games)
    write_jsonl(metadata_dir / "rejected_games.jsonl", rejected)
    for split, rows in manifest.items():
        write_jsonl(metadata_dir / f"{split}.jsonl", rows)
    report = {
        "created_utc": datetime.now(timezone.utc).isoformat(), "source": "IGDB API", "date_range": [str(args.start_date), str(args.end_date)],
        "target_games": args.target_games, "accepted_games": len(games), "rejected_games": len(rejected),
        "screenshots_by_split": {split: len(rows) for split, rows in manifest.items()},
        "leakage_controls": [
            "Covers are metadata only and never used as train/validation/test input.",
            "Exact screenshot hashes are unique across accepted games.",
            "Near-duplicate screenshots are clustered by perceptual hash and stay in one split.",
            "Each accepted game has at least one independent scene cluster in every split.",
        ],
    }
    (metadata_dir / "dataset_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if len(games) < args.target_games:
        print(f"WARNING: only {len(games)} games met the quality gates. Relax constraints or increase the candidate pool.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, requests.RequestException) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(2)
