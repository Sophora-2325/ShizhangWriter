"""Body revision contracts: no live provider calls."""
from copy import deepcopy
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient

from services.core import db, main, body_revisions as service, workspace as ws
from services.core.models import Chapter, ChapterPlan, ConsistencyReport, ConsistencyFlag
from services.core.repository import get_chapter, get_project, upsert_chapter

client = TestClient(main.app)


@pytest.fixture(autouse=True)
def storage(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path / "storage")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "storage/t.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "storage/projects")
    db.init_db()


def work(tmp_path, text="天还没亮，夕阳照进屋里。\n他没有说话。"):
    project = client.post("/project", json={"title": "细修测试", "parent_dir": str(tmp_path)}).json()
    pid = project["id"]
    client.patch(f"/project/{pid}/settings", json={"provider": "mock"})
    client.post(f"/project/{pid}/chapters", json={"body_text": text,
        "plan": {"scene_goal": "作者目标", "free_text": "手写规划", "misc": "保留杂项"}})
    return pid


def path(pid, extra=""):
    return f"/project/{pid}/chapter/1/body-revision" + extra


def view(pid):
    r = client.get(path(pid))
    assert r.status_code == 200, r.text
    return r.json()


def command(pid, **kwargs):
    state = view(pid)
    return {"chapter_uid": state["chapter"]["chapter_uid"], "expected_revision": state["workspace"]["revision"],
            "input_token": state["input_token"], "request_id": uuid4().hex, **kwargs}


def preview(pid, monkeypatch, patches=None):
    r = client.put(path(pid), json=command(pid, instruction="统一时间"))
    assert r.status_code == 200, r.text
    patches = patches if patches is not None else [{"before": "天还没亮，夕阳照进屋里。", "after": "天还没亮，窗外只有微光。", "reason": "统一时间"}]
    monkeypatch.setattr(service, "run_preview", lambda *a: (service.EditOutput(patches=patches), {}))
    r = client.post(path(pid, "/preview"), json=command(pid))
    assert r.status_code == 200, r.text
    return r.json()


def apply_command(pid, state):
    p = state["workspace"]["patches"][0]
    body = state["chapter"]["body_text"]
    return command(pid, preview_id=state["workspace"]["preview_id"], patch_ids=[p["id"]],
                   body_text=body[:p["start"]] + p["after"] + body[p["end"]:])


def test_preview_preserves_author_fields_and_apply_is_idempotent(tmp_path, monkeypatch):
    pid = work(tmp_path)
    original = get_chapter(pid, 1)
    state = preview(pid, monkeypatch)
    assert get_chapter(pid, 1).body_text == original.body_text
    assert get_chapter(pid, 1).plan == original.plan
    assert state["preview_valid"]
    command = apply_command(pid, state)
    r = client.post(path(pid, "/apply"), json=command)
    assert r.status_code == 200, r.text
    assert "窗外只有微光" in get_chapter(pid, 1).body_text
    assert get_chapter(pid, 1).summary_status == "stale"
    assert get_chapter(pid, 1).plan == original.plan
    assert client.post(path(pid, "/apply"), json=command).status_code == 200
    from services.core.versions import list_versions
    assert any(v["source"] == "ai" for v in list_versions(pid, 1))
    wrong = {**command, "body_text": "偷偷改写"}
    assert client.post(path(pid, "/apply"), json=wrong).status_code == 409


@pytest.mark.parametrize("change", ["body_text", "body_rich", "plan"])
def test_changed_inputs_reject_old_preview(tmp_path, monkeypatch, change):
    pid = work(tmp_path)
    state = preview(pid, monkeypatch)
    cmd = apply_command(pid, state)
    ch = get_chapter(pid, 1)
    if change == "plan": ch.plan.free_text = "新决定"
    else: setattr(ch, change, getattr(ch, change) + "新内容")
    upsert_chapter(pid, ch)
    assert not view(pid)["preview_valid"]
    r = client.post(path(pid, "/apply"), json=cmd)
    assert r.status_code == 409, r.text


def test_ordinary_stale_save_preserves_workspace_and_clear_roundtrips(tmp_path, monkeypatch):
    pid = work(tmp_path)
    old = get_chapter(pid, 1)
    preview(pid, monkeypatch)
    old.review = None
    upsert_chapter(pid, old)
    assert get_chapter(pid, 1).body_revision.preview_id
    assert client.put(path(pid), json=command(pid, instruction="", selected_issues=[])).status_code == 200
    assert view(pid)["workspace"]["instruction"] == ""
    root = ws.root_of(pid)
    assert any(x.get("body_revision", {}).get("preview_id") for x in
               (ws.read_json(p) for p in (root / ws.META_DIR).rglob("*.json") if "pending" not in p.name) if isinstance(x, dict))


def test_locate_repeated_overlapping_cross_paragraph_and_zero_change():
    chapter = Chapter(no=1, body_text="重复。重复。\n甲乙丙丁😀结束。")
    patches = service.locate(chapter, [
        {"before": "重复。", "after": "改"}, {"before": "甲乙丙", "after": "甲"},
        {"before": "乙丙丁", "after": "丁"}, {"before": "重复。\n甲", "after": "改"},
        {"before": "😀结束。", "after": "😀结束。"}, {"before": "不存在", "after": "改"}])
    assert all(p.unavailable for p in patches)
    unique = service.locate(chapter, [{"before": "😀结束。", "after": "😀收束。"}])[0]
    assert not unique.unavailable and chapter.body_text[unique.start:unique.end] == unique.before


@pytest.mark.parametrize("marker", ["«删:待确认删除»", "«插图:插图/a.png»"])
def test_saved_protected_markers_are_unavailable_in_preview(marker):
    ch = Chapter(no=1, body_text="保留这句话。", body_rich=
        '<Section xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">'
        f'<Paragraph><Run>保留这句话。{marker}</Run></Paragraph></Section>')
    assert service.locate(ch, [{"before": "保留这句话", "after": "保留这段话"}])[0].unavailable == "protected"


def test_full_body_and_only_manual_inputs_reach_model(tmp_path, monkeypatch):
    import json
    from services.core.providers.mock import MockProvider
    pid = work(tmp_path, "雨" * 9000 + "\n章末不能漏检。")
    client.put(path(pid), json=command(pid, instruction="检查章末"))
    seen = []
    def chat(self, req):
        seen.append(deepcopy(req))
        return {"content": '{"patches":[]}', "input_tokens": 1, "output_tokens": 1, "cost_usd": 0}
    monkeypatch.setattr(MockProvider, "chat", chat)
    cmd = command(pid)
    r = client.post(path(pid, "/preview"), json=cmd)
    assert r.status_code == 200, r.text
    assert len(seen) == 1 and seen[0]["purpose"] == "body_revision"
    data = json.loads(seen[0]["messages"][-1]["content"])
    assert data["完整正文"].endswith("章末不能漏检。") and len(data["完整正文"]) > 9000
    assert data["手选问题"] == []
    assert client.post(path(pid, "/preview"), json=cmd).status_code == 200
    assert len(seen) == 1


def test_old_model_result_does_not_overwrite_new_body(tmp_path, monkeypatch):
    pid = work(tmp_path)
    client.put(path(pid), json=command(pid, instruction="改时间"))
    def model(*args):
        ch = get_chapter(pid, 1); ch.body_text = "作者新正文"; upsert_chapter(pid, ch)
        return service.EditOutput(patches=[]), {}
    monkeypatch.setattr(service, "run_preview", model)
    r = client.post(path(pid, "/preview"), json=command(pid))
    assert r.status_code == 409 and get_chapter(pid, 1).body_text == "作者新正文"


def test_selected_issue_expiry_and_final_and_identity(tmp_path, monkeypatch):
    pid = work(tmp_path)
    ch = get_chapter(pid, 1)
    ch.consistency_report = ConsistencyReport(flags=[ConsistencyFlag(kind="time", point="时间矛盾", evidence="夕阳")], semantic_checked=True)
    upsert_chapter(pid, ch)
    state = view(pid); assert len(state["issues"]) == 1
    client.put(path(pid), json=command(pid, selected_issues=[state["issues"][0]["id"]]))
    ch = get_chapter(pid, 1); ch.consistency_report = None; upsert_chapter(pid, ch)
    assert client.post(path(pid, "/preview"), json=command(pid)).status_code == 409
    state = preview(pid, monkeypatch)
    cmd = apply_command(pid, state)
    from services.core.models import ChapterStatus
    ch = get_chapter(pid, 1); ch.status = ChapterStatus.final; upsert_chapter(pid, ch)
    assert client.post(path(pid, "/apply"), json=command(pid, preview_id=cmd["preview_id"], patch_ids=cmd["patch_ids"], body_text=cmd["body_text"])).status_code == 409
    assert client.put(path(pid), json={**command(pid), "chapter_uid": "other"}).status_code == 409


def test_journal_rolls_forward_apply_after_io_failure(tmp_path, monkeypatch):
    pid = work(tmp_path)
    state = preview(pid, monkeypatch); cmd = apply_command(pid, state)
    original_write = ws.write_chapter_meta
    def fail(*args, **kwargs): raise OSError("disk disconnected")
    monkeypatch.setattr(ws, "write_chapter_meta", fail)
    assert client.post(path(pid, "/apply"), json=cmd).status_code == 503
    monkeypatch.setattr(ws, "write_chapter_meta", original_write)
    from services.core.repository import recover_pending
    recover_pending(pid)
    assert "窗外只有微光" in get_chapter(pid, 1).body_text
    assert client.post(path(pid, "/apply"), json=cmd).status_code == 200
