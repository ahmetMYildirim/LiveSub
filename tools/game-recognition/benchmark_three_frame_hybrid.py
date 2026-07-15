"""Benchmark three-frame ONNX averaging with a fixed low-margin Ollama gate."""
from __future__ import annotations
import argparse, base64, json, random, re, time, unicodedata
from pathlib import Path
import numpy as np
import onnxruntime as ort
import requests
from benchmark_onnx_ollama import image_tensor

def norm(v): return re.sub(r'[^a-z0-9]+',' ',unicodedata.normalize('NFKD',v).encode('ascii','ignore').decode().lower()).strip()
p=argparse.ArgumentParser(); p.add_argument('--data-root',type=Path,required=True); p.add_argument('--onnx',type=Path,required=True); p.add_argument('--labels',type=Path,required=True); p.add_argument('--exclude',type=Path,required=True); p.add_argument('--output',type=Path,required=True); p.add_argument('--samples',type=int,default=100); p.add_argument('--margin',type=float,default=.0974299982); p.add_argument('--model',default='gemma3:4b'); a=p.parse_args()
labels=json.loads(a.labels.read_text(encoding='utf-8')); games=[json.loads(x) for x in (a.data_root/'metadata'/'games.jsonl').read_text(encoding='utf-8').splitlines() if x]; titles={x['label']:x['title'] for x in games}
excluded={x['truth'] for x in json.loads(a.exclude.read_text(encoding='utf-8'))['rows']}; groups=[]
for label in labels:
    files=sorted((a.data_root/'test'/label).glob('*.jpg'))
    if titles[label] not in excluded and len(files)>=3: groups.append((label,files[:3]))
random.Random(20260718).shuffle(groups); groups=groups[:a.samples]
session=ort.InferenceSession(str(a.onnx),providers=['CPUExecutionProvider']); input_name=session.get_inputs()[0].name; rows=[]
for i,(label,files) in enumerate(groups,1):
    logits=np.mean([session.run(None,{input_name:image_tensor(x)})[0][0] for x in files],axis=0); prob=np.exp(logits-logits.max()); prob/=prob.sum(); order=np.argsort(prob)[-5:][::-1]; candidates=[titles[labels[int(x)]] for x in order]; margin=float(prob[order[0]]-prob[order[1]]); selected=None; latency=0.; raw='not_called'
    if margin<a.margin:
        prompt='Choose exactly one title from the candidates for these three screenshots, or Unknown. Reply only with the title.\nCandidates:\n- '+'\n- '.join(candidates); started=time.perf_counter(); response=requests.post('http://127.0.0.1:11434/api/generate',json={'model':a.model,'stream':False,'prompt':prompt,'images':[base64.b64encode(x.read_bytes()).decode() for x in files],'options':{'temperature':0.,'num_predict':32}},timeout=180); response.raise_for_status(); raw=str(response.json().get('response','')).strip(); answer=norm(raw.strip('". ')); selected=next((x for x in candidates if norm(x)==answer),None); latency=(time.perf_counter()-started)*1000
    prediction=selected or candidates[0]; rows.append({'truth':titles[label],'onnx_top1':candidates[0],'margin':margin,'ollama_called':margin<a.margin,'ollama_selected':selected,'correct':prediction==titles[label],'latency_ms':round(latency,1)}); print(f'[{i}/{len(groups)}] gate={margin<a.margin} correct={prediction==titles[label]}',flush=True)
calls=[x for x in rows if x['ollama_called']]; report={'samples':len(rows),'margin':a.margin,'three_frame_onnx_top1':sum(x['onnx_top1']==x['truth'] for x in rows)/len(rows),'three_frame_hybrid_top1':sum(x['correct'] for x in rows)/len(rows),'ollama_call_rate':len(calls)/len(rows),'mean_ollama_latency_ms':sum(x['latency_ms'] for x in calls)/max(1,len(calls))}; a.output.write_text(json.dumps({'report':report,'rows':rows},ensure_ascii=False,indent=2),encoding='utf-8'); print(json.dumps(report,indent=2),flush=True)
