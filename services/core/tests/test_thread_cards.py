from __future__ import annotations

import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
import services.core.db as db
from services.core.main import app
from services.core.models import PlotThread
from services.core.repository import get_project
from services.core.context import build_layered_context

client = TestClient(app)


@pytest.fixture(autouse=True)
def storage(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path / "storage")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "storage" / "test.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "storage" / "projects")
    db.init_db()


def test_legacy_milestone_migrates_once_and_empty_list_is_authoritative():
    old = {"id": "t", "name": "主线", "next_milestone": "天台相遇"}
    first, second = PlotThread(**old), PlotThread(**old)
    assert first.time_position == "present" and first.tone == "" and first.misc == ""
    assert first.milestones[0].id == second.milestones[0].id
    assert first.milestones[0].name == "天台相遇"
    assert len(PlotThread(**first.model_dump()).milestones) == 1
    cleared = PlotThread(**dict(old, milestones=[]))
    assert cleared.next_milestone == "" and cleared.milestones == []
    first.milestones[0].completed = True
    assert PlotThread(**first.model_dump()).next_milestone == ""


def test_thread_fields_survive_save_snapshot_inheritance_and_reopen(tmp_path):
    response = client.post("/project", json={"title": "脉络测试", "parent_dir": str(tmp_path),
        "style": {"tense": "past"}, "threads": [], "characters": [], "entries": []})
    assert response.status_code == 200, response.text
    project = response.json(); pid = project["id"]
    client.patch(f"/project/{pid}/settings", json={"provider": "mock"})
    thread = {"id": "t", "name": "回忆", "time_position": "past", "tone": "温柔克制",
              "user_directive": "回忆天台", "misc": "作者补充：只在最后揭示来信人", "milestones": [
                  {"id": "m1", "name": "相遇", "description": "雨后的天台", "completed": True},
                  {"id": "m2", "name": "告白", "description": "尚未发生"}]}
    saved = client.put(f"/project/{pid}/lore", json={"threads": [thread], "characters": [], "entries": []})
    assert saved.status_code == 200, saved.text
    assert saved.json()["threads"][0]["next_milestone"] == "告白"
    assert saved.json()["threads"][0]["misc"] == thread["misc"]
    planned = client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    assert planned.status_code == 200, planned.text
    assert planned.json()["lore"]["threads"][0]["milestones"][0]["id"] == "m1"
    assert planned.json()["lore"]["threads"][0]["misc"] == thread["misc"]
    assert client.post(f"/project/{pid}/chapters", json={"body_text": "第二章"}).status_code == 200
    inherited = client.post(f"/project/{pid}/chapter/2/lore/inherit?from=1")
    assert inherited.status_code == 200, inherited.text
    assert inherited.json()["lore"]["threads"][0]["tone"] == "温柔克制"
    assert inherited.json()["lore"]["threads"][0]["misc"] == thread["misc"]
    assert client.get(f"/project/{pid}").json()["style"]["tense"] == "past"
    assert client.delete(f"/project/{pid}").status_code == 200
    opened = client.post("/projects/open", json={"path": project["root_path"]})
    assert opened.status_code == 200, opened.text
    assert opened.json()["threads"][0]["time_position"] == "past"
    assert opened.json()["threads"][0]["milestones"][1]["id"] == "m2"
    assert opened.json()["threads"][0]["misc"] == thread["misc"]
    thread["misc"] = ""
    cleared = client.put(f"/project/{pid}/lore", json={"threads": [thread], "characters": [], "entries": []})
    assert cleared.status_code == 200, cleared.text
    assert get_project(pid).threads[0].misc == ""


def test_context_preserves_individual_time_positions_and_plan_semantics(tmp_path):
    response = client.post("/project", json={"title": "过去与未来", "parent_dir": str(tmp_path),
        "threads": [dict(id="a", name="回忆", time_position="past", tone="怀念", user_directive="回望旧日", misc="仅结尾揭晓来信人"),
                    dict(id="b", name="约定", time_position="future", milestones=[dict(name="未来相遇")])],
        "characters": [], "entries": [], "style": {"tense": "past"}})
    assert response.status_code == 200, response.text
    project = get_project(response.json()["id"])
    layers = build_layered_context(project, None, [], note_reference="PRIVATE_SENTINEL")
    assert "故事时间位置=past" in layers["L1"] and "故事时间位置=future" in layers["L1"]
    assert "怀念" in layers["L1"] and "回望旧日" in layers["L1"] and "未来相遇" in layers["L1"]
    assert "不是已发生事实" in layers["L1"] and "不能据此杜撰历史事实" in layers["L1"]
    assert "脉络补充说明（作者意图，不自动视为已发生事实）=仅结尾揭晓来信人" in layers["L1"]
    assert "PRIVATE_SENTINEL" not in str(layers)
