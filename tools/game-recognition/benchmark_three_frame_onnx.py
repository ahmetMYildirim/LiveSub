"""Evaluate mean-logit ONNX prediction over three unseen screenshots of one game."""
from __future__ import annotations
import argparse, json, random
from pathlib import Path
import numpy as np
import onnxruntime as ort
from benchmark_onnx_ollama import image_tensor

p=argparse.ArgumentParser(); p.add_argument('--data-root',type=Path,required=True); p.add_argument('--onnx',type=Path,required=True); p.add_argument('--labels',type=Path,required=True); p.add_argument('--exclude',type=Path,required=True); p.add_argument('--output',type=Path,required=True); p.add_argument('--samples',type=int,default=100); a=p.parse_args()
labels=json.loads(a.labels.read_text(encoding='utf-8'))
games=[json.loads(x) for x in (a.data_root/'metadata'/'games.jsonl').read_text(encoding='utf-8').splitlines() if x]
titles={x['label']:x['title'] for x in games}; used={x['image'] for x in json.loads(a.exclude.read_text(encoding='utf-8'))['rows']}
groups=[]
for label in labels:
    files=[x for x in sorted((a.data_root/'test'/label).glob('*.jpg')) if str(x) not in used]
    if len(files)>=3: groups.append((label,files[:3]))
random.Random(20260717).shuffle(groups); groups=groups[:a.samples]
if len(groups)<a.samples: raise SystemExit(f'Only {len(groups)} unused three-frame groups available.')
session=ort.InferenceSession(str(a.onnx),providers=['CPUExecutionProvider']); name=session.get_inputs()[0].name
rows=[]
for i,(label,files) in enumerate(groups,1):
    logits=np.mean([session.run(None,{name:image_tensor(x)})[0][0] for x in files],axis=0); top=np.argsort(logits)[-5:][::-1]
    rows.append({'truth':titles[label],'top1':titles[labels[int(top[0])]],'top5':[titles[labels[int(x)]] for x in top],'images':[str(x) for x in files]}); print(f'[{i}/{len(groups)}] {rows[-1]["top1"]} | {titles[label]}',flush=True)
report={'samples':len(rows),'three_frame_top1_accuracy':sum(x['top1']==x['truth'] for x in rows)/len(rows),'three_frame_top5_recall':sum(x['truth'] in x['top5'] for x in rows)/len(rows)}
a.output.write_text(json.dumps({'report':report,'rows':rows},ensure_ascii=False,indent=2),encoding='utf-8'); print(json.dumps(report,indent=2))
