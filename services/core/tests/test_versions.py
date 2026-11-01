from __future__ import annotations

import hashlib
import json
import os
import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db
from services.core import versions as versions_module

from services.core.db import init_db
from services.core.main import app

client = TestClient(app)


@pytest.fixture(autouse=True)
def storage(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path / "storage")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "storage" / "t.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "storage" / "projects")
    init_db()
    yield tmp_path


def _project(parent: Path, title: str) -> str:
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
    assert client.patch(f"/project/{pid}/settings", json={"provider": "mock"}).status_code == 200
    return pid


def _root(pid: str) -> Path:
    return Path(client.get(f"/project/{pid}").json()["root_path"])


def _chapter(pid: str, text: str = "原稿正文") -> dict:
    r = client.post(f"/project/{pid}/chapters", json={"body_text": text})
    assert r.status_code == 200, r.text
    return r.json()


def _versions(pid: str, no: int = 1) -> list[dict]:
    r = client.get(f"/project/{pid}/chapter/{no}/versions")
    assert r.status_code == 200, r.text
    return r.json()["versions"]


def _new_versions(pid: str, no: int = 1) -> list[dict]:
    """排除旧版纯文本备份（workspace 旧 history 机制也会为每次保存留一份）。"""
    return [v for v in _versions(pid, no) if v["source"] != "legacy-text"]


def _version(pid: str, vid: str, no: int = 1) -> dict:
    r = client.get(f"/project/{pid}/chapter/{no}/versions/{vid}")
    assert r.status_code == 200, r.text
    return r.json()


def _save_body(pid: str, text: str, rich: str = "", no: int = 1) -> dict:
    r = client.put(f"/project/{pid}/chapter/{no}/body", json={"body_text": text, "body_rich": rich})
    assert r.status_code == 200, r.text
    return r.json()


RICH = '<Section xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Paragraph><Run>雨</Run></Paragraph></Section>'


def test_original_autosave_and_restore_keeps_backup(tmp_path):
    pid = _project(tmp_path, "版本恢复")
    _chapter(pid, "原稿正文")

    _save_body(pid, "改稿一", RICH)
    listed = _new_versions(pid)
    assert len(listed) == 1
    assert listed[0]["source"] == "original"
    assert listed[0]["format_available"] is True
    original_id = listed[0]["id"]
    original_detail = _version(pid, original_id)
    assert original_detail["body_text"] == "原稿正文"
    assert original_detail["body_rich"] == ""

    # 第二次改动：latest 是 original → 记一版 autosave（内容是上一次保存的正文+格式）
    _save_body(pid, "改稿二", "")
    listed = _new_versions(pid)
    assert {v["source"] for v in listed} == {"original", "autosave"}
    autosave = next(v for v in listed if v["source"] == "autosave")
    assert _version(pid, autosave["id"])["body_text"] == "改稿一"

    # 同内容保存不产生新版本
    before = len(listed)
    _save_body(pid, "改稿二", "")
    assert len(_new_versions(pid)) == before

    # 恢复 original：先备份当前稿，再写回版本正文
    r = client.post(f"/project/{pid}/chapter/1/versions/{original_id}/restore")
    assert r.status_code == 200, r.text
    assert r.json()["body_text"] == "原稿正文"
    sources = {v["source"] for v in _new_versions(pid)}
    assert "restore-backup" in sources
    backup = next(v for v in _new_versions(pid) if v["source"] == "restore-backup")
    assert _version(pid, backup["id"])["body_text"] == "改稿二"


def test_restore_invalidates_derived_results(tmp_path):
    pid = _project(tmp_path, "恢复失效")
    _chapter(pid, "正文甲")
    fin = client.post(f"/project/{pid}/chapter/1/finalize")
    assert fin.status_code == 200, fin.text
    assert fin.json()["summary"] is not None

    assert versions_module.checkpoint(pid, 1, "正文乙", "", "manual") is not None
    vid = next(v["id"] for v in _new_versions(pid) if v["source"] == "manual")
    r = client.post(f"/project/{pid}/chapter/1/versions/{vid}/restore")
    assert r.status_code == 200, r.text
    restored = client.get(f"/project/{pid}/chapter/1").json()
    assert restored["body_text"] == "正文乙"
    # upsert_chapter 的 freshness 逻辑应让旧摘要失效
    assert restored["summary"] is None
    assert restored["summary_status"] == "stale"


def test_restore_superseded_chapter_conflicts(tmp_path):
    pid = _project(tmp_path, "作废恢复")
    _chapter(pid, "第一章正文")
    _chapter(pid, "第二章正文")
    versions_module.checkpoint(pid, 2, "旧第二章", "", "manual")
    vid = _versions(pid, 2)[0]["id"]
    assert _version(pid, vid, 2)["body_text"] == "旧第二章"

    assert client.post(f"/project/{pid}/rollback/1").status_code == 200
    r = client.post(f"/project/{pid}/chapter/2/versions/{vid}/restore")
    assert r.status_code == 409
    assert r.json()["detail"]["code"] == "SUPERSEDED"
    # 查看不受影响
    assert _version(pid, vid, 2)["body_text"] == "旧第二章"


def test_versions_trim_to_latest_100(tmp_path):
    pid = _project(tmp_path, "版本裁剪")
    _chapter(pid, "起始正文")
    for index in range(105):
        assert versions_module.checkpoint(pid, 1, f"第{index}版正文", "", "manual") is not None

    listed = _versions(pid)
    assert len(listed) == 100
    files = sorted((_root(pid) / ".novel-gm" / "versions" / "0001").glob("*.json"))
    assert len(files) == 100
    bodies = {json.loads(path.read_text(encoding="utf-8"))["body_text"] for path in files}
    assert "第104版正文" in bodies and "第0版正文" not in bodies


def test_legacy_text_history_is_listed_and_restorable(tmp_path):
    pid = _project(tmp_path, "旧文本兼容")
    _chapter(pid, "新机制正文")
    root = _root(pid)
    file_name = "第1章.md"
    folder = root / ".novel-gm" / "history" / hashlib.sha256(file_name.encode()).hexdigest()[:16]
    folder.mkdir(parents=True, exist_ok=True)
    legacy_text = "旧机制留下的纯文本"
    (folder / (hashlib.sha256(legacy_text.encode()).hexdigest() + ".md")).write_text(legacy_text, encoding="utf-8")

    listed = _versions(pid)
    legacy = [v for v in listed if v["source"] == "legacy-text"]
    assert len(legacy) == 1
    assert legacy[0]["format_available"] is False
    assert _version(pid, legacy[0]["id"])["body_text"] == legacy_text

    r = client.post(f"/project/{pid}/chapter/1/versions/{legacy[0]['id']}/restore")
    assert r.status_code == 200, r.text
    assert r.json()["body_text"] == legacy_text
    assert r.json()["body_rich"] == ""


def test_legacy_text_duplicate_hash_is_skipped(tmp_path):
    pid = _project(tmp_path, "旧文本去重")
    _chapter(pid, "同一段正文")
    root = _root(pid)
    assert versions_module.checkpoint(pid, 1, "同一段正文", "", "manual") is not None
    folder = root / ".novel-gm" / "history" / hashlib.sha256("第1章.md".encode()).hexdigest()[:16]
    folder.mkdir(parents=True, exist_ok=True)
    (folder / (hashlib.sha256("同一段正文".encode()).hexdigest() + ".md")).write_text(
        "同一段正文", encoding="utf-8"
    )
    assert all(v["source"] != "legacy-text" for v in _versions(pid))


def test_legacy_text_only_lists_backups_older_than_new_versions(tmp_path):
    pid = _project(tmp_path, "旧文本时间窗")
    _chapter(pid, "当前正文")
    root = _root(pid)
    folder = root / ".novel-gm" / "history" / hashlib.sha256("第1章.md".encode()).hexdigest()[:16]
    folder.mkdir(parents=True, exist_ok=True)
    old_file = folder / (hashlib.sha256("很久以前的正文".encode()).hexdigest() + ".md")
    old_file.write_text("很久以前的正文", encoding="utf-8")
    os.utime(old_file, (946684800, 946684800))  # 2000-01-01，早于本轮新版本

    # 新版本出现之前留下的旧备份仍可见
    assert versions_module.checkpoint(pid, 1, "当前正文", "", "manual") is not None
    legacy = [v for v in _versions(pid) if v["source"] == "legacy-text"]
    assert len(legacy) == 1
    assert _version(pid, legacy[0]["id"])["body_text"] == "很久以前的正文"

    # 新版本之后写出的 history（新机制）不再混进版本列表
    new_file = folder / (hashlib.sha256("新机制写出的正文".encode()).hexdigest() + ".md")
    new_file.write_text("新机制写出的正文", encoding="utf-8")
    bodies = [
        _version(pid, v["id"])["body_text"]
        for v in _versions(pid)
        if v["source"] == "legacy-text"
    ]
    assert bodies == ["很久以前的正文"]


def test_versions_shift_when_chapters_deleted_and_inserted(tmp_path):
    pid = _project(tmp_path, "版本书平移")
    _chapter(pid, "第一章正文")
    _chapter(pid, "第二章正文")
    _chapter(pid, "第三章正文")
    for no, text in ((1, "一版"), (2, "二版"), (3, "三版")):
        assert versions_module.checkpoint(pid, no, text, "", "manual") is not None

    assert client.delete(f"/project/{pid}/chapter/1").status_code == 200
    assert _version(pid, _versions(pid, 1)[0]["id"], 1)["body_text"] == "二版"
    assert _version(pid, _versions(pid, 2)[0]["id"], 2)["body_text"] == "三版"

    # 在第 1 章后插一章：原第 2 章变成第 3 章，版本目录跟着后移
    assert client.post(f"/project/{pid}/chapter/1/insert-after").status_code == 200
    assert _versions(pid, 2) == []
    assert _version(pid, _versions(pid, 3)[0]["id"], 3)["body_text"] == "三版"


def test_unknown_version_returns_404(tmp_path):
    pid = _project(tmp_path, "缺版本")
    _chapter(pid, "正文")
    r = client.get(f"/project/{pid}/chapter/1/versions/nope")
    assert r.status_code == 404
    assert r.json()["detail"]["code"] == "NOT_FOUND"
