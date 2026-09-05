from __future__ import annotations

import json
from pathlib import Path

import UnityPy


DATA_ROOT = Path(r"C:\Users\ReneeDeng\Desktop\BP0108\BP0108\BP0108_Data")
OUT_PATH = Path(r"C:\Users\ReneeDeng\Desktop\BP0108\tmp\unity_ui_components.json")
NAME_ALIASES = {
    "\u6392\u884c\u699c\u6210\u5458\u9884\u5236\u4f53": "Leaderboard Member Prefab",
    "\u9898\u76ee\u6570": "Question Count",
}


def english_name(name: str | None) -> str | None:
    if name is None:
        return None
    stripped = name.strip()
    for source, replacement in NAME_ALIASES.items():
        if stripped == source:
            return replacement
        suffix_prefix = f"{source} "
        if stripped.startswith(suffix_prefix):
            return replacement + stripped[len(source):]
    return name


def script_name(data) -> str | None:
    script = getattr(data, "m_Script", None)
    if script is None:
        return None
    try:
        script_data = script.read()
        return getattr(script_data, "m_Name", None)
    except Exception:
        return None


def read_name(pointer) -> str | None:
    if pointer is None:
        return None
    try:
        data = pointer.read()
    except Exception:
        return None
    return english_name(getattr(data, "m_Name", None))


def main() -> None:
    env = UnityPy.load(str(DATA_ROOT))
    game_objects = {}
    transforms = {}
    go_names = {}
    transform_by_go = {}
    go_by_transform = {}
    parent_transform_by_transform = {}

    for obj in env.objects:
        try:
            data = obj.read()
        except Exception:
            continue
        type_name = obj.type.name
        if type_name == "GameObject":
            game_objects[obj.path_id] = data
            go_names[obj.path_id] = english_name(getattr(data, "m_Name", "")) or "<unnamed>"
        elif type_name in {"RectTransform", "Transform"}:
            transforms[obj.path_id] = data
            go_ref = getattr(data, "m_GameObject", None)
            go_id = getattr(go_ref, "path_id", None)
            if go_id is not None:
                transform_by_go[go_id] = obj.path_id
                go_by_transform[obj.path_id] = go_id
            parent_ref = getattr(data, "m_Father", None)
            parent_transform_by_transform[obj.path_id] = getattr(parent_ref, "path_id", None)

    def build_path(go_id: int) -> str:
        parts = []
        current_go = go_id
        visited = set()
        while current_go is not None and current_go not in visited:
            visited.add(current_go)
            parts.append(go_names.get(current_go, f"<{current_go}>"))
            current_transform = transform_by_go.get(current_go)
            parent_transform = parent_transform_by_transform.get(current_transform)
            current_go = go_by_transform.get(parent_transform)
        return "/".join(reversed(parts))

    records = []
    for go_id, go_data in game_objects.items():
        path = build_path(go_id)
        if not path.startswith("UI/"):
            continue

        components = []
        for component_ref in getattr(go_data, "m_Component", []):
            pointer = getattr(component_ref, "component", component_ref)
            component_id = getattr(pointer, "path_id", None)
            assets_file = getattr(pointer, "assetsfile", None)
            if component_id is None or assets_file is None:
                continue

            component_obj = assets_file.objects.get(component_id)
            if component_obj is None:
                continue
            try:
                component_data = component_obj.read()
            except Exception:
                continue

            component_type = component_obj.type.name
            info = {"type": component_type, "path_id": component_id}

            if component_type == "MonoBehaviour":
                info["script"] = script_name(component_data)
            elif component_type == "Image":
                info["sprite"] = read_name(getattr(component_data, "m_Sprite", None))
                info["color"] = [
                    getattr(getattr(component_data, "m_Color", None), "r", None),
                    getattr(getattr(component_data, "m_Color", None), "g", None),
                    getattr(getattr(component_data, "m_Color", None), "b", None),
                    getattr(getattr(component_data, "m_Color", None), "a", None),
                ]
            elif component_type == "Text":
                info["text"] = getattr(component_data, "m_Text", None)
                font_ref = getattr(component_data, "m_FontData", None)
                info["fontSize"] = getattr(font_ref, "m_FontSize", None)
                info["alignment"] = getattr(font_ref, "m_Alignment", None)

            components.append(info)

        records.append({"path": path, "name": go_names[go_id], "components": components})

    records.sort(key=lambda item: item["path"])
    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUT_PATH.write_text(json.dumps(records, indent=2, ensure_ascii=False), encoding="utf-8")
    print(OUT_PATH)


if __name__ == "__main__":
    main()
