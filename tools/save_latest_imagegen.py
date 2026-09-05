from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path

from PIL import Image, ImageOps


GENERATED_ROOT = Path(r"C:\Users\alpac\.codex\generated_images")
PROJECT_ROOT = Path(r"C:\Users\alpac\Desktop\BP0108")
PROGRESS_PATH = PROJECT_ROOT / "tmp" / "imagegen" / "replacement_progress.json"
IMAGE_DIR = PROJECT_ROOT / "question_images"


def load_progress() -> list[dict]:
    if not PROGRESS_PATH.exists():
        return []
    try:
        return json.loads(PROGRESS_PATH.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return []


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--id", required=True)
    parser.add_argument("--answer", required=True)
    args = parser.parse_args()

    candidates = list(GENERATED_ROOT.rglob("*.png"))
    if not candidates:
        raise SystemExit("No generated PNG files found.")

    src = max(candidates, key=lambda path: path.stat().st_mtime)
    dst = IMAGE_DIR / f"{args.id}.png"

    image = Image.open(src).convert("RGB")
    normalized = ImageOps.fit(
        image,
        (2048, 1152),
        method=Image.Resampling.LANCZOS,
        centering=(0.5, 0.5),
    )
    normalized.save(dst, "PNG", optimize=True)

    PROGRESS_PATH.parent.mkdir(parents=True, exist_ok=True)
    progress = [entry for entry in load_progress() if entry.get("id") != args.id]
    progress.append(
        {
            "id": args.id,
            "answer": args.answer,
            "source": str(src),
            "destination": str(dst),
            "mode": "built-in image_gen",
            "normalized_size": [2048, 1152],
            "replaced_at": datetime.now().isoformat(timespec="seconds"),
        }
    )
    PROGRESS_PATH.write_text(json.dumps(progress, indent=2), encoding="utf-8")

    print(f"{src}")
    print(f"-> {dst} ({dst.stat().st_size} bytes); progress entries={len(progress)}")


if __name__ == "__main__":
    main()
