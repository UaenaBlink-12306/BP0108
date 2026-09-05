from __future__ import annotations

import argparse
from pathlib import Path

import UnityPy


NAME_ALIASES = {
    "\u7b2c\u4e00\u5e27": "first_frame",
    "\u7b2c\u4e8c\u5e27": "second_frame",
    "\u7b2c\u4e09\u5e27": "third_frame",
    "\u7b2c\u56db\u5e27": "fourth_frame",
    "\u753b\u9762\u80cc\u666f": "screen_background",
    "\u7070\u8272\u5f39\u7a97_\u7528\u4e8e\u663e\u793a\u5e73\u5c40\u6216\u8005\u5176\u4ed6\u4fe1\u606f": "match_update_modal",
}


def sanitize(name: str) -> str:
    name = NAME_ALIASES.get(name.strip(), name)
    cleaned = "".join(ch if ch.isalnum() or ch in ("-", "_") else "_" for ch in name.strip())
    return cleaned or "unnamed"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--data-root",
        default=r"C:\Users\ReneeDeng\Desktop\BP0108\BP0108\BP0108_Data",
        help="Unity *_Data directory",
    )
    parser.add_argument(
        "--output",
        default=r"C:\Users\ReneeDeng\Desktop\BP0108\tmp\exported_sprites",
        help="Directory to write sprite PNGs into",
    )
    args = parser.parse_args()

    out_dir = Path(args.output)
    out_dir.mkdir(parents=True, exist_ok=True)

    env = UnityPy.load(args.data_root)
    exported = 0

    for obj in env.objects:
        if obj.type.name != "Sprite":
            continue

        data = obj.read()
        name = getattr(data, "m_Name", "") or f"sprite_{obj.path_id}"
        image = data.image
        target = out_dir / f"{sanitize(name)}_{obj.path_id}.png"
        image.save(target)
        exported += 1
        print(target)

    print(f"Exported {exported} sprites to {out_dir}")


if __name__ == "__main__":
    main()
