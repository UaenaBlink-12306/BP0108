from __future__ import annotations

import argparse
import shutil
from pathlib import Path

from PIL import Image
import UnityPy


DEFAULT_DATA_ROOT = Path(r"C:\Users\ReneeDeng\Desktop\BP0108\BP0108\BP0108_Data")
TEXTURE_FILE_ALIASES = {
    "\u753b\u9762\u80cc\u666f": "screen_background",
    "\u7070\u8272\u5f39\u7a97_\u7528\u4e8e\u663e\u793a\u5e73\u5c40\u6216\u8005\u5176\u4ed6\u4fe1\u606f": "match_update_modal",
}


def backup_path(path: Path) -> Path:
    return path.with_suffix(path.suffix + ".codexbak")


def ensure_backup(path: Path) -> Path:
    target = backup_path(path)
    if not target.exists():
        shutil.copy2(path, target)
    return target


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--asset-file", required=True, help="Asset file inside *_Data, e.g. sharedassets0.assets")
    parser.add_argument("--replace-dir", required=True, help="Directory of replacement PNGs named after Texture2D.m_Name")
    parser.add_argument("--data-root", default=str(DEFAULT_DATA_ROOT))
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    data_root = Path(args.data_root)
    asset_path = data_root / args.asset_file
    replace_dir = Path(args.replace_dir)

    if not asset_path.exists():
        raise FileNotFoundError(asset_path)
    if not replace_dir.exists():
        raise FileNotFoundError(replace_dir)

    env = UnityPy.load(str(asset_path))
    replaced_names: list[str] = []

    for obj in env.objects:
        if obj.type.name != "Texture2D":
            continue

        data = obj.read()
        texture_name = (getattr(data, "m_Name", "") or "").strip()
        if not texture_name:
            continue

        replacement_name = TEXTURE_FILE_ALIASES.get(texture_name, texture_name)
        replacement_path = replace_dir / f"{replacement_name}.png"
        if not replacement_path.exists():
            continue

        image = Image.open(replacement_path).convert("RGBA")
        data.image = image
        data.save()
        replaced_names.append(texture_name)
        print(f"replace {texture_name} <= {replacement_path.name}")

    if not replaced_names:
        print("No matching replacement textures were found.")
        return

    if args.dry_run:
        print(f"Dry run only. Would write {len(replaced_names)} textures into {asset_path}")
        return

    ensure_backup(asset_path)
    asset_path.write_bytes(env.file.save())
    print(f"Wrote {len(replaced_names)} textures into {asset_path}")


if __name__ == "__main__":
    main()
