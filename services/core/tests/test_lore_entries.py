"""背景设定条目（取代原「梗概」）与 format 1 → 2 的迁移。"""
from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db

db.STORAGE = ROOT / "storage" / "test_lore"
db.DB_PATH = db.STORAGE / "test_lore.db"
db.PROJECTS_DIR = db.STORAGE / "projects"
db.STORAGE.mkdir(parents=True, exist_ok=True)
if db.DB_PATH.exists():
    db.DB_PATH.unlink()

from services.core.context import build_layered_context
from services.core.db import init_db
from services.core.main import app
from services.core.models import Project

init_db()
client = TestClient(app)
PARENT = Path(tempfile.mkdtemp(prefix="novelgm_lore_"))


def _create(title: str) -> dict:
    r = client.post(
        "/project",
        json={
            "title": title,
            "entries": [],
            "characters": [],
            "style": {},
            "threads": [],
            "parent_dir": str(PARENT),
        },
    )
    assert r.status_code == 200, r.text
    pid = r.json()["id"]
    client.patch(f"/project/{pid}/settings", json={"provider": "mock"})
    return r.json()


def _project(pid: str) -> Project:
    return Project(**client.get(f"/project/{pid}").json())


def test_rule_entry_goes_to_l0_world_entry_to_l1():
    pid = _create("分层测试")["id"]
    r = client.put(
        f"/project/{pid}/lore",
        json={
            "entries": [
                {"category": "world", "title": "大陆", "content": "三大王国分割大陆"},
                {"category": "rule", "title": "", "content": "死人不能复活", "hard": True},
                {"category": "rule", "title": "", "content": "水系法术需要吟唱", "hard": False},
                {"category": "geo", "title": "北境", "content": "终年积雪"},
                {"category": "item", "title": "旧铜钥匙", "content": "家族代代相传，只能开启北塔的门"},
            ],
            "characters": [],
            "threads": [],
        },
    )
    assert r.status_code == 200, r.text

    layers = build_layered_context(_project(pid), None, [])
    assert "死人不能复活" in layers["L0"]
    assert "水系法术需要吟唱" not in layers["L0"]  # 非硬规则不进 L0
    assert "死人不能复活" not in layers["L1"]
    assert "三大王国分割大陆" in layers["L1"]
    assert "终年积雪" in layers["L1"]  # 非硬规则的规则条目按分类进 L1
    assert "物件：" in layers["L1"]
    assert "旧铜钥匙：家族代代相传，只能开启北塔的门" in layers["L1"]
    assert _project(pid).entries[-1].category == "item"
    assert "水系法术需要吟唱" in layers["L1"]
    assert "【背景设定】" in layers["L1"]
    assert "故事梗概" not in layers["L1"]  # 梗概已彻底移除


def test_entries_inherit_to_next_chapter_and_stay_per_chapter():
    pid = _create("条目继承")["id"]
    client.put(
        f"/project/{pid}/lore",
        json={
            "entries": [{"category": "world", "content": "初始世界"}],
            "characters": [],
            "threads": [],
        },
    )
    ch1 = client.post(f"/project/{pid}/plan", json={"user_notes": ""}).json()
    assert ch1["lore"]["entries"][0]["content"] == "初始世界"

    # 在第 1 章上加一条，并让它成为最新章的来源
    r = client.put(
        f"/project/{pid}/lore?chapter=1",
        json={
            "entries": [
                {"category": "world", "content": "初始世界"},
                {"category": "faction", "content": "影卫"},
            ],
            "characters": [],
            "threads": [],
        },
    )
    assert r.status_code == 200, r.text

    client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    client.post(
        f"/project/{pid}/chapter/1/refine",
        json={
            "chapter_no": 1,
            "user_annotations": "",
            "thread_updates": [],
            "bible_edits": [],
            "constraints_for_next": [],
            "next_chapter_intent": "继续",
        },
    )
    ch2 = client.post(f"/project/{pid}/plan", json={"user_notes": ""}).json()
    assert [e["content"] for e in ch2["lore"]["entries"]] == ["初始世界", "影卫"]

    # 改较早的章不影响作品级
    client.put(
        f"/project/{pid}/lore?chapter=1",
        json={"entries": [{"category": "world", "content": "只改第一章"}], "characters": [], "threads": []},
    )
    assert [e["content"] for e in client.get(f"/project/{pid}").json()["entries"]] == ["初始世界", "影卫"]
    assert client.get(f"/project/{pid}/chapter/1").json()["lore"]["entries"][0]["content"] == "只改第一章"


def test_create_project_seeds_main_thread_from_first_world_entry():
    """新建作品没给脉络时，用第一条世界观条目当主线走向（原「梗概」的脉络职能）。"""
    r = client.post(
        "/project",
        json={
            "title": "开篇背景测试",
            "entries": [
                {"category": "geo", "content": "北境"},
                {"category": "world", "content": "三大王国对峙"},
            ],
            "characters": [],
            "style": {},
            "parent_dir": str(PARENT),
        },
    )
    assert r.status_code == 200, r.text
    body = r.json()
    assert [t["name"] for t in body["threads"]] == ["主线"]
    assert body["threads"][0]["user_directive"] == "三大王国对峙"  # 跳过非 world 分类


def test_bad_category_rejected():
    pid = _create("分类校验")["id"]
    r = client.put(
        f"/project/{pid}/lore",
        json={"entries": [{"category": "nope", "content": "x"}], "characters": [], "threads": []},
    )
    assert r.status_code == 400, r.text
    assert r.json()["detail"]["code"] == "BAD_CATEGORY"


def test_hard_forced_false_outside_rule_and_empty_entries_dropped():
    pid = _create("字段归一")["id"]
    r = client.put(
        f"/project/{pid}/lore",
        json={
            "entries": [
                {"category": "world", "content": "普通背景", "hard": True},
                {"category": "history", "title": "", "content": ""},
            ],
            "characters": [],
            "threads": [],
        },
    )
    assert r.status_code == 200, r.text
    entries = r.json()["entries"]
    assert len(entries) == 1  # 空条目被丢弃
    assert entries[0]["hard"] is False  # 非「规则」分类强制关掉硬规则
    assert entries[0]["id"]  # 自动补 id


def test_hard_entry_inherits_id_so_edits_stick():
    pid = _create("条目 id")["id"]
    r = client.put(
        f"/project/{pid}/lore",
        json={
            "entries": [{"category": "rule", "content": "不能飞", "hard": True}],
            "characters": [],
            "threads": [],
        },
    )
    eid = r.json()["entries"][0]["id"]
    assert eid

    r2 = client.put(
        f"/project/{pid}/lore",
        json={
            "entries": [{"id": eid, "category": "rule", "content": "不能飞也不能瞬移", "hard": True}],
            "characters": [],
            "threads": [],
        },
    )
    assert r2.json()["entries"][0]["id"] == eid
    assert r2.json()["entries"][0]["content"] == "不能飞也不能瞬移"


def test_legacy_format_migrates_on_open_and_is_idempotent():
    p = _create("迁移测试")
    pid, root = p["id"], Path(p["root_path"])
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})

    # 把作品文件夹倒回 format 1：premise + world_rules，并去掉 entries
    proj_file = root / "作品.json"
    data = json.loads(proj_file.read_text(encoding="utf-8"))
    data.pop("entries", None)
    data["premise"] = "旧版一句话梗概"
    data["world_rules"] = [{"id": "r1", "rule": "旧版硬规则", "hard": True}]
    proj_file.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")

    meta_file = root / ".novel-gm" / "chapters" / "0001.json"
    meta = json.loads(meta_file.read_text(encoding="utf-8"))
    meta["lore"].pop("entries", None)
    meta["lore"]["premise"] = "章内旧梗概"
    meta_file.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

    assert client.delete(f"/project/{pid}").status_code == 200  # 只删索引，文件夹留着
    r = client.post("/projects/open", json={"path": str(root)})
    assert r.status_code == 200, r.text

    entries = client.get(f"/project/{pid}").json()["entries"]
    assert [e["content"] for e in entries] == ["旧版一句话梗概", "旧版硬规则"]
    assert entries[0]["category"] == "world" and entries[0]["title"] == "故事背景"
    assert entries[1]["category"] == "rule" and entries[1]["hard"] is True

    ch_lore = client.get(f"/project/{pid}/chapter/1").json()["lore"]
    assert [e["content"] for e in ch_lore["entries"]] == ["章内旧梗概"]

    # 迁移回写：文件里不再有旧键
    written = json.loads(proj_file.read_text(encoding="utf-8"))
    assert "premise" not in written and "world_rules" not in written
    assert "entries" in written
    written_meta = json.loads(meta_file.read_text(encoding="utf-8"))
    assert "premise" not in written_meta["lore"]

    # 幂等：再打开一次不会重复迁移出第二条「故事背景」
    assert client.delete(f"/project/{pid}").status_code == 200
    assert client.post("/projects/open", json={"path": str(root)}).status_code == 200
    again = client.get(f"/project/{pid}").json()["entries"]
    assert [e["content"] for e in again] == ["旧版一句话梗概", "旧版硬规则"]
