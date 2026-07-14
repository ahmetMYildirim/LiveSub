"""Download IGDB cover images for the existing PsGameTranslator game-recognition dataset."""

from __future__ import annotations

import argparse
import csv
import os
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

import requests


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--workers", type=int, default=8)
    return parser.parse_args()


def query_covers(client_id: str, access_token: str, game_ids: list[str]) -> dict[str, str]:
    headers = {"Client-ID": client_id, "Authorization": f"Bearer {access_token}"}
    results: dict[str, str] = {}

    for start in range(0, len(game_ids), 500):
        batch = game_ids[start : start + 500]
        query = f"fields id,name,cover.url; where id = ({','.join(batch)}); limit {len(batch)};"
        response = requests.post("https://api.igdb.com/v4/games", headers=headers, data=query, timeout=30)
        response.raise_for_status()

        for game in response.json():
            cover_url = game.get("cover", {}).get("url")
            if cover_url:
                results[str(game["id"])] = "https:" + cover_url if cover_url.startswith("//") else cover_url

    return results


def download_cover(session: requests.Session, game: dict[str, str], covers_dir: Path) -> dict[str, str]:
    game_id = game["game_id"]
    cover_url = game["cover_url"]
    target_path = covers_dir / f"gameid_{game_id}.jpg"

    try:
        response = session.get(cover_url, timeout=30)
        response.raise_for_status()
        target_path.write_bytes(response.content)
        return {**game, "cover_path": str(target_path.relative_to(covers_dir.parent)), "status": "downloaded"}
    except requests.RequestException as error:
        return {**game, "cover_path": "", "status": f"failed: {error}"}


def main() -> None:
    args = parse_args()
    client_id = os.getenv("IGDB_CLIENT_ID")
    access_token = os.getenv("IGDB_ACCESS_TOKEN")
    if not client_id or not access_token:
        raise SystemExit("Set IGDB_CLIENT_ID and IGDB_ACCESS_TOKEN before downloading covers.")

    selected_games_path = args.data_root / "metadata" / "selected_games.csv"
    covers_dir = args.data_root / "covers"
    covers_dir.mkdir(exist_ok=True)
    with selected_games_path.open(encoding="utf-8-sig", newline="") as stream:
        selected_games = list(csv.DictReader(stream))

    cover_urls = query_covers(client_id, access_token, [game["game_id"] for game in selected_games])
    work_items = [
        {"game_id": game["game_id"], "game_name": game["game_name"], "cover_url": cover_urls[game["game_id"]]}
        for game in selected_games
        if game["game_id"] in cover_urls
    ]
    results: list[dict[str, str]] = []

    with requests.Session() as session, ThreadPoolExecutor(max_workers=args.workers) as executor:
        futures = [executor.submit(download_cover, session, game, covers_dir) for game in work_items]
        for index, future in enumerate(as_completed(futures), start=1):
            result = future.result()
            results.append(result)
            print(f"Cover {index}/{len(work_items)} | {result['game_name']} | {result['status']}", flush=True)

    results.sort(key=lambda row: int(row["game_id"]))
    output_path = args.data_root / "metadata" / "covers.csv"
    with output_path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=["game_id", "game_name", "cover_url", "cover_path", "status"])
        writer.writeheader()
        writer.writerows(results)

    downloaded = sum(result["status"] == "downloaded" for result in results)
    print(f"Finished | downloaded={downloaded} | unavailable={len(selected_games) - downloaded}", flush=True)


if __name__ == "__main__":
    main()

