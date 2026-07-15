"""Independent holdout validation for a fixed ONNX-margin/Ollama gate."""
from __future__ import annotations
import argparse, json, random
from pathlib import Path
import numpy as np
import onnxruntime as ort
from benchmark_onnx_ollama import image_tensor, select_answer

p = argparse.ArgumentParser()
p.add_argument("--data-root", type=Path, required=True); p.add_argument("--onnx", type=Path, required=True)
p.add_argument("--labels", type=Path, required=True); p.add_argument("--exclude", type=Path, required=True)
p.add_argument("--output", type=Path, required=True); p.add_argument("--samples", type=int, default=200)
p.add_argument("--margin", type=float, default=0.09742999821901321); p.add_argument("--model", default="gemma3:4b")
a = p.parse_args()
labels = json.loads(a.labels.read_text(encoding="utf-8"))
games = [json.loads(x) for x in (a.data_root / "metadata" / "games.jsonl").read_text(encoding="utf-8").splitlines() if x]
titles = {g["label"]: g["title"] for g in games}
used = {row["image"] for row in json.loads(a.exclude.read_text(encoding="utf-8"))["rows"]}
pool = []
for label in labels:
    files = [x for x in sorted((a.data_root / "test" / label).glob("*.jpg")) if str(x) not in used]
    if files: pool.append((label, random.Random(20260716 + len(label)).choice(files)))
random.Random(20260716).shuffle(pool); pool = pool[:a.samples]
if len(pool) < a.samples: raise SystemExit(f"Only {len(pool)} unused test images available.")
session = ort.InferenceSession(str(a.onnx), providers=["CPUExecutionProvider"]); input_name = session.get_inputs()[0].name
rows = []
for index, (truth_label, image) in enumerate(pool, 1):
    logits = session.run(None, {input_name: image_tensor(image)})[0][0]
    shifted = logits - logits.max(); prob = np.exp(shifted) / np.exp(shifted).sum(); order = np.argsort(prob)[-5:][::-1].tolist()
    candidates = [titles[labels[i]] for i in order]; margin = float(prob[order[0]] - prob[order[1]]); gated = margin < a.margin
    selected, latency, raw = select_answer(image, candidates, a.model) if gated else (None, 0.0, "not_called")
    prediction = selected if selected is not None else candidates[0]
    rows.append({"truth": titles[truth_label], "onnx_top1": candidates[0], "margin": margin, "ollama_called": gated, "ollama_selected": selected, "correct": prediction == titles[truth_label], "latency_ms": round(latency, 1)})
    print(f"[{index}/{len(pool)}] gate={gated} correct={prediction == titles[truth_label]}", flush=True)
calls = [x for x in rows if x["ollama_called"]]
report = {"samples": len(rows), "fixed_margin": a.margin, "onnx_top1_accuracy": sum(x["onnx_top1"] == x["truth"] for x in rows) / len(rows), "gated_hybrid_accuracy": sum(x["correct"] for x in rows) / len(rows), "ollama_call_rate": len(calls) / len(rows), "mean_ollama_latency_ms": sum(x["latency_ms"] for x in calls) / max(1, len(calls))}
a.output.write_text(json.dumps({"report": report, "rows": rows}, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2), flush=True)
