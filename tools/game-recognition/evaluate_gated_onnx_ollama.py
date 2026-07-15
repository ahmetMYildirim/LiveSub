"""Evaluate confidence-gated Ollama selection using a completed hybrid benchmark."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort

from benchmark_onnx_ollama import image_tensor


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--benchmark", type=Path, required=True)
    p.add_argument("--onnx", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    a = p.parse_args()
    payload = json.loads(a.benchmark.read_text(encoding="utf-8"))
    rows = payload["rows"]
    session = ort.InferenceSession(str(a.onnx), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    for row in rows:
        logits = session.run(None, {input_name: image_tensor(Path(row["image"]))})[0][0]
        shifted = logits - logits.max()
        probabilities = np.exp(shifted) / np.exp(shifted).sum()
        ranked = np.sort(probabilities)[::-1]
        row["onnx_confidence"] = float(ranked[0])
        row["onnx_margin"] = float(ranked[0] - ranked[1])

    baseline = sum(row["onnx_top1"] == row["truth"] for row in rows) / len(rows)
    hybrid = sum(row["hybrid_correct"] for row in rows) / len(rows)
    ranked = sorted(rows, key=lambda row: row["onnx_margin"])
    results = []
    for percent in range(0, 101, 5):
        count = round(len(rows) * percent / 100)
        gated = {id(row) for row in ranked[:count]}
        correct = 0
        for row in rows:
            choose_ollama = id(row) in gated and row["ollama_selected"] is not None
            prediction = row["ollama_selected"] if choose_ollama else row["onnx_top1"]
            correct += prediction == row["truth"]
        results.append({"ollama_on_lowest_margin_percent": percent, "ollama_calls": count,
                        "accuracy": correct / len(rows),
                        "margin_cutoff": ranked[count - 1]["onnx_margin"] if count else None})
    best = max(results, key=lambda item: item["accuracy"])
    report = {"samples": len(rows), "onnx_top1_accuracy": baseline,
              "always_ollama_accuracy": hybrid, "best_gated_strategy": best,
              "strategies": results}
    a.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
