import json
import sys
from pathlib import Path

from PIL import Image


def main() -> None:
    if len(sys.argv) != 5:
        raise SystemExit("usage: split_vertical_sheet.py <sheet> <batch_json> <start_index> <output_dir>")

    sheet_path = Path(sys.argv[1])
    batch_path = Path(sys.argv[2])
    start_index = int(sys.argv[3])
    output_dir = Path(sys.argv[4])
    output_dir.mkdir(parents=True, exist_ok=True)

    batch = json.loads(batch_path.read_text(encoding="utf-8"))
    items = batch[start_index:start_index + 5]

    with Image.open(sheet_path) as sheet:
        sheet = sheet.convert("RGB")
        slice_height = sheet.height // 5
        for i, item in enumerate(items):
            top = i * slice_height
            bottom = sheet.height if i == 4 else (i + 1) * slice_height
            panel = sheet.crop((0, top, sheet.width, bottom))
            panel = trim_white_edges(panel)
            target = output_dir / f"{item['id']}.png"
            panel.save(target, format="PNG")


def trim_white_edges(image: Image.Image, threshold: int = 245) -> Image.Image:
    pixels = image.load()
    width, height = image.size

    def row_is_white(y: int) -> bool:
        for x in range(width):
            r, g, b = pixels[x, y]
            if r < threshold or g < threshold or b < threshold:
                return False
        return True

    top = 0
    while top < height and row_is_white(top):
        top += 1

    bottom = height - 1
    while bottom >= top and row_is_white(bottom):
        bottom -= 1

    return image.crop((0, top, width, bottom + 1))


if __name__ == "__main__":
    main()
