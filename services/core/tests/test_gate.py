from __future__ import annotations

import sys
import tempfile
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

# isolate DB
import services.core.db as db

db.STORAGE = ROOT / "storage" / "test"
db.DB_PATH = db.STORAGE / "test.db"
db.PROJECTS_DIR = db.STORAGE / "projects"
db.STORAGE.mkdir(parents=True, exist_ok=True)
if db.DB_PATH.exists():
    db.DB_PATH.unlink()

from services.core.main import app
from services.core.db import init_db

init_db()
client = TestClient(app)


def test_health():
    r = client.get("/health")
    assert r.status_code == 200
    assert r.json()["status"] == "ok"


def test_generate_requires_refine_but_no_reading_gate():
    r = client.post(
        "/project",
        json={
            "parent_dir": tempfile.mkdtemp(prefix="novelgm_gate_"),
            "title": "闸门测试",
            "entries": [{"category": "world", "content": "测试"}],
            "characters": [
                {
                    "id": "p",
                    "name": "甲",
                    "role": "protagonist",
                    "public_face": "",
                    "secret": "",
                    "goals": [],
                    "voice": "",
                    "relations": [],
                    "known_facts": [],
                    "status": {},
                }
            ],
            "style": {"pov": "third_limited", "tense": "past"},
            "threads": [
                {
                    "id": "main",
                    "name": "主线",
                    "kind": "main",
                    "state": "active",
                    "user_directive": "推进",
                    "next_milestone": "",
                    "priority": 1,
                }
            ],
        },
    )
    assert r.status_code == 200
    pid = r.json()["id"]

    # force provider mock via settings
    client.patch(f"/project/{pid}/settings", json={"provider": "mock"})

    ch = client.post(f"/project/{pid}/plan", json={"user_notes": ""}).json()
    n = ch["no"]
    gen = client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    assert gen.status_code == 200
    fin = client.post(f"/project/{pid}/chapter/{n}/finalize")
    assert fin.status_code == 200

    # 未提交「创作下一章」时可以先规划，但下一章无法生成
    assert client.post(f"/project/{pid}/plan", json={"user_notes": "下一章"}).status_code == 200
    blocked = client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    assert blocked.status_code == 409
    assert blocked.json()["detail"]["code"] == "REFINE_REQUIRED"

    # 不需要确认读完，直接细修即可继续
    ref = client.post(
        f"/project/{pid}/chapter/{n}/refine",
        json={
            "chapter_no": n,
            "user_annotations": "ok",
            "next_chapter_intent": "推进冲突",
            "thread_updates": [],
            "bible_edits": [],
            "constraints_for_next": [],
        },
    )
    assert ref.status_code == 200
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    ok = client.post(f"/project/{pid}/chapter", json={"prefix": "雨还在下。", "confirm_plan": True})
    assert ok.status_code == 200


def test_style_banned_detection():
    from services.core.style import style_check

    report = style_check("他心中一紧，仿佛五味杂陈，瞳孔骤缩。")
    assert report.verdict in ("fail", "marginal")
    assert len(report.banned_phrase_hits) >= 3
