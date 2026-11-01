from __future__ import annotations

import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db
from services.core import search as search_module

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
    return r.json()["id"]


def _chapter(pid: str, text: str) -> dict:
    r = client.post(f"/project/{pid}/chapters", json={"body_text": text})
    assert r.status_code == 200, r.text
    return r.json()


def _search(pid: str, **body) -> dict:
    r = client.post(f"/project/{pid}/search", json=body)
    assert r.status_code == 200, r.text
    return r.json()


def test_book_search_counts_offsets_and_context(tmp_path):
    pid = _project(tmp_path, "查找基础")
    _chapter(pid, "开头。雨夜。中段。雨夜。结尾。")
    _chapter(pid, "晴天。雨夜。")

    result = _search(pid, query="雨夜")
    assert result["total"] == 3 and result["truncated"] is False
    assert [c["chapter_no"] for c in result["chapters"]] == [1, 2]
    assert [c["count"] for c in result["chapters"]] == [2, 1]
    first = result["chapters"][0]["matches"][0]
    assert first["offset"] == 3
    assert first["context"][first["context_offset"]:first["context_offset"] + 2] == "雨夜"
    assert first["in_draft"] is False

    empty = _search(pid, query="不存在")
    assert empty["total"] == 0 and empty["chapters"] == []


def test_search_context_window_is_30_chars(tmp_path):
    pid = _project(tmp_path, "查找上下文")
    body = "前" * 40 + "目标" + "后" * 40
    _chapter(pid, body)
    match = _search(pid, query="目标")["chapters"][0]["matches"][0]
    assert match["offset"] == 40
    assert match["context"] == body[10:72]
    assert match["context_offset"] == 30


def test_case_sensitivity_and_casefold_offsets(tmp_path):
    pid = _project(tmp_path, "查找大小写")
    _chapter(pid, "abc ABC def")

    sensitive = _search(pid, query="ABC", case_sensitive=True)
    assert sensitive["total"] == 1
    assert sensitive["chapters"][0]["matches"][0]["offset"] == 4

    insensitive = _search(pid, query="ABC", case_sensitive=False)
    assert insensitive["total"] == 2
    assert [m["offset"] for m in insensitive["chapters"][0]["matches"]] == [0, 4]

    # casefold 可能把一个字符折成多个（ß→ss），偏移仍要落在原文上
    _chapter(pid, "Straße am Weg")
    folded = _search(pid, query="strasse")
    assert folded["total"] == 1
    assert folded["chapters"][0]["chapter_no"] == 2
    assert folded["chapters"][0]["matches"][0]["offset"] == 0


def test_draft_text_replaces_current_chapter(tmp_path):
    pid = _project(tmp_path, "查找草稿")
    _chapter(pid, "雨夜。")
    _chapter(pid, "雨夜。雨夜。")

    chapter_scope = _search(
        pid, query="雨夜", scope="chapter", chapter_no=1, draft_text="雨夜雨夜雨夜"
    )
    assert chapter_scope["total"] == 3
    assert [c["chapter_no"] for c in chapter_scope["chapters"]] == [1]
    assert all(m["in_draft"] for m in chapter_scope["chapters"][0]["matches"])

    book_scope = _search(pid, query="雨夜", chapter_no=1, draft_text="雨夜雨夜雨夜")
    assert book_scope["total"] == 5
    assert book_scope["chapters"][0]["count"] == 3
    assert all(m["in_draft"] for m in book_scope["chapters"][0]["matches"])
    assert all(not m["in_draft"] for m in book_scope["chapters"][1]["matches"])


def test_empty_draft_overrides_library_body(tmp_path):
    """draft_text="" 表示当前编辑章草稿为空，必须覆盖库里的正文（该章不命中）。"""
    pid = _project(tmp_path, "查找空草稿")
    _chapter(pid, "雨夜。")
    _chapter(pid, "雨夜。")

    chapter_scope = _search(pid, query="雨夜", scope="chapter", chapter_no=1, draft_text="")
    assert chapter_scope["total"] == 0
    assert chapter_scope["chapters"] == []

    book_scope = _search(pid, query="雨夜", chapter_no=1, draft_text="")
    assert book_scope["total"] == 1
    assert [c["chapter_no"] for c in book_scope["chapters"]] == [2]


def test_missing_draft_uses_library_body(tmp_path):
    """不传 draft_text（null/缺省）时按库里的正文查找。"""
    pid = _project(tmp_path, "查找无草稿")
    _chapter(pid, "雨夜。")
    result = _search(pid, query="雨夜", scope="chapter", chapter_no=1)
    assert result["total"] == 1
    assert result["chapters"][0]["matches"][0]["in_draft"] is False


def test_global_pagination_across_chapters(tmp_path):
    pid = _project(tmp_path, "查找分页")
    _chapter(pid, "雨夜。" * 5)
    _chapter(pid, "雨夜。")

    page1 = _search(pid, query="雨夜", page=1, page_size=2)
    assert page1["total"] == 6
    assert page1["chapters"][0]["chapter_no"] == 1
    assert page1["chapters"][0]["count"] == 5
    assert len(page1["chapters"][0]["matches"]) == 2

    page3 = _search(pid, query="雨夜", page=3, page_size=2)
    assert [c["chapter_no"] for c in page3["chapters"]] == [1, 2]
    assert [c["count"] for c in page3["chapters"]] == [5, 1]
    assert len(page3["chapters"][0]["matches"]) == 1
    assert len(page3["chapters"][1]["matches"]) == 1

    capped = _search(pid, query="雨夜", page_size=500)
    assert capped["page_size"] == 100


def test_superseded_chapters_are_excluded(tmp_path):
    pid = _project(tmp_path, "查找作废")
    _chapter(pid, "雨夜。")
    _chapter(pid, "雨夜。")
    assert client.post(f"/project/{pid}/rollback/1").status_code == 200
    assert _search(pid, query="雨夜")["total"] == 1


def test_empty_query_is_rejected(tmp_path):
    pid = _project(tmp_path, "查找空串")
    r = client.post(f"/project/{pid}/search", json={"query": "  "})
    assert r.status_code == 400
    assert r.json()["detail"]["code"] == "EMPTY_QUERY"


def test_scan_budget_sets_truncated(tmp_path, monkeypatch):
    pid = _project(tmp_path, "查找预算")
    _chapter(pid, "雨夜雨夜雨夜")
    _chapter(pid, "雨夜雨夜雨夜")
    monkeypatch.setattr(search_module, "MAX_SCAN_CHARS", 4)
    result = _search(pid, query="雨夜")
    assert result["truncated"] is True
    assert result["total"] == 2
