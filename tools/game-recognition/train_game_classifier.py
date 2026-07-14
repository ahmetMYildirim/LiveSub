"""Fine-tune an EfficientNet-B0 classifier and export it to ONNX for PsGameTranslator."""

from __future__ import annotations

import argparse
import json
import random
import time
from pathlib import Path

import torch
from torch import Tensor, nn, optim
from torch.utils.data import DataLoader
from torchvision import datasets, models, transforms


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--epochs", type=int, default=18)
    parser.add_argument("--freeze-epochs", type=int, default=2)
    parser.add_argument("--batch-size", type=int, default=16)
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--learning-rate", type=float, default=3e-4)
    parser.add_argument("--seed", type=int, default=20260713)
    return parser.parse_args()


def set_seed(seed: int) -> None:
    random.seed(seed)
    torch.manual_seed(seed)
    torch.cuda.manual_seed_all(seed)


def accuracy(logits: Tensor, targets: Tensor) -> tuple[int, int]:
    top1 = (logits.argmax(dim=1) == targets).sum().item()
    top_k = min(5, logits.shape[1])
    top5 = logits.topk(top_k, dim=1).indices.eq(targets.unsqueeze(1)).any(dim=1).sum().item()
    return top1, top5


def run_epoch(
    model: nn.Module,
    loader: DataLoader,
    criterion: nn.Module,
    device: torch.device,
    optimizer: optim.Optimizer | None = None,
    scaler: torch.amp.GradScaler | None = None,
) -> dict[str, float]:
    is_training = optimizer is not None
    model.train(is_training)
    loss_sum = 0.0
    top1_sum = 0
    top5_sum = 0
    sample_count = 0

    for images, targets in loader:
        images = images.to(device, non_blocking=True)
        targets = targets.to(device, non_blocking=True)

        if is_training:
            optimizer.zero_grad(set_to_none=True)

        with torch.autocast(device_type=device.type, enabled=device.type == "cuda"):
            logits = model(images)
            loss = criterion(logits, targets)

        if is_training:
            assert scaler is not None
            scaler.scale(loss).backward()
            scaler.step(optimizer)
            scaler.update()

        batch_size = targets.shape[0]
        top1, top5 = accuracy(logits.detach(), targets)
        loss_sum += loss.item() * batch_size
        top1_sum += top1
        top5_sum += top5
        sample_count += batch_size

    return {
        "loss": loss_sum / sample_count,
        "top1": top1_sum / sample_count,
        "top5": top5_sum / sample_count,
    }


def main() -> None:
    args = parse_args()
    set_seed(args.seed)

    train_dir = args.data_root / "train"
    validation_dir = args.data_root / "validation"
    test_dir = args.data_root / "test"
    args.output_dir.mkdir(parents=True, exist_ok=True)

    train_transform = transforms.Compose(
        [
            transforms.RandomResizedCrop(224, scale=(0.75, 1.0)),
            transforms.RandomHorizontalFlip(),
            transforms.ColorJitter(brightness=0.15, contrast=0.15, saturation=0.1),
            transforms.ToTensor(),
            transforms.Normalize(mean=(0.485, 0.456, 0.406), std=(0.229, 0.224, 0.225)),
        ]
    )
    evaluation_transform = transforms.Compose(
        [
            transforms.Resize(256),
            transforms.CenterCrop(224),
            transforms.ToTensor(),
            transforms.Normalize(mean=(0.485, 0.456, 0.406), std=(0.229, 0.224, 0.225)),
        ]
    )

    train_set = datasets.ImageFolder(train_dir, transform=train_transform)
    validation_set = datasets.ImageFolder(validation_dir, transform=evaluation_transform)
    test_set = datasets.ImageFolder(test_dir, transform=evaluation_transform)

    if train_set.classes != validation_set.classes or train_set.classes != test_set.classes:
        raise RuntimeError("Train, validation, and test class folders do not match.")

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    pin_memory = device.type == "cuda"
    train_loader = DataLoader(
        train_set,
        batch_size=args.batch_size,
        shuffle=True,
        num_workers=args.workers,
        pin_memory=pin_memory,
        persistent_workers=args.workers > 0,
    )
    validation_loader = DataLoader(
        validation_set,
        batch_size=args.batch_size,
        shuffle=False,
        num_workers=args.workers,
        pin_memory=pin_memory,
        persistent_workers=args.workers > 0,
    )
    test_loader = DataLoader(
        test_set,
        batch_size=args.batch_size,
        shuffle=False,
        num_workers=args.workers,
        pin_memory=pin_memory,
        persistent_workers=args.workers > 0,
    )

    weights = models.EfficientNet_B0_Weights.IMAGENET1K_V1
    model = models.efficientnet_b0(weights=weights)
    feature_count = model.classifier[1].in_features
    model.classifier[1] = nn.Linear(feature_count, len(train_set.classes))
    model.to(device)

    criterion = nn.CrossEntropyLoss(label_smoothing=0.1)
    optimizer = optim.AdamW(model.parameters(), lr=args.learning_rate, weight_decay=1e-4)
    scheduler = optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs)
    scaler = torch.amp.GradScaler("cuda", enabled=device.type == "cuda")

    labels_path = args.output_dir / "labels.json"
    labels_path.write_text(json.dumps(train_set.classes, ensure_ascii=False, indent=2), encoding="utf-8")
    metrics_path = args.output_dir / "metrics.jsonl"
    best_checkpoint = args.output_dir / "best_game_recognition.pt"
    best_validation_top1 = -1.0

    print(
        f"Training started | device={device.type} | classes={len(train_set.classes)} | "
        f"train={len(train_set)} | validation={len(validation_set)} | test={len(test_set)}",
        flush=True,
    )

    for epoch in range(1, args.epochs + 1):
        started_at = time.perf_counter()
        train_features = epoch > args.freeze_epochs
        for parameter in model.features.parameters():
            parameter.requires_grad = train_features

        train_metrics = run_epoch(model, train_loader, criterion, device, optimizer, scaler)
        validation_metrics = run_epoch(model, validation_loader, criterion, device)
        scheduler.step()

        is_best = validation_metrics["top1"] > best_validation_top1
        if is_best:
            best_validation_top1 = validation_metrics["top1"]
            torch.save(
                {
                    "epoch": epoch,
                    "model_state_dict": model.state_dict(),
                    "classes": train_set.classes,
                    "validation_metrics": validation_metrics,
                },
                best_checkpoint,
            )

        epoch_metrics = {
            "epoch": epoch,
            "learning_rate": optimizer.param_groups[0]["lr"],
            "train": train_metrics,
            "validation": validation_metrics,
            "best_model_updated": is_best,
            "seconds": round(time.perf_counter() - started_at, 1),
        }
        with metrics_path.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(epoch_metrics) + "\n")

        print(
            f"Epoch {epoch}/{args.epochs} | "
            f"train_loss={train_metrics['loss']:.4f} | train_top1={train_metrics['top1']:.2%} | "
            f"val_loss={validation_metrics['loss']:.4f} | val_top1={validation_metrics['top1']:.2%} | "
            f"val_top5={validation_metrics['top5']:.2%} | best={'yes' if is_best else 'no'} | "
            f"time={epoch_metrics['seconds']}s",
            flush=True,
        )

    checkpoint = torch.load(best_checkpoint, map_location=device, weights_only=True)
    model.load_state_dict(checkpoint["model_state_dict"])
    test_metrics = run_epoch(model, test_loader, criterion, device)
    print(
        f"Test | loss={test_metrics['loss']:.4f} | top1={test_metrics['top1']:.2%} | top5={test_metrics['top5']:.2%}",
        flush=True,
    )

    model.eval().cpu()
    onnx_path = args.output_dir / "game_recognition_efficientnet_b0.onnx"
    dummy_input = torch.randn(1, 3, 224, 224)
    torch.onnx.export(
        model,
        dummy_input,
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
        "class_count": len(train_set.classes),
    }
    (args.output_dir / "model_metadata.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"ONNX model exported: {onnx_path}", flush=True)


if __name__ == "__main__":
    main()

