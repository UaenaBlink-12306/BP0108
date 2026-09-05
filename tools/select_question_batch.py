import json
from pathlib import Path

WORKSPACE = Path(r"C:\Users\ReneeDeng\Desktop\BP0108")
SOURCE = WORKSPACE / "questions.json"
TARGET = WORKSPACE / "stream_questions_100.json"


def main() -> None:
    payload = json.loads(SOURCE.read_text(encoding="utf-8"))
    items = payload.get("items", [])[:100]
    result = {
        "id": payload.get("id", "stream_questions_100"),
        "name": "Stream Questions (100)",
        "categories": payload.get("categories", []),
        "items": items,
    }
    TARGET.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Wrote {len(items)} questions to {TARGET}")


if __name__ == "__main__":
    main()
