"""分区规划：作者杂项、关联资料和重复保存。全部使用隔离库与 mock provider。"""
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from services.core import db, main
from services.core.context import build_layered_context
from services.core.models import ChapterLore, ChapterPlan
from services.core.repository import get_chapter, get_project, upsert_chapter

client = TestClient(main.app)


@pytest.fixture(autouse=True)
def storage(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path / "storage")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "storage" / "t.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "storage" / "projects")
    db.init_db()


def work(parent: Path):
    response = client.post("/project", json={"title": "规划测试", "parent_dir": str(parent),
        "characters": [], "threads": [], "entries": [], "style": {}})
    assert response.status_code == 200, response.text
    pid = response.json()["id"]
    assert client.patch(f"/project/{pid}/settings", json={"provider": "mock"}).status_code == 200
    return pid


def chapter(pid, plan):
    response = client.post(f"/project/{pid}/chapters", json={"plan": plan})
    assert response.status_code == 200, response.text
    return response.json()


def test_misc_defaults_and_persists_and_clears(tmp_path):
    assert ChapterPlan().misc == ""
    pid = work(tmp_path)
    created = chapter(pid, {"misc": "只在雨夜发生"})
    assert created["plan"]["misc"] == "只在雨夜发生"
    response = client.put(f"/project/{pid}/chapter/1/plan", json={"misc": "甲\r\n乙", "free_text": "大纲"})
    assert response.status_code == 200
    assert get_chapter(pid, 1).plan.misc == "甲\n乙"
    response = client.put(f"/project/{pid}/chapter/1/plan", json={"misc": "", "free_text": "大纲"})
    assert response.status_code == 200
    assert get_chapter(pid, 1).plan.misc == ""


def test_misc_reaches_context_and_replanning_without_being_rewritten(tmp_path, monkeypatch):
    pid = work(tmp_path)
    chapter(pid, {"misc": "不能解释来信", "free_text": "雨夜来客"})
    captured = []

    def plan(*args, **kwargs):
        captured.append(args[2])
        return ChapterPlan(scene_goal="新目标", misc="模型擅自改写"), {}

    monkeypatch.setattr(main, "run_plan", plan)
    response = client.post(f"/project/{pid}/chapter/1/replan")
    assert response.status_code == 200, response.text
    assert response.json()["plan"]["misc"] == "不能解释来信"
    assert response.json()["plan"]["free_text"] == "雨夜来客"
    assert "不能解释来信" in captured[0]
    layers = build_layered_context(get_project(pid), get_chapter(pid, 1).plan, [], target_chapter=1)
    assert "本章补充说明：\n不能解释来信" in layers["extra"]


def test_only_misc_survives_automatic_planning_and_generation(tmp_path):
    pid = work(tmp_path)
    chapter(pid, {"misc": "窗外始终下雨"})
    response = client.post(f"/project/{pid}/chapter", json={"confirm_plan": True})
    assert response.status_code == 200, response.text
    assert response.json()["plan"]["misc"] == "窗外始终下雨"
    assert response.json()["body_text"]


def test_reusing_planned_chapter_preserves_misc_but_explicit_override_can_clear(tmp_path, monkeypatch):
    pid = work(tmp_path)
    chapter(pid, {"misc": "作者说明"})
    monkeypatch.setattr(main, "run_plan", lambda *args, **kwargs: (ChapterPlan(scene_goal="新目标"), {}))
    response = client.post(f"/project/{pid}/plan", json={})
    assert response.status_code == 200, response.text
    assert response.json()["plan"]["misc"] == "作者说明"
    response = client.post(f"/project/{pid}/plan", json={"overrides": {"scene_goal": "重设", "misc": ""}})
    assert response.status_code == 200, response.text
    assert response.json()["plan"]["misc"] == ""


def test_joining_master_entries_populates_chapter_lore_and_removing_keeps_records(tmp_path):
    pid = work(tmp_path)
    chapter(pid, {"scene_goal": "入城"})
    assert client.put(f"/project/{pid}/lore", json={"characters": [], "entries": [],
        "threads": [{"id": "t1", "name": "来信"}]}).status_code == 200
    assert client.post(f"/project/{pid}/characters", json={"id": "c1", "name": "林舟"}).status_code == 200
    # 模拟早于这些作品资料的章节快照。
    ch = get_chapter(pid, 1); ch.lore = ChapterLore(); upsert_chapter(pid, ch)
    joined = {"present_character_ids": ["c1"], "required_threads": ["t1"], "misc": "保留作者说明"}
    for _ in range(2):
        response = client.put(f"/project/{pid}/chapter/1/plan", json=joined)
        assert response.status_code == 200, response.text
    ch = get_chapter(pid, 1)
    assert [c.id for c in ch.lore.characters] == ["c1"]
    assert [t.id for t in ch.lore.threads] == ["t1"]
    assert client.put(f"/project/{pid}/chapter/1/plan", json={}).status_code == 200
    assert len(get_chapter(pid, 1).lore.characters) == 1
    assert len(get_chapter(pid, 1).lore.threads) == 1
    assert [c.id for c in get_project(pid).characters] == ["c1"]


def test_role_creation_retry_reuses_one_master_card_and_one_chapter_member(tmp_path):
    pid = work(tmp_path); chapter(pid, {"scene_goal": "相遇"})
    # 角色保存成功、关联请求失败后重试：再次保存同一窗口的稳定 ID。
    for _ in range(2):
        assert client.post(f"/project/{pid}/characters", json={"id": "stable-id", "name": "来客", "chapter_no": 1}).status_code == 200
        response = client.put(f"/project/{pid}/chapter/1/plan", json={"present_character_ids": ["stable-id"]})
        assert response.status_code == 200
    assert [c.id for c in get_project(pid).characters] == ["stable-id"]
    assert [c.id for c in get_chapter(pid, 1).lore.characters] == ["stable-id"]
    assert get_chapter(pid, 1).plan.present_character_ids == ["stable-id"]


@pytest.mark.parametrize("paused_endpoint", ["plan", "body"])
def test_plan_and_body_saves_preserve_each_other(tmp_path, monkeypatch, paused_endpoint):
    from concurrent.futures import ThreadPoolExecutor
    from threading import Event
    from services.core.repository import chapter_lock

    pid = work(tmp_path); chapter(pid, {"scene_goal": "原计划"})
    read_started, release_read, writer_started, writer_done = (Event() for _ in range(4))
    original = main.get_chapter

    def paused_read(project_id, no):
        result = original(project_id, no)
        read_started.set()
        assert release_read.wait(5)
        return result

    def save_other_field():
        writer_started.set()
        with chapter_lock(pid):
            latest = get_chapter(pid, 1)
            if paused_endpoint == "plan":
                latest.body_text = "正文刚刚保存的新内容"
            else:
                latest.plan = ChapterPlan(scene_goal="新计划", misc="作者说明")
            upsert_chapter(pid, latest)
        writer_done.set()

    monkeypatch.setattr(main, "get_chapter", paused_read)
    with ThreadPoolExecutor(max_workers=2) as workers:
        payload = ({"scene_goal": "新计划", "misc": "作者说明"} if paused_endpoint == "plan"
                   else {"body_text": "正文刚刚保存的新内容"})
        saving_request = workers.submit(client.put, f"/project/{pid}/chapter/1/{paused_endpoint}", json=payload)
        assert read_started.wait(5)
        saving_other = workers.submit(save_other_field)
        try:
            assert writer_started.wait(5)
            writer_done.wait(0.2)
        finally:
            release_read.set()
        assert saving_request.result(timeout=5).status_code == 200
        saving_other.result(timeout=5)
    latest = get_chapter(pid, 1)
    assert latest.body_text == "正文刚刚保存的新内容"
    assert latest.plan.scene_goal == "新计划"
    assert latest.plan.misc == "作者说明"
