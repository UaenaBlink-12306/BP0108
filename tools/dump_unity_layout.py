from __future__ import annotations

import json
from pathlib import Path

import UnityPy


DATA_ROOT = Path(r"C:\Users\ReneeDeng\Desktop\BP0108\BP0108\BP0108_Data")
OUT_PATH = Path(r"C:\Users\ReneeDeng\Desktop\BP0108\tmp\unity_layout_dump.json")
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


def vector_to_list(value):
    if value is None:
        return None
    if isinstance(value, (list, tuple)):
        return list(value)
    parts = []
    for attr in ("x", "y", "z", "w"):
        if hasattr(value, attr):
            parts.append(getattr(value, attr))
    return parts


def read_all():
    env = UnityPy.load(str(DATA_ROOT))
    game_objects = {}
    transforms = {}
    components_by_go = {}
    names = {}

    for obj in env.objects:
        try:
            data = obj.read()
        except Exception:
            continue

        type_name = obj.type.name
        if type_name == "GameObject":
            name = english_name(getattr(data, "m_Name", "")) or "<unnamed>"
            game_objects[obj.path_id] = data
            names[obj.path_id] = name
            components_by_go[obj.path_id] = [c.path_id for c in getattr(data, "m_Component", []) if getattr(c, "path_id", None)]
        elif type_name in {"RectTransform", "Transform"}:
            transforms[obj.path_id] = data

    go_by_transform = {}
    parent_transform_by_transform = {}
    transform_by_go = {}
    for path_id, transform in transforms.items():
        go_ref = getattr(transform, "m_GameObject", None)
        go_id = getattr(go_ref, "path_id", None)
        parent_ref = getattr(transform, "m_Father", None)
        parent_id = getattr(parent_ref, "path_id", None)
        if go_id is not None:
            go_by_transform[path_id] = go_id
            transform_by_go[go_id] = path_id
        parent_transform_by_transform[path_id] = parent_id

    def build_path(go_id: int) -> str:
        current_go = go_id
        parts = []
        visited = set()
        while current_go is not None and current_go not in visited:
            visited.add(current_go)
            parts.append(names.get(current_go, f"<{current_go}>"))
            current_transform_id = transform_by_go.get(current_go)
            parent_transform_id = parent_transform_by_transform.get(current_transform_id)
            current_go = go_by_transform.get(parent_transform_id)
        return "/".join(reversed(parts))

    interesting = []
    keywords = ("UI/", "TestCanvas/", "Dialogue", "Ranklist", "avatar", "Blood_", "ROUND", "\u9898\u76ee\u6570", "Image/Panel", "Text (Legacy)")

    for go_id, name in names.items():
        path = build_path(go_id)
        if not any(keyword in path or keyword == name for keyword in keywords):
            continue

        transform_id = transform_by_go.get(go_id)
        transform = transforms.get(transform_id)
        if transform is None:
            continue

        record = {
            "name": name,
            "path": path,
            "transformType": type(transform).__name__,
            "localPosition": vector_to_list(getattr(transform, "m_LocalPosition", None)),
            "localScale": vector_to_list(getattr(transform, "m_LocalScale", None)),
            "components": components_by_go.get(go_id, []),
        }

        # RectTransform-only fields
        for field in (
            "m_AnchorMin",
            "m_AnchorMax",
            "m_AnchoredPosition",
            "m_SizeDelta",
            "m_Pivot",
        ):
            value = getattr(transform, field, None)
            if value is not None:
                record[field] = vector_to_list(value)

        interesting.append(record)

    interesting.sort(key=lambda item: item["path"])
    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUT_PATH.write_text(json.dumps(interesting, indent=2, ensure_ascii=False), encoding="utf-8")
    print(OUT_PATH)


if __name__ == "__main__":
    read_all()
