from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

import UnityPy


NAME_ALIASES = {
    "\u4e0b\u4e00\u9898": "Next Question",
    "\u5f00\u59cb\u6e38\u620f": "Start Game",
    "\u65e0\u4eba": "No Team",
    "\u7ea2\u961f": "Red Team",
    "\u7ed3\u675f\u6e38\u620f": "End Game",
    "\u83b7\u53d6\u961f\u4f0d\u6210\u5458": "Get Team Members",
    "\u83b7\u53d6\u9898\u76ee\u5217\u8868": "Get Question List",
    "\u84dd\u961f": "Blue Team",
    "\u6392\u884c\u699c\u6210\u5458\u9884\u5236\u4f53": "Leaderboard Member Prefab",
    "\u9898\u76ee\u6570": "Question Count",
}


def english_name(name: str) -> str:
    stripped = (name or "").strip()
    for source, replacement in NAME_ALIASES.items():
        if stripped == source:
            return replacement
        suffix_prefix = f"{source} "
        if stripped.startswith(suffix_prefix):
            return replacement + stripped[len(source):]
    return name


def safe_name(data) -> str:
    for attr in ("m_Name", "name"):
        value = getattr(data, attr, None)
        if isinstance(value, str) and value.strip():
            return english_name(value.strip())

    game_object = getattr(data, "m_GameObject", None)
    if game_object is not None:
        try:
            resolved = game_object.read()
            value = getattr(resolved, "m_Name", None)
            if isinstance(value, str) and value.strip():
                return english_name(value.strip())
        except Exception:
            pass

    return ""


def try_path_id(pointer) -> int | None:
    value = getattr(pointer, "path_id", None)
    if isinstance(value, int):
        return value
    return None


def load_records(data_root: Path) -> tuple[list[dict], Counter]:
    env = UnityPy.load(str(data_root))
    records: list[dict] = []
    counts: Counter = Counter()
    game_objects: dict[int, dict] = {}
    transform_links: dict[int, tuple[int | None, int | None]] = {}

    for obj in env.objects:
        type_name = obj.type.name
        counts[type_name] += 1

        try:
            data = obj.read()
        except Exception:
            continue

        name = safe_name(data)
        container = getattr(obj, "assets_file", None)
        container_name = getattr(container, "name", "")

        if type_name == "GameObject":
            game_objects[obj.path_id] = {
                "path_id": obj.path_id,
                "name": name or "<unnamed>",
                "container": container_name,
            }
        elif type_name in {"RectTransform", "Transform"}:
            game_object_id = try_path_id(getattr(data, "m_GameObject", None))
            father_transform_id = try_path_id(getattr(data, "m_Father", None))
            transform_links[obj.path_id] = (game_object_id, father_transform_id)

        records.append(
            {
                "path_id": obj.path_id,
                "type": type_name,
                "name": name,
                "container": container_name,
            }
        )

    parent_by_game_object: dict[int, int | None] = {}
    for _, (game_object_id, father_transform_id) in transform_links.items():
        if game_object_id is None:
            continue

        parent_game_object_id = None
        if father_transform_id is not None and father_transform_id in transform_links:
            parent_game_object_id = transform_links[father_transform_id][0]

        parent_by_game_object[game_object_id] = parent_game_object_id

    resolved_paths: dict[int, str] = {}

    def build_game_object_path(game_object_id: int) -> str:
        if game_object_id in resolved_paths:
            return resolved_paths[game_object_id]

        current = game_objects.get(game_object_id)
        if current is None:
            return "<missing>"

        parent_id = parent_by_game_object.get(game_object_id)
        if parent_id is None or parent_id == game_object_id:
            path = current["name"]
        else:
            path = f'{build_game_object_path(parent_id)}/{current["name"]}'

        resolved_paths[game_object_id] = path
        return path

    for record in records:
        record["path"] = ""
        if record["type"] == "GameObject":
            record["path"] = build_game_object_path(record["path_id"])

    return records, counts


def filter_records(records: list[dict], query: str | None, types: set[str] | None) -> list[dict]:
    result = []
    needle = query.lower() if query else None

    for record in records:
        if types and record["type"] not in types:
            continue

        haystack = f'{record["type"]} {record["name"]} {record["container"]} {record.get("path", "")}'.lower()
        if needle and needle not in haystack:
            continue

        result.append(record)

    result.sort(key=lambda item: (item["type"], item["name"], item["container"], item["path_id"]))
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--data-root",
        default=r"C:\Users\ReneeDeng\Desktop\BP0108\BP0108\BP0108_Data",
        help="Unity *_Data directory",
    )
    parser.add_argument("--query", help="Case-insensitive name/type/container filter")
    parser.add_argument("--types", nargs="*", help="Object types to include, e.g. Sprite GameObject MonoBehaviour")
    parser.add_argument("--limit", type=int, default=120)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    records, counts = load_records(Path(args.data_root))
    filtered = filter_records(records, args.query, set(args.types) if args.types else None)

    if args.json:
        print(json.dumps({"counts": counts, "records": filtered[: args.limit]}, indent=2, default=int))
        return

    print("Object counts:")
    for type_name, count in counts.most_common():
        print(f"  {type_name}: {count}")

    print("")
    print(f"Matches ({min(len(filtered), args.limit)} of {len(filtered)}):")
    for record in filtered[: args.limit]:
        name = record["name"] or "<unnamed>"
        extra = f'    path={record["path"]}' if record.get("path") else ""
        print(f'  [{record["type"]}] {name}    file={record["container"]}    path_id={record["path_id"]}{extra}')


if __name__ == "__main__":
    main()
