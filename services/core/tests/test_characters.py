from __future__ import annotations

import sys
import tempfile
from pathlib import Path

from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db

db.STORAGE = ROOT / "storage" / "test_chars"
db.DB_PATH = db.STORAGE / "test_chars.db"
db.PROJECTS_DIR = db.STORAGE / "projects"
db.STORAGE.mkdir(parents=True, exist_ok=True)
if db.DB_PATH.exists():
    db.DB_PATH.unlink()

from services.core.db import init_db
from services.core.main import app

init_db()
client = TestClient(app)


def _project() -> str:
    r = client.post(
        "/project",
        json={
            "parent_dir": tempfile.mkdtemp(prefix="novelgm_chars_"),
            "title": "人物测试",
            "entries": [{"category": "world", "content": "p"}],
            "characters": [],
            "style": {},
            "threads": [
                {
                    "id": "main",
                    "name": "主线",
                    "kind": "main",
                    "state": "active",
                    "user_directive": "x",
                    "next_milestone": "",
                    "priority": 1,
                }
            ],
        },
    )
    assert r.status_code == 200
    return r.json()["id"]


def test_character_requires_only_name():
    pid = _project()
    bad = client.post(
        f"/project/{pid}/characters",
        json={"name": "  ", "age": "17"},
    )
    assert bad.status_code == 400
    assert bad.json()["detail"]["code"] == "REQUIRED_FIELDS"
    assert bad.json()["detail"]["missing"] == ["name"]

    # 新卡片只要求姓名：其余资料留空也能保存
    ok = client.post(f"/project/{pid}/characters", json={"name": "苍"})
    assert ok.status_code == 200, ok.text
    assert ok.json()["age"] == "" and ok.json()["gender"] == ""


def test_character_upsert_and_list():
    pid = _project()
    ok = client.post(
        f"/project/{pid}/characters",
        json={
            "name": "苍",
            "age": "17",
            "personality": "话少、手稳",
            "appearance": "黑发、略瘦",
            "outfit": "药师学徒短褂",
            "recent_thoughts": "先把井水测完",
            "role": "protagonist",
            "join_reason": "",
            "goals": ["治好村里的病"],
        },
    )
    assert ok.status_code == 200
    cid = ok.json()["id"]
    listed = client.get(f"/project/{pid}/characters")
    assert listed.status_code == 200
    assert len(listed.json()) == 1
    assert listed.json()[0]["recent_thoughts"] == "先把井水测完"

    upd = client.post(
        f"/project/{pid}/characters",
        json={
            "id": cid,
            "name": "苍",
            "age": "18",
            "personality": "话少、手稳",
            "appearance": "黑发、略瘦",
            "outfit": "药师学徒短褂",
            "recent_thoughts": "有人要毁采样",
            "goals": ["查清污染"],
        },
    )
    assert upd.status_code == 200
    assert upd.json()["age"] == "18"

    deleted = client.delete(f"/project/{pid}/characters/{cid}")
    assert deleted.status_code == 200
    assert deleted.json()["characters"] == []
