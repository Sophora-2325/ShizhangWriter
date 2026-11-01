from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db

from services.core.db import init_db
from services.core.main import app

client = TestClient(app)


@pytest.fixture(autouse=True)
def storage(tmp_path, monkeypatch):
    """每个用例独立的库与作品目录；不依赖真实模型（mock provider）。"""
    monkeypatch.setattr(db, "STORAGE", tmp_path / "storage")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "storage" / "t.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "storage" / "projects")
    init_db()
    yield tmp_path


def _project(parent: Path, title: str, provider: str | None = "mock") -> str:
    r = client.post(
        "/project",
        json={
            "title": title,
            "parent_dir": str(parent),
            "entries": [],
            "characters": [],
            "style": {},
            "threads": [],
        },
    )
    assert r.status_code == 200, r.text
    pid = r.json()["id"]
    if provider:
        assert client.patch(f"/project/{pid}/settings", json={"provider": provider}).status_code == 200
    return pid


def _root(pid: str) -> Path:
    return Path(client.get(f"/project/{pid}").json()["root_path"])


def _note(pid: str, chapter_no: int = 1, title: str = "本章意图", body: str = "主角还在犹豫") -> dict:
    r = client.post(f"/project/{pid}/notes", json={"chapter_no": chapter_no, "title": title, "body": body})
    assert r.status_code == 200, r.text
    return r.json()


def test_note_crud_and_version_conflict(tmp_path):
    pid = _project(tmp_path, "笔记CRUD")
    note = _note(pid)
    assert len(note["id"]) == 12
    assert note["content_version"] == 1
    assert note["distill"]["status"] == "none"

    listed = client.get(f"/project/{pid}/notes")
    assert listed.status_code == 200
    assert [n["id"] for n in listed.json()["notes"]] == [note["id"]]

    bad = client.put(
        f"/project/{pid}/notes/{note['id']}",
        json={"title": "新标题", "body": "新正文", "content_version": 99},
    )
    assert bad.status_code == 409
    assert bad.json()["detail"]["code"] == "NOTE_VERSION"
    assert client.get(f"/project/{pid}/notes").json()["notes"][0]["body"] == "主角还在犹豫"

    ok = client.put(
        f"/project/{pid}/notes/{note['id']}",
        json={"title": "新标题", "body": "新正文", "content_version": 1},
    )
    assert ok.status_code == 200, ok.text
    assert ok.json()["content_version"] == 2
    assert ok.json()["body"] == "新正文"


def test_notes_never_call_a_model_or_return_legacy_references(tmp_path, monkeypatch):
    import services.core.notes as notes
    pid = _project(tmp_path, "个人笔记")
    note = _note(pid)
    path = _root(pid) / ".novel-gm" / "notes.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    data["notes"][0]["distill"]["reference"] = "PRIVATE_SENTINEL"
    path.write_text(json.dumps(data), encoding="utf-8")

    def forbidden(*args, **kwargs):
        raise AssertionError("个人笔记不得调用模型")

    monkeypatch.setattr(notes, "distill_note", forbidden)
    assert client.post(f"/project/{pid}/notes/{note['id']}/distill", json={"content_version": 1}).status_code == 410
    assert client.put(f"/project/{pid}/notes/{note['id']}/reference", json={"reference": "秘密"}).status_code == 410
    reference = client.get(f"/project/{pid}/notes/reference", params={"chapter_no": 1}).json()
    assert reference["reference"] == "" and reference["notes"] == []
    from services.core.main import _note_reference
    assert _note_reference(pid, 1) == ""
    assert client.get(f"/project/{pid}/notes").json()["notes"][0]["distill"]["reference"] == "PRIVATE_SENTINEL"


def test_context_does_not_accept_note_reference(tmp_path):
    from services.core.context import build_layered_context
    from services.core.repository import get_project
    pid = _project(tmp_path, "上下文隔离")
    context = build_layered_context(get_project(pid), None, [], note_reference="PRIVATE_SENTINEL")
    assert "PRIVATE_SENTINEL" not in str(context)


def test_category_create_rename_move_and_reload(tmp_path):
    pid = _project(tmp_path, "分类")
    folders = client.get(f"/project/{pid}/notes/folders").json()["folders"]
    assert [f["id"] for f in folders] == ["default", "foreshadow", "memo"]
    created = client.post(f"/project/{pid}/notes/folders", json={"title": "资料"})
    assert created.status_code == 200
    folder = created.json()["id"]
    assert client.put(f"/project/{pid}/notes/folders/{folder}", json={"title": "摘录"}).status_code == 200
    note = client.post(f"/project/{pid}/notes", json={"folder": folder, "body": "个人资料"}).json()
    assert note["chapter_no"] == 0
    moved = client.put(f"/project/{pid}/notes/{note['id']}", json={"body": "个人资料", "folder": "memo", "content_version": 1})
    assert moved.status_code == 200
    assert moved.json()["folder"] == "memo" and moved.json()["content_version"] == 2
    assert client.get(f"/project/{pid}/notes").json()["notes"][0]["folder"] == "memo"
    assert any(f["title"] == "摘录" for f in client.get(f"/project/{pid}/notes/folders").json()["folders"])
    invalid = client.post(f"/project/{pid}/notes", json={"folder": "does-not-exist"})
    assert invalid.status_code == 400


def test_corrupt_notes_file_is_treated_as_empty(tmp_path):
    pid = _project(tmp_path, "坏文件")
    path = _root(pid) / ".novel-gm" / "notes.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("{ not json", encoding="utf-8")

    listed = client.get(f"/project/{pid}/notes")
    assert listed.status_code == 200 and listed.json()["notes"] == []
    ref = client.get(f"/project/{pid}/notes/reference", params={"chapter_no": 1})
    assert ref.status_code == 200
    assert ref.json()["status"] == "none" and ref.json()["reference"] == ""

    # 损坏文件不阻止新建：读成空列表后正常原子写回
    note = _note(pid)
    data = json.loads(path.read_text(encoding="utf-8"))
    assert data["version"] == 1 and data["notes"][0]["id"] == note["id"]


def test_notes_survive_chapter_delete_and_insert(tmp_path):
    pid = _project(tmp_path, "笔记平移")
    assert client.post(f"/project/{pid}/chapters", json={"body_text": "第一章"}).status_code == 200
    assert client.post(f"/project/{pid}/chapters", json={"body_text": "第二章"}).status_code == 200
    n1 = _note(pid, 1, "第一章笔记")
    n2 = _note(pid, 2, "第二章笔记")

    assert client.delete(f"/project/{pid}/chapter/1").status_code == 200
    remaining = client.get(f"/project/{pid}/notes").json()["notes"]
    assert {n["id"] for n in remaining} == {n1["id"], n2["id"]}
    assert client.post(f"/project/{pid}/chapter/1/insert-after").status_code == 200
    after = client.get(f"/project/{pid}/notes").json()["notes"]
    assert {n["id"] for n in after} == {n1["id"], n2["id"]}


def test_notes_require_work_folder(tmp_path):
    pid = _project(tmp_path, "文件夹丢失")
    root = _root(pid)
    import shutil

    shutil.rmtree(root)
    r = client.get(f"/project/{pid}/notes")
    assert r.status_code == 409
    assert r.json()["detail"]["code"] == "ROOT_MISSING"


def test_manual_order_pin_and_content_save_are_independent(tmp_path):
    pid = _project(tmp_path, "手动排序")
    first, second, third = [_note(pid, title=t) for t in ("甲", "乙", "丙")]
    url = f"/project/{pid}/notes"
    ids = lambda: [n["id"] for n in client.get(url).json()["notes"]]
    assert ids() == [third["id"], second["id"], first["id"]]
    moved = client.post(f"{url}/{first['id']}/move", json={"direction": "up"})
    assert moved.status_code == 200
    assert ids() == [third["id"], first["id"], second["id"]]
    assert client.patch(f"{url}/{second['id']}/pin", json={"pinned": True}).status_code == 200
    assert ids() == [second["id"], third["id"], first["id"]]
    # A pinned note cannot cross its group boundary.
    client.post(f"{url}/{second['id']}/move", json={"direction": "down"})
    assert ids() == [second["id"], third["id"], first["id"]]
    edited = client.put(f"{url}/{first['id']}", json={"title": "改名", "body": "新正文", "content_version": 1})
    assert edited.status_code == 200
    assert ids() == [second["id"], third["id"], first["id"]]
    pinned = next(n for n in client.get(url).json()["notes"] if n["id"] == second["id"])
    assert pinned["content_version"] == 1 and pinned["updated_at"] == second["updated_at"]
    client.patch(f"{url}/{second['id']}/pin", json={"pinned": False})
    assert ids()[0] == second["id"]
    assert client.post(f"{url}/{first['id']}/move", json={"direction": "left"}).status_code == 400
    # File-backed ordering survives forgetting and reopening the project.
    root = str(_root(pid))
    before = ids()
    assert client.delete(f"/project/{pid}").status_code == 200
    assert client.post("/projects/open", json={"path": root}).status_code == 200
    assert ids() == before


def test_legacy_order_is_stable_and_read_does_not_rewrite(tmp_path):
    pid = _project(tmp_path, "旧笔记排序")
    first, second = _note(pid, title="甲"), _note(pid, title="乙")
    path = _root(pid) / ".novel-gm" / "notes.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    for i, note in enumerate(data["notes"]):
        note.pop("sort_order"); note.pop("pinned")
        note["updated_at"] = f"2026-10-{18 + i}T00:00:00Z"
    path.write_text(json.dumps(data), encoding="utf-8")
    before = path.read_bytes()
    notes = client.get(f"/project/{pid}/notes").json()["notes"]
    assert [n["id"] for n in notes] == [second["id"], first["id"]]
    assert path.read_bytes() == before
    client.put(f"/project/{pid}/notes/{first['id']}", json={"title": "新名", "body": first["body"], "content_version": 1})
    assert [n["id"] for n in client.get(f"/project/{pid}/notes").json()["notes"]] == [second["id"], first["id"]]


def test_duplicate_is_independent_and_drops_legacy_distillation(tmp_path):
    pid = _project(tmp_path, "副本")
    source = _note(pid, title="原稿", body="原文")
    url = f"/project/{pid}/notes"
    client.patch(f"{url}/{source['id']}/pin", json={"pinned": True})
    path = _root(pid) / ".novel-gm" / "notes.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    data["notes"][0]["distill"]["reference"] = "旧提炼"
    path.write_text(json.dumps(data), encoding="utf-8")
    copy = client.post(f"{url}/{source['id']}/copy", json={}).json()
    assert copy["id"] != source["id"] and copy["title"] == "原稿 副本"
    assert not copy["pinned"] and copy["body"] == "原文" and copy["content_version"] == 1
    assert copy["distill"]["reference"] == "" and copy["chapter_no"] == 0
    client.put(f"{url}/{copy['id']}", json={"title": copy["title"], "body": "副本正文", "content_version": 1})
    original = next(n for n in client.get(url).json()["notes"] if n["id"] == source["id"])
    assert original["body"] == "原文" and original["distill"]["reference"] == "旧提炼"


def test_failed_order_write_preserves_file(tmp_path, monkeypatch):
    from services.core import workspace
    pid = _project(tmp_path, "排序失败")
    note = _note(pid)
    path = _root(pid) / ".novel-gm" / "notes.json"
    before = path.read_bytes()
    def fail(*args, **kwargs):
        raise OSError("disk full")
    monkeypatch.setattr(workspace, "write_json", fail)
    with pytest.raises(OSError, match="disk full"):
        client.patch(f"/project/{pid}/notes/{note['id']}/pin", json={"pinned": True})
    assert path.read_bytes() == before


def test_move_retry_with_same_operation_id_moves_only_once(tmp_path):
    pid = _project(tmp_path, "移动重试")
    first, second, third = [_note(pid, title=t) for t in ("甲", "乙", "丙")]
    url = f"/project/{pid}/notes"
    request = {"direction": "up", "operation_id": "move-response-lost"}
    for _ in range(2):
        response = client.post(f"{url}/{first['id']}/move", json=request)
        assert response.status_code == 200, response.text
    assert [n["id"] for n in response.json()["notes"]] == [third["id"], first["id"], second["id"]]


def test_copy_retry_returns_same_note_after_other_writes_and_reopen(tmp_path):
    pid = _project(tmp_path, "复制重试")
    source = _note(pid, title="原稿")
    url = f"/project/{pid}/notes"
    request = {"title": "原稿 副本", "operation_id": "copy-response-lost"}
    first = client.post(f"{url}/{source['id']}/copy", json=request).json()
    # A separate content update must preserve the operation receipt.
    assert client.put(f"{url}/{source['id']}", json={"title": "已改名", "body": "新正文", "content_version": 1}).status_code == 200
    root = str(_root(pid))
    assert client.delete(f"/project/{pid}").status_code == 200
    assert client.post("/projects/open", json={"path": root}).status_code == 200
    retry = client.post(f"{url}/{source['id']}/copy", json=request)
    assert retry.status_code == 200, retry.text
    assert retry.json()["id"] == first["id"]
    assert retry.json()["body"] == first["body"]
    assert len(client.get(url).json()["notes"]) == 2


def test_operation_id_cannot_be_reused_for_another_action(tmp_path):
    pid = _project(tmp_path, "操作标识冲突")
    note = _note(pid)
    url = f"/project/{pid}/notes/{note['id']}"
    assert client.post(f"{url}/copy", json={"operation_id": "same-operation"}).status_code == 200
    conflict = client.post(f"{url}/move", json={"direction": "up", "operation_id": "same-operation"})
    assert conflict.status_code == 409
    assert conflict.json()["detail"]["code"] == "NOTE_OPERATION"
