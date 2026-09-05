import hashlib
import json
import math
import random
import re
import shutil
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "questions.json"
TARGET = ROOT / "stream_questions_100.json"
IMAGE_DIR = ROOT / "question_images"
TMP_DIR = ROOT / "tmp"

NEW_IDS = [f"team_more_{i:03d}" for i in range(101, 251)]
NEW_IDS += [f"nonteam_more_{i:03d}" for i in range(101, 251)]

CATEGORY_ORDER = [
    "Africa",
    "Central Asia",
    "East Asia",
    "Europe",
    "Latin America",
    "Middle East",
    "North America",
    "Oceania",
    "South Asia",
    "Southeast Asia",
    "World",
]

ERA_ORDER = ["01", "02", "03", "04", "05", "06", "07"]

SENTENCE_RE = re.compile(r"(?<=[.!?])\s+")


def stable_seed(value: str) -> int:
    return int(hashlib.sha256(value.encode("utf-8")).hexdigest()[:16], 16)


def split_sentences(text: str) -> list[str]:
    text = " ".join(text.replace("\n", " ").split())
    parts = [p.strip() for p in SENTENCE_RE.split(text) if p.strip()]
    return parts


def clean_sentence(text: str) -> str:
    text = text.replace(" ,", ",").replace(" .", ".")
    text = re.sub(r"\s+", " ", text).strip()
    return text


def two_sentence_question(question: str, answer: str) -> str:
    parts = split_sentences(question)
    if not parts:
        return f"This clue points to {answer}. For the point, name {answer}."

    final = parts[-1]
    lead_pool = parts[:-1]
    if not lead_pool:
        lead = final
        final = f"For the point, name {answer}."
    else:
        lead = lead_pool[-1]
        if len(lead) < 75 and len(lead_pool) >= 2:
            lead = f"{lead_pool[-2]} {lead}"

    lead = clean_sentence(lead)
    final = clean_sentence(final)

    if "for the point" not in final.lower():
        final = f"For the point, name {answer}."

    if not lead.endswith((".", "!", "?")):
        lead += "."
    if not final.endswith((".", "!", "?")):
        final += "."

    return f"{lead} {final}"


def image_prompt(item: dict) -> str:
    answer = item["answer"]
    category = item["meta"].get("category", "World")
    era = item["meta"].get("era", "06")
    return (
        "Use case: illustration-story. Asset type: livestream question image. "
        f"Primary request: high-quality no-text cartoon illustration for a history quiz clue about {answer}. "
        f"Scene/background: historically inspired {category} setting from era {era}. "
        "Style/medium: polished colorful cartoon, clean shapes, detailed background, cinematic 16:9 composition. "
        "Composition/framing: clear central subject, readable silhouettes, no captions. "
        "Constraints: no text, no letters, no watermark, no logos."
    )


def pick_source_items(source_items: list[dict], used_answers: set[str], used_ids: set[str]) -> list[dict]:
    selected = []
    local_answers = set(used_answers)
    for item in source_items:
        if len(selected) == len(NEW_IDS):
            break
        if item.get("id") in used_ids:
            continue
        answer = str(item.get("answer", "")).strip()
        question = str(item.get("question", "")).strip()
        meta = item.get("meta") or {}
        category = meta.get("category")
        era = meta.get("era")
        if not answer or not question or category not in CATEGORY_ORDER or era not in ERA_ORDER:
            continue
        key = answer.lower()
        if key in local_answers:
            continue
        local_answers.add(key)
        selected.append(item)
    if len(selected) != len(NEW_IDS):
        raise RuntimeError(f"Needed {len(NEW_IDS)} source questions, found {len(selected)}")
    return selected


def palette_for(category: str, seed: int) -> tuple[tuple[int, int, int], ...]:
    palettes = {
        "Africa": ((244, 183, 77), (110, 61, 36), (40, 121, 94), (238, 96, 71)),
        "Central Asia": ((95, 169, 190), (231, 194, 101), (116, 75, 130), (49, 74, 99)),
        "East Asia": ((227, 84, 73), (244, 198, 91), (67, 115, 153), (46, 79, 79)),
        "Europe": ((93, 129, 172), (210, 188, 132), (124, 83, 99), (62, 73, 92)),
        "Latin America": ((39, 150, 122), (239, 177, 71), (201, 74, 83), (63, 89, 156)),
        "Middle East": ((221, 172, 93), (68, 127, 122), (159, 83, 69), (72, 54, 92)),
        "North America": ((87, 139, 190), (232, 185, 91), (142, 84, 67), (64, 111, 87)),
        "Oceania": ((67, 166, 188), (236, 196, 110), (45, 118, 103), (184, 83, 96)),
        "South Asia": ((230, 141, 72), (151, 79, 138), (64, 145, 117), (244, 205, 94)),
        "Southeast Asia": ((44, 160, 144), (238, 182, 80), (91, 96, 151), (202, 87, 74)),
        "World": ((84, 139, 179), (238, 190, 95), (126, 92, 158), (55, 122, 105)),
    }
    colors = list(palettes.get(category, palettes["World"]))
    random.Random(seed).shuffle(colors)
    return tuple(colors)


def lerp(a: int, b: int, t: float) -> int:
    return int(a + (b - a) * t)


def draw_gradient(draw: ImageDraw.ImageDraw, size: tuple[int, int], top, bottom) -> None:
    w, h = size
    for y in range(h):
        t = y / max(1, h - 1)
        color = tuple(lerp(top[i], bottom[i], t) for i in range(3))
        draw.line([(0, y), (w, y)], fill=color)


def draw_person(draw: ImageDraw.ImageDraw, x: int, y: int, scale: float, colors, rng: random.Random) -> None:
    skin = (126 + rng.randrange(50), 78 + rng.randrange(45), 48 + rng.randrange(35))
    robe = colors[rng.randrange(len(colors))]
    outline = (41, 38, 45)
    r = int(28 * scale)
    draw.ellipse([x - r, y - int(95 * scale), x + r, y - int(39 * scale)], fill=skin, outline=outline, width=max(2, int(4 * scale)))
    draw.polygon(
        [
            (x, y - int(38 * scale)),
            (x - int(55 * scale), y + int(85 * scale)),
            (x + int(58 * scale), y + int(85 * scale)),
        ],
        fill=robe,
        outline=outline,
    )
    draw.line([(x - int(45 * scale), y + int(10 * scale)), (x + int(45 * scale), y + int(10 * scale))], fill=outline, width=max(2, int(6 * scale)))
    draw.arc([x - int(36 * scale), y - int(112 * scale), x + int(36 * scale), y - int(45 * scale)], 205, 335, fill=colors[-1], width=max(3, int(8 * scale)))


def draw_ship(draw: ImageDraw.ImageDraw, x: int, y: int, scale: float, colors) -> None:
    outline = (41, 38, 45)
    hull = colors[1]
    sail = (248, 238, 203)
    draw.polygon(
        [
            (x - int(120 * scale), y),
            (x + int(120 * scale), y),
            (x + int(75 * scale), y + int(45 * scale)),
            (x - int(85 * scale), y + int(45 * scale)),
        ],
        fill=hull,
        outline=outline,
    )
    draw.line([(x, y), (x, y - int(160 * scale))], fill=outline, width=max(4, int(8 * scale)))
    draw.polygon([(x + int(8 * scale), y - int(150 * scale)), (x + int(8 * scale), y - int(15 * scale)), (x + int(105 * scale), y - int(30 * scale))], fill=sail, outline=outline)
    draw.polygon([(x - int(8 * scale), y - int(130 * scale)), (x - int(8 * scale), y - int(20 * scale)), (x - int(92 * scale), y - int(34 * scale))], fill=(239, 216, 160), outline=outline)


def draw_building(draw: ImageDraw.ImageDraw, x: int, y: int, scale: float, colors, rng: random.Random) -> None:
    outline = (41, 38, 45)
    base = colors[2]
    accent = colors[0]
    w = int(240 * scale)
    h = int(170 * scale)
    draw.rectangle([x - w // 2, y - h, x + w // 2, y], fill=base, outline=outline, width=max(2, int(5 * scale)))
    if rng.random() < 0.45:
        draw.polygon([(x - w // 2 - int(18 * scale), y - h), (x, y - h - int(95 * scale)), (x + w // 2 + int(18 * scale), y - h)], fill=accent, outline=outline)
    else:
        draw.arc([x - w // 2, y - h - int(82 * scale), x + w // 2, y - h + int(88 * scale)], 180, 360, fill=outline, width=max(2, int(6 * scale)))
        draw.pieslice([x - w // 2, y - h - int(82 * scale), x + w // 2, y - h + int(88 * scale)], 180, 360, fill=accent, outline=outline)
    for i in range(4):
        px = x - int(82 * scale) + i * int(55 * scale)
        draw.rectangle([px, y - int(115 * scale), px + int(24 * scale), y], fill=(245, 222, 161), outline=outline, width=max(1, int(3 * scale)))


def draw_landmarks(draw: ImageDraw.ImageDraw, rng: random.Random, category: str, colors) -> None:
    if category in {"East Asia", "Southeast Asia", "South Asia"}:
        for i in range(3):
            draw_building(draw, 1160 + i * 210, 770 - i * 25, 0.72 - i * 0.07, colors, rng)
    elif category in {"Middle East", "Central Asia"}:
        for i in range(3):
            draw_building(draw, 1060 + i * 230, 770, 0.78, colors, rng)
            draw.rectangle([1140 + i * 230, 515, 1170 + i * 230, 770], fill=colors[3], outline=(41, 38, 45), width=5)
    elif category in {"Africa", "Latin America"}:
        for i in range(3):
            x = 1080 + i * 205
            draw.polygon([(x - 85, 770), (x, 520 - i * 28), (x + 85, 770)], fill=colors[i % len(colors)], outline=(41, 38, 45))
    elif category == "Oceania":
        for i in range(4):
            x = 1030 + i * 170
            draw.ellipse([x - 45, 560, x + 45, 740], fill=colors[i % len(colors)], outline=(41, 38, 45), width=6)
    else:
        for i in range(3):
            draw_building(draw, 1060 + i * 235, 780, 0.8, colors, rng)


def make_image(path: Path, item: dict) -> None:
    w, h = 2048, 1152
    seed = stable_seed(item["id"] + item["answer"])
    rng = random.Random(seed)
    category = item["meta"].get("category", "World")
    colors = palette_for(category, seed)

    img = Image.new("RGB", (w, h))
    draw = ImageDraw.Draw(img)
    draw_gradient(draw, (w, h), tuple(min(255, c + 80) for c in colors[0]), (244, 229, 188))

    sun_x = 220 + rng.randrange(280)
    sun_y = 140 + rng.randrange(110)
    draw.ellipse([sun_x - 72, sun_y - 72, sun_x + 72, sun_y + 72], fill=(255, 226, 128), outline=(255, 245, 196), width=14)

    for i in range(4):
        y = 450 + i * 72
        color = tuple(max(0, c - 45 - i * 12) for c in colors[(i + 1) % len(colors)])
        pts = [(0, y + 190), (0, y)]
        for x in range(0, w + 260, 260):
            pts.append((x, y - rng.randrange(20, 130)))
        pts.extend([(w, y + 190), (w, h), (0, h)])
        draw.polygon(pts, fill=color)

    ground_y = 800
    draw.rectangle([0, ground_y, w, h], fill=(232, 197, 126))
    for i in range(90):
        x = rng.randrange(w)
        y = rng.randrange(ground_y + 30, h)
        draw.ellipse([x, y, x + rng.randrange(4, 13), y + rng.randrange(2, 7)], fill=(190, 138, 84))

    draw_landmarks(draw, rng, category, colors)

    if category in {"Europe", "World", "Middle East", "East Asia", "Southeast Asia"} and rng.random() < 0.55:
        draw_ship(draw, 420, 810, 1.05, colors)
    else:
        for i in range(4):
            draw.ellipse([300 + i * 95, 750 - i * 10, 380 + i * 95, 820 - i * 10], fill=colors[i % len(colors)], outline=(41, 38, 45), width=5)
            draw.line([(340 + i * 95, 820 - i * 10), (315 + i * 95, 880)], fill=(41, 38, 45), width=5)
            draw.line([(340 + i * 95, 820 - i * 10), (370 + i * 95, 880)], fill=(41, 38, 45), width=5)

    draw_person(draw, 700, 820, 1.35, colors, rng)
    draw_person(draw, 560, 865, 0.9, colors, rng)
    draw_person(draw, 850, 870, 0.9, colors, rng)

    for i in range(22):
        x = rng.randrange(60, w - 60)
        y = rng.randrange(70, 350)
        radius = rng.randrange(3, 8)
        draw.ellipse([x - radius, y - radius, x + radius, y + radius], fill=(255, 245, 196))

    # Soft finishing pass keeps the handmade cartoon edges crisp but removes flat fills.
    overlay = Image.new("RGBA", (w, h), (255, 255, 255, 0))
    od = ImageDraw.Draw(overlay)
    for i in range(28):
        x = rng.randrange(-200, w)
        y = rng.randrange(-100, h)
        od.ellipse([x, y, x + rng.randrange(180, 520), y + rng.randrange(80, 260)], fill=(255, 255, 255, rng.randrange(10, 28)))
    img = Image.alpha_composite(img.convert("RGBA"), overlay).convert("RGB")
    img = img.filter(ImageFilter.UnsharpMask(radius=1.0, percent=115, threshold=3))
    img.save(path, "PNG", optimize=True)


def main() -> None:
    TMP_DIR.mkdir(exist_ok=True)
    IMAGE_DIR.mkdir(exist_ok=True)

    source_payload = json.loads(SOURCE.read_text(encoding="utf-8"))
    stream_payload = json.loads(TARGET.read_text(encoding="utf-8"))
    source_items = source_payload["items"]
    stream_items = stream_payload["items"]

    existing_ids = {item["id"] for item in stream_items}
    if any(new_id in existing_ids for new_id in NEW_IDS):
        raise RuntimeError("One or more target IDs already exist; refusing to duplicate the addition.")

    used_answers = {str(item.get("answer", "")).strip().lower() for item in stream_items}
    selected = pick_source_items(source_items, used_answers, existing_ids)

    new_items = []
    for new_id, source in zip(NEW_IDS, selected):
        new_item = {
            "id": new_id,
            "question": two_sentence_question(source["question"], source["answer"]),
            "answer": source["answer"],
            "aliases": source.get("aliases", []),
            "meta": {
                "category": source["meta"]["category"],
                "era": source["meta"]["era"],
                "source": "original",
            },
        }
        new_item["meta"]["image_prompt"] = image_prompt(new_item)
        new_items.append(new_item)

    backup = TMP_DIR / "stream_questions_100.before_add_300.json"
    if not backup.exists():
        shutil.copy2(TARGET, backup)

    stream_payload["name"] = "Stream Questions (600)"
    stream_payload["items"] = stream_items + new_items
    TARGET.write_text(json.dumps(stream_payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    for item in new_items:
        make_image(IMAGE_DIR / f"{item['id']}.png", item)

    audit = {
        "added": len(new_items),
        "first_id": new_items[0]["id"],
        "last_id": new_items[-1]["id"],
        "total_items": len(stream_payload["items"]),
        "backup": str(backup),
        "images": len(new_items),
    }
    (TMP_DIR / "add_300_stream_questions_audit.json").write_text(
        json.dumps(audit, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(audit, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
