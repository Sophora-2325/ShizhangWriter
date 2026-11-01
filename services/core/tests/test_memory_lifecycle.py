"""Regression coverage for safe persistence and versioned chapter memory."""
import pytest

from services.core import db
from services.core import repository as repo
from services.core import workspace as ws
from services.core.models import (
    Chapter,
    ChapterPlan,
    ChapterSummary,
    ConsistencyReport,
    ProjectCreate,
)


@pytest.fixture
def work(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path / "data")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "data/db.sqlite")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "data/projects")
    db.init_db()
    p = repo.create_project(ProjectCreate(title="测试", parent_dir=str(tmp_path)))
    p.provider = "mock"
    repo.save_project(p)
    return p


def test_rename_failure_keeps_original(tmp_path, monkeypatch):
    (tmp_path / "旧.md").write_text("原稿", encoding="utf-8")
    def fail(*args, **kwargs):
        raise OSError("disk full")
    monkeypatch.setattr(ws.os, "replace", fail)
    with pytest.raises(OSError):
        ws.write_chapter_body(tmp_path, "新.md", "新稿", "旧.md")
    assert (tmp_path / "旧.md").read_text(encoding="utf-8") == "原稿"


def test_body_edit_invalidates_memory_and_reports(work):
    ch = Chapter(no=1, title="一", body_text="旧正文", summary=ChapterSummary(one_line="旧摘要"),
                 consistency_report=ConsistencyReport())
    repo.upsert_chapter(work.id, ch)
    repo.add_fact(work.id, 1, "旧事实", [])
    ch = repo.get_chapter(work.id, 1)
    ch.body_text = "新正文"
    repo.upsert_chapter(work.id, ch)
    now = repo.get_chapter(work.id, 1)
    assert now.summary is None
    assert now.consistency_report is None
    assert now.summary_status == "stale"
    assert repo.search_facts(work.id, "旧事实") == []


def test_formatting_does_not_invalidate(work):
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="正文", summary=ChapterSummary(one_line="摘要")))
    ch = repo.get_chapter(work.id, 1)
    ch.body_rich = "<FlowDocument/>"
    repo.upsert_chapter(work.id, ch)
    assert repo.get_chapter(work.id, 1).summary.one_line == "摘要"


def test_context_never_uses_future_chapters(work):
    from services.core.context import build_layered_context
    chapters = [Chapter(no=n, status="final", body_text=f"第{n}章正文",
                        summary=ChapterSummary(one_line=f"第{n}章秘密")) for n in (1, 3, 10)]
    layers = build_layered_context(work, ChapterPlan(), chapters, target_chapter=3)
    assert "第10章" not in layers["L2"]
    assert "第3章秘密" not in layers["L2"]
    assert "第1章秘密" in layers["L2"]


def test_stale_analysis_cannot_overwrite_new_body(work):
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="旧稿"))
    old = repo.get_chapter(work.id, 1)
    new = old.model_copy(update={"body_text": "新稿"})
    repo.upsert_chapter(work.id, new)
    old.summary = ChapterSummary(one_line="旧稿摘要")
    from fastapi import HTTPException
    with pytest.raises(HTTPException) as exc:
        repo.upsert_chapter(work.id, old, expected_hash=old.body_hash)
    assert exc.value.status_code == 409
    assert repo.get_chapter(work.id, 1).body_text == "新稿"


def test_finalize_extracts_and_failure_preserves_final(work, monkeypatch):
    from services.core import main
    from services.core.models import StateDelta
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="正文"))
    monkeypatch.setattr(main, "run_extract", lambda *a: (ChapterSummary(one_line="摘要"), StateDelta(), {}))
    result = main.api_finalize(work.id, 1)
    assert result.summary_status == "ready"
    assert result.summary.one_line == "摘要"
    ch = repo.get_chapter(work.id, 1)
    ch.body_text = "改稿"
    repo.upsert_chapter(work.id, ch)
    def fail(*a):
        raise RuntimeError("offline")
    monkeypatch.setattr(main, "run_extract", fail)
    result = main.api_finalize(work.id, 1)
    assert result.status.value == "final"
    assert result.summary_status == "failed"


def test_failed_extraction_keeps_previous_facts(work, monkeypatch):
    from services.core import main
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="正文", summary=ChapterSummary(one_line="摘要")))
    repo.add_fact(work.id, 1, "仍有效的事实", [])
    def fail(*a):
        raise RuntimeError("offline")
    monkeypatch.setattr(main, "run_extract", fail)
    with pytest.raises(RuntimeError):
        main.api_extract(work.id, 1)
    assert repo.search_facts(work.id, "仍有效的事实")


def test_save_replays_after_metadata_failure(work, monkeypatch):
    from pathlib import Path
    root = Path(work.root_path)
    repo.upsert_chapter(work.id, Chapter(no=1, title="旧名", body_text="原稿"))
    ch = repo.get_chapter(work.id, 1)
    ch.title = "新名"
    ch.body_text = "新稿"
    writer = ws.write_chapter_meta
    def fail(*args):
        raise OSError("metadata unavailable")
    monkeypatch.setattr(ws, "write_chapter_meta", fail)
    with pytest.raises(OSError):
        repo.upsert_chapter(work.id, ch)
    assert (root / "旧名.md").read_text(encoding="utf-8") == "原稿"
    assert (root / ".novel-gm/pending-save.json").exists()
    monkeypatch.setattr(ws, "write_chapter_meta", writer)
    assert repo.get_chapter(work.id, 1).body_text == "新稿"
    assert not (root / "旧名.md").exists()
    assert not (root / ".novel-gm/pending-save.json").exists()
    assert any(p.read_text(encoding="utf-8") == "原稿" for p in (root / ".novel-gm/history").rglob("*.md"))


def test_upstream_edit_preserves_text_but_invalidates_dependent_memory(work):
    for n in (1, 2):
        repo.upsert_chapter(work.id, Chapter(no=n, title=str(n), body_text=f"正文{n}",
                            summary=ChapterSummary(one_line=f"摘要{n}"), consistency_report=ConsistencyReport()))
        repo.add_fact(work.id, n, f"事实{n}", [])
    ch = repo.get_chapter(work.id, 1)
    ch.body_text = "第一章改稿"
    repo.upsert_chapter(work.id, ch)
    later = repo.get_chapter(work.id, 2)
    assert later.body_text == "正文2"
    assert later.upstream_changed == [1]
    assert later.summary is None and later.consistency_report is None
    assert "summary" in later.stale_results
    assert repo.search_facts(work.id, "事实2") == []


def test_external_edit_excludes_old_summary_and_facts(work):
    from pathlib import Path
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="正文", summary=ChapterSummary(one_line="摘要")))
    repo.add_fact(work.id, 1, "旧事实", [])
    (Path(work.root_path) / "一.md").write_text("外部改稿", encoding="utf-8")
    assert repo.get_chapter(work.id, 1).summary is None
    assert repo.search_facts(work.id, "旧事实") == []


def test_future_facts_never_enter_retrieval(work):
    for n in (1, 10):
        repo.upsert_chapter(work.id, Chapter(no=n, title=str(n), body_text="正文", summary=ChapterSummary(one_line="摘要")))
        repo.add_fact(work.id, n, f"秘密来自第{n}章", ["甲"])
    facts = repo.search_facts(work.id, "秘密", before_chapter=3)
    assert [f["chapter_no"] for f in facts] == [1]
    assert facts[0]["known_by"] == ["甲"]


def test_semantic_lookup_and_failure_fallback(work, monkeypatch):
    from services.core import retrieval
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="正文", summary=ChapterSummary(one_line="摘要")))
    repo.add_fact(work.id, 1, "林舟代替同伴承认了纵火", [])
    monkeypatch.setenv("NOVEL_GM_EMBEDDING_URL", "http://127.0.0.1:8080/v1")
    monkeypatch.setenv("NOVEL_GM_EMBEDDING_MODEL", "model-a")
    calls = []
    def embed(texts, config):
        calls.append(config[1])
        return [[1.0, 0.0] for _ in texts]
    monkeypatch.setattr(retrieval, "_embed", embed)
    found = repo.search_facts(work.id, "谁替他承担罪责")
    assert found and found[0]["retrieval_mode"] == "hybrid"
    monkeypatch.setenv("NOVEL_GM_EMBEDDING_MODEL", "model-b")
    assert retrieval.reindex(work.id)["indexed"] == 1
    assert calls[-1] == "model-b"
    def fail(*args):
        raise ValueError("service unavailable")
    monkeypatch.setattr(retrieval, "_embed", fail)
    found = repo.search_facts(work.id, "林舟")
    assert found and found[0]["retrieval_mode"] == "lexical"
    assert repo.search_facts(work.id, "苹果手机") == []


def test_offline_mode_never_calls_cloud_embedding(work, monkeypatch):
    from services.core import retrieval
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="正文", summary=ChapterSummary(one_line="摘要")))
    repo.add_fact(work.id, 1, "林舟承担罪责", [])
    work.offline_mode = True
    repo.save_project(work)
    monkeypatch.setenv("NOVEL_GM_EMBEDDING_URL", "https://example.invalid/v1")
    monkeypatch.setenv("NOVEL_GM_EMBEDDING_MODEL", "model")
    def forbidden(*args):
        pytest.fail("offline mode contacted cloud embedding")
    monkeypatch.setattr(retrieval, "_embed", forbidden)
    assert repo.search_facts(work.id, "林舟")


def test_edit_during_extract_rejects_results(work, monkeypatch):
    from fastapi import HTTPException

    from services.core import main
    from services.core.models import NewFact, StateDelta
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="原稿"))
    def extracting(*args):
        current = repo.get_chapter(work.id, 1)
        current.body_text = "更新稿"
        repo.upsert_chapter(work.id, current)
        return ChapterSummary(one_line="原稿摘要", new_facts=[NewFact(fact="原稿事实")]), StateDelta(), {}
    monkeypatch.setattr(main, "run_extract", extracting)
    with pytest.raises(HTTPException) as exc:
        main.api_extract(work.id, 1)
    assert exc.value.status_code == 409
    assert repo.get_chapter(work.id, 1).body_text == "更新稿"
    assert repo.search_facts(work.id, "原稿事实") == []


def test_extract_ignores_future_even_on_evidence_retry(work, monkeypatch):
    from services.core import extraction
    from services.core.models import Beat
    captured = []
    monkeypatch.setattr(extraction, "_chat_extract", lambda *args: {})
    monkeypatch.setattr(extraction, "assemble_messages", lambda layers, *args: captured.append(layers) or [])
    monkeypatch.setattr(extraction, "_parse_summary", lambda *args: (ChapterSummary(), {}))
    summary = ChapterSummary(beats=[Beat(order=1, what_happened="事件", evidence="错误")])
    future = Chapter(no=10, status="final", body_text="未来秘密")
    extraction._retry_failed_evidence(summary, [(summary.beats[0], "错误")], "本章正文", work, None,
                                      [future], None, "mock", "mock", 3)
    assert "未来秘密" not in captured[0]["L2"]


def test_refresh_summary_does_not_silently_approve_old_plan(work, monkeypatch):
    from services.core import main
    from services.core.models import StateDelta
    for n in (1, 2):
        repo.upsert_chapter(work.id, Chapter(no=n, title=str(n), body_text=f"正文{n}",
                            plan=ChapterPlan(scene_goal="原计划"), summary=ChapterSummary(one_line="摘要")))
    first = repo.get_chapter(work.id, 1)
    first.body_text = "改变走向"
    repo.upsert_chapter(work.id, first)
    monkeypatch.setattr(main, "run_extract", lambda *a: (ChapterSummary(one_line="新摘要"), StateDelta(), {}))
    updated = main.api_extract(work.id, 2)
    assert updated.upstream_changed == []
    assert updated.plan_upstream_changed == [1]
    main.api_save_plan(work.id, 2, updated.plan)
    assert repo.get_chapter(work.id, 2).plan_upstream_changed == []


def test_audit_cannot_commit_if_plan_changes_during_call(work, monkeypatch):
    from fastapi import HTTPException

    from services.core import main
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="正文", plan=ChapterPlan(scene_goal="原计划")))
    def audit(*args, **kwargs):
        main.api_save_plan(work.id, 1, ChapterPlan(scene_goal="新计划"))
        return ConsistencyReport()
    monkeypatch.setattr(main, "run_audit", audit)
    with pytest.raises(HTTPException) as exc:
        main.api_audit(work.id, 1)
    assert exc.value.status_code == 409
    assert repo.get_chapter(work.id, 1).plan.scene_goal == "新计划"
    assert repo.get_chapter(work.id, 1).consistency_report is None


def test_import_preserves_fact_source_version(work):
    repo.upsert_chapter(work.id, Chapter(no=1, title="一", body_text="正文", summary=ChapterSummary(one_line="摘要")))
    repo.add_fact(work.id, 1, "历史事实", ["甲"])
    original = repo.search_facts(work.id, "历史事实")[0]["source_hash"]
    repo.import_folder(work.root_path)
    assert repo.search_facts(work.id, "历史事实")[0]["source_hash"] == original


def test_global_offline_mode_disables_cloud_embedding(work, monkeypatch):
    from services.core import retrieval
    monkeypatch.setenv("NOVEL_GM_EMBEDDING_URL", "https://example.invalid/v1")
    monkeypatch.setenv("NOVEL_GM_EMBEDDING_MODEL", "model")
    with db.connect() as conn:
        db.set_setting(conn, "offline_mode", "1")
    assert retrieval._config(False) is None
