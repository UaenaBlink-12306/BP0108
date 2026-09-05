from pathlib import Path
import sys

from PIL import Image


def ensure_16x9(source: Path, target: Path, size=(2048, 1152)) -> None:
    width, height = size
    target_ratio = width / height

    with Image.open(source) as image:
        image = image.convert("RGB")
        source_ratio = image.width / image.height

        if source_ratio > target_ratio:
            new_width = int(image.height * target_ratio)
            left = (image.width - new_width) // 2
            image = image.crop((left, 0, left + new_width, image.height))
        elif source_ratio < target_ratio:
            new_height = int(image.width / target_ratio)
            top = (image.height - new_height) // 2
            image = image.crop((0, top, image.width, top + new_height))

        image = image.resize((width, height), Image.LANCZOS)
        image.save(target, format="PNG")


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit("usage: ensure_16x9.py <source> <target>")

    source = Path(sys.argv[1])
    target = Path(sys.argv[2])
    ensure_16x9(source, target)


if __name__ == "__main__":
    main()
