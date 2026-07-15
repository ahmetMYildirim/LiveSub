"""Benchmark ONNX top-5 candidate generation followed by local Ollama vision selection."""

from __future__ import annotations

import argparse
import base64
import json
import random
import re
import time
import unicodedata
from pathlib import Path

import numpy as np
import onnxruntime as ort
import requests
from PIL import Image


def args() -> argparse.Namespace:
    p = argparse.ArgumentParser()
    p.add_argument("--data-root", type=Path, required=True)
    p.add_argument("--onnx", type=Path, required=True)
    p.add_argument("--labels", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--model", default="gemma3:4b")
    p.add_argument("--samples", type=int, default=100)
    p.add_argument("--seed", type=int, default=20260715)
    return p.parse_args()


def norm(value: str) -> str:
    value = unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode().lower()
    return re.sub(r"[^a-z0-9]+", " ", value).strip()


def image_tensor(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        image = image.convert("RGB")
        width, height = image.size
        scale = 256 / min(width, height)
        image = image.resize((round(width * scale), round(height * scale)), Image.Resampling.BILINEAR)
        left, top = (image.width - 224) // 2, (image.height - 224) // 2
        image = image.crop((left, top, left + 224, top + 224))
        array = np.asarray(image, dtype=np.float32) / 255.0
    array = (array - np.array([0.485, 0.456, 0.406], dtype=np.float32)) / np.array([0.229, 0.224, 0.225], dtype=np.float32)
    return np.transpose(array, (2, 0, 1))[None, ...]


def select_answer(image: Path, candidates: list[str], model: str) -> tuple[str | None, float, str]:
    prompt = (
        "Identify the game screenshot. You must choose exactly one title from this candidate list, "
        "or answer Unknown if none is clearly correct. Reply only with the selected title or Unknown.\n\n"
        + "Candidates:\n- " + "\n- ".join(candidates)
    )
    started = time.perf_counter()
    response = requests.post(
        "http://127.0.0.1:11434/api/generate",
        json={"model": model, "stream": False, "prompt": prompt,
              "images": [base64.b64encode(image.read_bytes()).decode("ascii")],
              "options": {"temperature": 0.0, "num_predict": 32}},
        timeout=180,
    )
    response.raise_for_status()
    raw = str(response.json().get("response", "")).strip()
    answer = norm(raw.strip('". '))
    selected = next((title for title in candidates if norm(title) == answer), None)
    return selected, (time.perf_counter() - started) * 1000, raw


def main() -> None:
    a = args()
    labels: list[str] = json.loads(a.labels.read_text(encoding="utf-8"))
    games = [json.loads(x) for x in (a.data_root / "metadata" / "games.jsonl").read_text(encoding="utf-8").splitlines() if x]
    titles = {game["label"]: game["title"] for game in games}
    samples = []
    for label in labels:
        files = sorted((a.data_root / "test" / label).glob("*.jpg"))
        if files:
            samples.append((label, files[0]))
    random.Random(a.seed).shuffle(samples)
    samples = samples[:a.samples]
    if not samples:
        raise SystemExit("No test images found.")

    session = ort.InferenceSession(str(a.onnx), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    rows = []
    for index, (truth_label, image) in enumerate(samples, start=1):
        logits = session.run(None, {input_name: image_tensor(image)})[0][0]
        top = np.argsort(logits)[-5:][::-1].tolist()
        candidate_labels = [labels[i] for i in top]
        candidate_titles = [titles[item] for item in candidate_labels]
        truth_title = titles[truth_label]
        selected, latency, raw = select_answer(image, candidate_titles, a.model)
        row = {"image": str(image), "truth": truth_title, "onnx_top1": candidate_titles[0],
               "onnx_top5": candidate_titles, "truth_in_top5": truth_title in candidate_titles,
               "ollama_selected": selected, "ollama_raw": raw, "hybrid_correct": selected == truth_title,
               "ollama_latency_ms": round(latency, 1)}
        rows.append(row)
        print(f"[{index}/{len(samples)}] onnx={candidate_titles[0]} | truth={truth_title} | ollama={selected or raw}", flush=True)

    total = len(rows)
    report = {"samples": total, "model": a.model,
              "onnx_top1_accuracy": sum(x["onnx_top1"] == x["truth"] for x in rows) / total,
              "onnx_top5_recall": sum(x["truth_in_top5"] for x in rows) / total,
              "ollama_valid_selection_rate": sum(x["ollama_selected"] is not None for x in rows) / total,
              "hybrid_top1_accuracy": sum(x["hybrid_correct"] for x in rows) / total,
              "mean_ollama_latency_ms": sum(x["ollama_latency_ms"] for x in rows) / total}
    a.output.parent.mkdir(parents=True, exist_ok=True)
    a.output.write_text(json.dumps({"report": report, "rows": rows}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)


if __name__ == "__main__":
    main()
