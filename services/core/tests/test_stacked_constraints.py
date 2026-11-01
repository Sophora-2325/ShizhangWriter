"""Stacked author intent: interval semantics, persistence, model context and concurrent writes."""
from pathlib import Path
from types import SimpleNamespace
import json

import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError

from services.core import db, main
from services.core.audit import run_plan_check
from services.core.context import build_layered_context
from services.core.models import Chapter, ChapterPlan, ConsistencyReport, StackedConstraints
from services.core.repository import context_revision, get_chapter, get_project, upsert_chapter
from services.core.stacked_constraints import compile_constraints, intervals, semantic_data

client = TestClient(main.app)


@pytest.fixture(autouse=True)
def storage(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path / "storage")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "storage" / "test.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "storage" / "projects")
    db.init_db()


def track(eid="c1", start=10, end=30, **kwargs):
    return {"id": "track-" + eid, "domain": "character", "element_id": eid,
            "name": eid, "segments": [{"id": "segment-" + eid, "start": start, "end": end,
                                         "directive": "保守来信的秘密"}], **kwargs}


def work(tmp_path):
    r = client.post("/project", json={"title": "叠条测试", "parent_dir": str(tmp_path),
        "characters": [{"id": "c1", "name": "林舟"}], "threads": [], "entries": [], "style": {}})
    assert r.status_code == 200, r.text
    pid = r.json()["id"]
    client.patch(f"/project/{pid}/settings", json={"provider": "mock"})
    r = client.post(f"/project/{pid}/chapters", json={"plan": {"scene_goal": "来信"}})
    assert r.status_code == 200, r.text
    return pid


def save(pid, tracks):
    return client.put(f"/project/{pid}/chapter/1/stacked-constraints", json={"tracks": tracks})


def test_intervals_do_not_relate_touching_edges_or_propagate_relations():
    value = StackedConstraints(tracks=[track("a", 0, 30), track("b", 20, 50), track("c", 40, 60)])
    slices = [(a, b, {t.element_id for t, _ in active}) for a, b, active in intervals(value)]
    assert (20, 30, {"a", "b"}) in slices
    assert (40, 50, {"b", "c"}) in slices
    assert not any({"a", "c"} <= ids for _, _, ids in slices)
    touching = StackedConstraints(tracks=[track("a", 0, 20), track("b", 20, 100)])
    assert all(len(active) == 1 for _, _, active in intervals(touching))


@pytest.mark.parametrize("change", [{"weight": 0}, {"weight": 0.05}, {"weight": -1}, {"weight": 1.1}, {"weight": float("nan")},
    {"segments": [{"id": "x", "start": 40, "end": 20}]},
    {"segments": [{"id": "x", "start": 0, "end": 101}]}])
def test_validation_rejects_invalid_geometry_and_weights(change):
    with pytest.raises(ValidationError):
        StackedConstraints(tracks=[track(**change)])


@pytest.mark.parametrize("weight", [0.1, 0.5, 1])
def test_multiple_segments_weight_boundaries_and_layout_only_changes(weight):
    t = track(weight=weight)
    t["segments"].append({"id": "late", "start": 80, "end": 100, "directive": ""})
    value = StackedConstraints(tracks=[t])
    moved = value.model_copy(deep=True)
    moved.tracks[0].segments[0].top = 35
    assert semantic_data(value) == semantic_data(moved)
    prompt = compile_constraints(value)
    assert f"权重={weight:g}" in prompt and "80%–100%" in prompt
    assert "出现并按作者安排推进" in prompt


def test_roundtrip_clear_metadata_associations_and_no_inheritance(tmp_path):
    pid = work(tmp_path)
    assert get_chapter(pid, 1).stacked_constraints.tracks == []
    r = save(pid, [track()]); assert r.status_code == 200, r.text
    assert r.json()["stacked_constraints"]["tracks"][0]["name"] == "林舟"
    assert r.json()["plan"]["present_character_ids"] == ["c1"]
    r = client.put(f"/project/{pid}/chapter/1/plan", json={"scene_goal": "不能丢掉关联"})
    assert r.status_code == 409
    # Workspace metadata is the portable source of truth (directory name is defined centrally).
    from services.core import workspace as ws
    assert '"stacked_constraints"' in ws.meta_path(Path(get_project(pid).root_path), 1).read_text("utf-8")
    db.init_db()  # repeatable migration / reopen
    assert get_chapter(pid, 1).stacked_constraints.tracks[0].segments[0].end == 30
    r = client.post(f"/project/{pid}/chapters", json={})
    assert r.json()["stacked_constraints"] == {"tracks": []}
    assert save(pid, []).status_code == 200
    assert get_chapter(pid, 1).stacked_constraints.tracks == []
    assert get_chapter(pid, 1).plan.present_character_ids == ["c1"]


def test_replan_reuse_generation_and_target_only_context(tmp_path, monkeypatch):
    pid = work(tmp_path); save(pid, [track()])
    captured = []
    def plan(view, chapters, *args, **kwargs):
        captured.append(build_layered_context(view, None, chapters, target_chapter=kwargs["target_chapter"])["extra"])
        return ChapterPlan(scene_goal="改写大纲"), {}
    monkeypatch.setattr(main, "run_plan", plan)
    r = client.post(f"/project/{pid}/chapter/1/replan")
    assert r.status_code == 200, r.text
    assert r.json()["plan"]["present_character_ids"] == ["c1"]
    r = client.post(f"/project/{pid}/plan", json={})
    assert r.status_code == 200, r.text
    assert r.json()["stacked_constraints"]["tracks"]
    assert all("保守来信的秘密" in text for text in captured)
    def generate(view, plan, chapters, **kwargs):
        text = build_layered_context(view, plan, chapters, target_chapter=1)["extra"]
        assert "保守来信的秘密" in text
        return "林舟将信藏进衣内。", {}
    monkeypatch.setattr(main, "run_generate", generate)
    r = client.post(f"/project/{pid}/chapter", json={})
    assert r.status_code == 200, r.text
    assert r.json()["stacked_constraints"]["tracks"]
    assert "叠条" not in build_layered_context(get_project(pid), None, [get_chapter(pid, 1)], target_chapter=2)["extra"]


def test_semantic_edits_invalidate_check_layout_does_not(tmp_path):
    pid = work(tmp_path); save(pid, [track()])
    ch = get_chapter(pid, 1); ch.plan_check = ConsistencyReport(passed=True)
    upsert_chapter(pid, ch)
    original = context_revision(pid, 1)
    t = track(); t["segments"][0]["top"] = 25
    save(pid, [t])
    assert context_revision(pid, 1) == original
    assert get_chapter(pid, 1).plan_check is not None
    t["weight"] = 1
    save(pid, [t])
    assert context_revision(pid, 1) != original
    assert get_chapter(pid, 1).plan_check is None


def test_changed_constraints_reject_late_ai_result(tmp_path, monkeypatch):
    pid = work(tmp_path); save(pid, [track()])
    def generate(*args, **kwargs):
        assert save(pid, [track(weight=0.8)]).status_code == 200
        return "旧约束生成的正文", {}
    monkeypatch.setattr(main, "run_generate", generate)
    r = client.post(f"/project/{pid}/chapter", json={})
    assert r.status_code == 409, r.text
    assert get_chapter(pid, 1).body_text == ""
    assert get_chapter(pid, 1).stacked_constraints.tracks[0].weight == 0.8


def test_layout_changed_during_ai_is_preserved_without_rejecting_result(tmp_path, monkeypatch):
    pid = work(tmp_path); save(pid, [track()])
    def generate(*args, **kwargs):
        t = track(); t["segments"][0]["top"] = 35
        assert save(pid, [t]).status_code == 200
        return "保留新布局的正文", {}
    monkeypatch.setattr(main, "run_generate", generate)
    r = client.post(f"/project/{pid}/chapter", json={})
    assert r.status_code == 200, r.text
    assert get_chapter(pid, 1).stacked_constraints.tracks[0].segments[0].top == 35


@pytest.mark.parametrize("field", ["body", "plan"])
def test_save_merges_latest_body_and_plan_under_chapter_lock(tmp_path, monkeypatch, field):
    from concurrent.futures import ThreadPoolExecutor
    from threading import Event
    from services.core.repository import chapter_lock
    pid = work(tmp_path)
    paused, release, other_started = Event(), Event(), Event()
    original = main.get_chapter
    def pause_read(project_id, no):
        result = original(project_id, no); paused.set(); assert release.wait(5); return result
    def write_other():
        other_started.set()
        with chapter_lock(pid):
            latest = get_chapter(pid, 1)
            if field == "body": latest.body_text = "同时写下的正文"
            else: latest.plan.misc = "同时写下的规划补充"
            upsert_chapter(pid, latest)
    monkeypatch.setattr(main, "get_chapter", pause_read)
    with ThreadPoolExecutor(max_workers=2) as pool:
        saving = pool.submit(save, pid, [track()]); assert paused.wait(5)
        writer = pool.submit(write_other); assert other_started.wait(5); release.set()
        assert saving.result(timeout=5).status_code == 200; writer.result(timeout=5)
    latest = get_chapter(pid, 1)
    assert latest.stacked_constraints.tracks
    assert (latest.body_text if field == "body" else latest.plan.misc) == ("同时写下的正文" if field == "body" else "同时写下的规划补充")


def test_check_reads_chapter_end_and_reports_unchecked(tmp_path, monkeypatch):
    from services.core import audit
    pid = work(tmp_path); save(pid, [track(start=80, end=100)])
    ch = get_chapter(pid, 1); ch.body_text = "雨" * 8500 + "章末来信仍未拆封"
    messages = []
    provider = SimpleNamespace(chat=lambda req: (messages.extend(req["messages"]) or {"content": '{"flags":[],"passed":true}'}), name="mock")
    monkeypatch.setattr(audit, "resolve_for_project", lambda _: (provider, "mock"))
    rep = run_plan_check(get_project(pid), ch, [], semantic=True)
    assert rep.semantic_checked
    assert "章末来信仍未拆封" in messages[-1]["content"]
    assert "80%–100%" in messages[-1]["content"]
    rep = run_plan_check(get_project(pid), ch, [], semantic=False)
    assert not rep.semantic_checked
    assert any("尚未" in f.point for f in rep.flags)


def test_real_planning_and_generation_requests_include_stacked_author_intent(tmp_path, monkeypatch):
    from services.core import generation
    pid = work(tmp_path); save(pid, [track()])
    requests = []
    def chat(request):
        requests.append(request)
        return {"content": '{"scene_goal":"收到来信","present_character_ids":["c1"]}'
                if request["purpose"] == "plan" else "林舟把信收入衣内。"}
    provider = SimpleNamespace(chat=chat, name="mock")
    monkeypatch.setattr(generation, "resolve_for_project", lambda _: (provider, "mock"))
    r = client.post(f"/project/{pid}/chapter/1/replan")
    assert r.status_code == 200, r.text
    r = client.post(f"/project/{pid}/chapter", json={"prefix": "这是已有的开头。"})
    assert r.status_code == 200, r.text
    assert r.json()["body_text"].startswith("这是已有的开头。")
    assert [r["purpose"] for r in requests] == ["plan", "generate"]
    for request in requests:
        content = "\n".join(message["content"] for message in request["messages"])
        assert "保守来信的秘密" in content
        assert "10%–30%" in content
        assert "权重仅决定同领域" in content
        assert content.index("一、节奏骨架") < content.index("二、具体内容") < content.index("三、写作要求")
        assert "优先于普通大纲" not in content


@pytest.mark.parametrize("weight", [0, 0.05])
def test_legacy_weights_migrate_database_and_metadata_and_notice_once(tmp_path, weight):
    from services.core import workspace as ws
    from services.core.repository import import_folder
    pid = work(tmp_path); save(pid, [track()])
    root = Path(get_project(pid).root_path)
    old = {"tracks": [track(weight=weight)]}
    with db.connect() as conn:
        conn.execute("UPDATE chapters SET stacked_constraints_json=? WHERE project_id=? AND no=1", (json.dumps(old), pid))
    migrated = get_chapter(pid, 1)
    assert migrated.stacked_constraints.tracks[0].weight == 0.1
    assert migrated.stacked_constraints.tracks[0].segments[0].directive == "保守来信的秘密"
    assert migrated.stacked_weight_migration_pending
    assert json.loads(ws.meta_path(root, 1).read_text("utf-8"))["stacked_constraints"]["tracks"][0]["weight"] == 0.1
    with db.connect() as conn:
        assert json.loads(conn.execute("SELECT stacked_constraints_json FROM chapters WHERE project_id=?", (pid,)).fetchone()[0])["tracks"][0]["weight"] == 0.1
    assert client.post(f"/project/{pid}/chapter/1/stacked-constraints/migration/ack").status_code == 200
    import_folder(str(root))  # reopen portable metadata, not merely the in-memory model
    assert not get_chapter(pid, 1).stacked_weight_migration_pending
    with db.connect() as conn:
        assert conn.execute("SELECT COUNT(*) FROM events WHERE project_id=? AND event_type='StackedWeightsMigrated'", (pid,)).fetchone()[0] == 1
    assert save(pid, [track(weight=weight)]).status_code == 422  # new input is never normalized


def test_legacy_metadata_and_pending_save_recovery_migrate(tmp_path):
    from services.core import workspace as ws
    from services.core.repository import import_folder, recover_pending
    pid = work(tmp_path); save(pid, [track()]); root = Path(get_project(pid).root_path)
    path = ws.meta_path(root, 1)
    meta = json.loads(path.read_text("utf-8")); meta["stacked_constraints"]["tracks"][0]["weight"] = 0.05
    path.write_text(json.dumps(meta, ensure_ascii=False), "utf-8")
    import_folder(str(root))
    assert get_chapter(pid, 1).stacked_constraints.tracks[0].weight == 0.1
    assert json.loads(path.read_text("utf-8"))["stacked_weight_migration_pending"]
    ch = get_chapter(pid, 1).model_dump(mode="json"); ch["stacked_constraints"]["tracks"][0]["weight"] = 0
    with db.connect() as conn:
        filename = conn.execute("SELECT body_path FROM chapters WHERE project_id=? AND no=1", (pid,)).fetchone()[0]
    ws.write_json(ws.meta_root(root) / "pending-save.json", {"changes": [
        {"chapter": ch, "file": filename, "old": filename, "write_body": False}], "facts": None})
    recover_pending(pid)
    assert get_chapter(pid, 1).stacked_constraints.tracks[0].weight == 0.1
    assert not (ws.meta_root(root) / "pending-save.json").exists()
    with db.connect() as conn:
        assert conn.execute("SELECT COUNT(*) FROM events WHERE project_id=? AND event_type='StackedWeightsMigrated'", (pid,)).fetchone()[0] == 2


def alignment_provider(monkeypatch, response):
    from services.core import rhythm_alignment as alignment
    alignment._cache.clear()
    requests = []
    def chat(request):
        requests.append(request)
        value = response(request) if callable(response) else response
        return {"content": json.dumps(value, ensure_ascii=False) if isinstance(value, dict) else value}
    monkeypatch.setattr(alignment, "resolve_for_project", lambda _: (SimpleNamespace(chat=chat, name="mock"), "mock"))
    return requests


def mismatch():
    return {"status": "mismatch", "issues": [{"kind": "order", "plan_field": "free_text",
        "plan_quote": "先争吵再来信", "track_id": "track-c1", "segment_id": "segment-c1",
        "reason": "规划先争吵再来信，片段明确先来信再争吵，顺序相反"}]}


def conflicted_work(tmp_path):
    pid = work(tmp_path)
    t = track(); t["segments"][0]["directive"] = "先来信再争吵"
    save(pid, [t]); ch = get_chapter(pid, 1)
    ch.plan.free_text = "先争吵再来信"; ch.body_text = "已有正文应保留。"
    upsert_chapter(pid, ch)
    return pid


@pytest.mark.parametrize("endpoint", ["chapter", "chapter/1/replan", "plan"])
def test_explicit_mismatch_blocks_before_planning_or_generation_without_overwriting(tmp_path, monkeypatch, endpoint):
    pid = conflicted_work(tmp_path); before = get_chapter(pid, 1).model_dump()
    requests = alignment_provider(monkeypatch, mismatch())
    def forbidden(*args, **kwargs):
        raise AssertionError("creation must not run after a detected mismatch")
    monkeypatch.setattr(main, "run_plan", forbidden); monkeypatch.setattr(main, "run_generate", forbidden)
    if endpoint == "plan":  # reuse empty planned chapter; other paths also cover continuation
        ch = get_chapter(pid, 1); ch.body_text = ""; upsert_chapter(pid, ch); before = get_chapter(pid, 1).model_dump()
    for _ in range(2):
        r = client.post(f"/project/{pid}/{endpoint}", json={})
        assert r.status_code == 409, r.text
        detail = r.json()["detail"]
        assert detail["code"] == "PLAN_RHYTHM_MISMATCH"
        issue = detail["issues"][0]
        assert issue["plan_quote"] == "先争吵再来信"
        assert issue["element_id"] == "c1" and issue["start"] == 10 and issue["end"] == 30
    assert len(requests) == 1  # same author semantics reuse the result
    after = get_chapter(pid, 1).model_dump()
    assert after["body_text"] == before["body_text"] and after["plan"] == before["plan"]
    assert after["stacked_constraints"] == before["stacked_constraints"]


def test_alignment_cache_ignores_vertical_layout_and_rechecks_semantic_edits(tmp_path, monkeypatch):
    from services.core.rhythm_alignment import check_alignment
    pid = work(tmp_path); save(pid, [track()])
    requests = alignment_provider(monkeypatch, {"status": "aligned", "issues": []})
    def check():
        assert check_alignment(get_project(pid), get_chapter(pid, 1)) == []
    check(); check(); assert len(requests) == 1
    t = track(); t["segments"][0]["top"] = 35; save(pid, [t]); check(); assert len(requests) == 1
    for update in (lambda c: setattr(c.plan, "misc", "用动作呈现犹豫"),
                   lambda c: setattr(c.stacked_constraints.tracks[0], "weight", 0.1),
                   lambda c: setattr(c.stacked_constraints.tracks[0].segments[0], "start", 15),
                   lambda c: setattr(c.stacked_constraints.tracks[0].segments[0], "directive", "暂不拆信")):
        ch = get_chapter(pid, 1); update(ch); upsert_chapter(pid, ch); check()
    assert len(requests) == 5
    prompt = requests[0]["messages"][-1]["content"]
    assert "未写明先后" in prompt and "正常过渡" in prompt and "不算矛盾" in prompt
    assert requests[0]["purpose"] == "rhythm_alignment"
    assert requests[0]["model"] == "mock"


@pytest.mark.parametrize("response", ["broken json", {"status": "mismatch", "issues": []},
    {"status": "mismatch", "issues": [{**mismatch()["issues"][0], "plan_quote": "编造的规划"}]}])
def test_failed_or_ungrounded_check_reports_retry_and_does_not_cache(tmp_path, monkeypatch, response):
    pid = conflicted_work(tmp_path)
    requests = alignment_provider(monkeypatch, response)
    for _ in range(2):
        r = client.post(f"/project/{pid}/chapter", json={})
        assert r.status_code == 503 and r.json()["detail"]["code"] == "PLAN_RHYTHM_CHECK_FAILED"
    assert len(requests) == 2 and get_chapter(pid, 1).body_text == "已有正文应保留。"


def test_aligned_preflight_allows_continuation_and_changes_during_check_reject_old_context(tmp_path, monkeypatch):
    pid = work(tmp_path); save(pid, [track()])
    requests = alignment_provider(monkeypatch, {"status": "aligned", "issues": []})
    monkeypatch.setattr(main, "run_generate", lambda *args, **kwargs: ("已通过检查的正文", {}))
    assert client.post(f"/project/{pid}/chapter", json={}).status_code == 200
    assert client.post(f"/project/{pid}/chapter", json={}).status_code == 200
    assert len(requests) == 1
    def changed(_):
        save(pid, [track(weight=0.8)])
        return {"status": "aligned", "issues": []}
    alignment_provider(monkeypatch, changed)
    r = client.post(f"/project/{pid}/chapter", json={})
    assert r.status_code == 409 and r.json()["detail"]["code"] == "BODY_CHANGED"
    assert get_chapter(pid, 1).body_text == "已通过检查的正文"
