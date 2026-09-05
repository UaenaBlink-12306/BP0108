import argparse
import json
from datetime import datetime
from pathlib import Path

from PIL import Image


ROOT = Path(r"C:\Users\alpac\Desktop\BP0108")
GENERATED_ROOT = Path(r"C:\Users\alpac\.codex\generated_images")
IMAGE_DIR = ROOT / "question_images"
TMP = ROOT / "tmp" / "imagegen"
PROGRESS_PATH = TMP / "replacement_progress.json"
MANIFEST_PATH = ROOT / "stream_questions_100.json"
CURSOR_PATH = TMP / "generation_cursor.json"


def suffix_num(qid):
    try:
        return int(qid.rsplit("_", 1)[1])
    except Exception:
        return 0


def sort_key(entry):
    qid = entry["id"]
    if qid.startswith("team_more_"):
        group = 0
    elif qid.startswith("nonteam_more_"):
        group = 1
    else:
        group = 2
    return (group, suffix_num(qid), qid)


def load_prompt_rows():
    rows = {}
    for name in ("team_more_prompts.jsonl", "nonteam_more_prompts.jsonl"):
        path = TMP / name
        if not path.exists():
            continue
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                row = json.loads(line)
                rows[row["id"]] = row
    return rows


def cursor_mtime():
    if not CURSOR_PATH.exists():
        return 0
    try:
        return float(json.loads(CURSOR_PATH.read_text(encoding="utf-8")).get("mtime", 0))
    except Exception:
        return 0


def latest_unused_source(progress):
    used = {Path(entry["source"]).resolve() for entry in progress if entry.get("source")}
    minimum_mtime = cursor_mtime()
    candidates = sorted(GENERATED_ROOT.rglob("*.png"), key=lambda p: p.stat().st_mtime, reverse=True)
    for candidate in candidates:
        if candidate.stat().st_mtime <= minimum_mtime:
            continue
        if candidate.resolve() not in used:
            return candidate
    raise RuntimeError(f"No unused generated PNG found in {GENERATED_ROOT}")


def normalize_image(source, destination):
    with Image.open(source) as img:
        img = img.convert("RGB")
        target = (2048, 1152)
        src_ratio = img.width / img.height
        target_ratio = target[0] / target[1]
        if src_ratio > target_ratio:
            new_h = target[1]
            new_w = round(new_h * src_ratio)
        else:
            new_w = target[0]
            new_h = round(new_w / src_ratio)
        resized = img.resize((new_w, new_h), Image.Resampling.LANCZOS)
        left = max(0, (new_w - target[0]) // 2)
        top = max(0, (new_h - target[1]) // 2)
        final = resized.crop((left, top, left + target[0], top + target[1]))
        final.save(destination, format="PNG")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("id")
    parser.add_argument("--source")
    args = parser.parse_args()
    qid = args.id

    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    items = {item["id"]: item for item in manifest["items"]}
    if qid not in items:
        raise RuntimeError(f"{qid} is not in {MANIFEST_PATH}")

    progress = json.loads(PROGRESS_PATH.read_text(encoding="utf-8")) if PROGRESS_PATH.exists() else []
    source = Path(args.source) if args.source else latest_unused_source(progress)
    if not source.exists():
        raise RuntimeError(f"Source image does not exist: {source}")
    destination = IMAGE_DIR / f"{qid}.png"
    normalize_image(source, destination)

    prompt_rows = load_prompt_rows()
    item = items[qid]
    row = prompt_rows.get(qid, {})

    progress = [entry for entry in progress if entry.get("id") != qid]
    progress.append(
        {
            "id": qid,
            "answer": item.get("answer") or row.get("answer", ""),
            "source": str(source),
            "destination": str(destination),
            "mode": "built-in image_gen",
            "normalized_size": [2048, 1152],
            "replaced_at": datetime.now().replace(microsecond=0).isoformat(),
            "prompt": row.get("prompt") or item.get("meta", {}).get("image_prompt", ""),
        }
    )
    progress.sort(key=sort_key)
    PROGRESS_PATH.write_text(json.dumps(progress, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    CURSOR_PATH.write_text(
        json.dumps(
            {
                "source": str(source),
                "mtime": source.stat().st_mtime,
                "updated_at": datetime.now().replace(microsecond=0).isoformat(),
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )

    with Image.open(destination) as img:
        width, height = img.size
    print(
        json.dumps(
            {
                "id": qid,
                "source": str(source),
                "destination": str(destination),
                "width": width,
                "height": height,
                "progress_entries": len(progress),
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
