"""Evaluate a saved game-recognition checkpoint and export the model to ONNX."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import torch
from torch import nn
from torch.utils.data import DataLoader
from torchvision import datasets, models, transforms


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--batch-size", type=int, default=32)
    parser.add_argument("--workers", type=int, default=2)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    checkpoint_path = args.output_dir / "best_game_recognition.pt"
    checkpoint = torch.load(checkpoint_path, map_location="cpu", weights_only=True)
    classes = checkpoint["classes"]

    model = models.efficientnet_b0(weights=None)
    model.classifier[1] = nn.Linear(model.classifier[1].in_features, len(classes))
    model.load_state_dict(checkpoint["model_state_dict"])
    model.eval()

    transform = transforms.Compose(
        [
            transforms.Resize(256),
            transforms.CenterCrop(224),
            transforms.ToTensor(),
            transforms.Normalize(mean=(0.485, 0.456, 0.406), std=(0.229, 0.224, 0.225)),
        ]
    )
    test_set = datasets.ImageFolder(args.data_root / "test", transform=transform)
    if test_set.classes != classes:
        raise RuntimeError("The checkpoint labels and test folder labels do not match.")

    loader = DataLoader(test_set, batch_size=args.batch_size, shuffle=False, num_workers=args.workers)
    criterion = nn.CrossEntropyLoss(label_smoothing=0.1)
    loss_sum = 0.0
    top1_sum = 0
    top5_sum = 0
    count = 0
    with torch.inference_mode():
        for images, targets in loader:
            logits = model(images)
            loss_sum += criterion(logits, targets).item() * targets.shape[0]
            top1_sum += (logits.argmax(dim=1) == targets).sum().item()
            top5_sum += logits.topk(min(5, len(classes)), dim=1).indices.eq(targets.unsqueeze(1)).any(dim=1).sum().item()
            count += targets.shape[0]

    test_metrics = {"loss": loss_sum / count, "top1": top1_sum / count, "top5": top5_sum / count}
    print(
        f"Test | loss={test_metrics['loss']:.4f} | top1={test_metrics['top1']:.2%} | top5={test_metrics['top5']:.2%}",
        flush=True,
    )

    onnx_path = args.output_dir / "game_recognition_efficientnet_b0.onnx"
    torch.onnx.export(
        model,
        torch.randn(1, 3, 224, 224),
        onnx_path,
        input_names=["image"],
        output_names=["logits"],
        dynamic_axes={"image": {0: "batch"}, "logits": {0: "batch"}},
        opset_version=17,
        dynamo=False,
    )

    metadata = {
        "architecture": "efficientnet_b0",
        "input_name": "image",
        "output_name": "logits",
        "input_shape": [1, 3, 224, 224],
        "normalization": {"mean": [0.485, 0.456, 0.406], "std": [0.229, 0.224, 0.225]},
        "best_epoch": checkpoint["epoch"],
        "validation_metrics": checkpoint["validation_metrics"],
        "test_metrics": test_metrics,
        "class_count": len(classes),
    }
    (args.output_dir / "model_metadata.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"ONNX model exported: {onnx_path}", flush=True)


if __name__ == "__main__":
    main()
