"""「本章校验」：核对计划里的约束有没有被正文照做，且与语义审计互不覆盖。

本地部分（脉络推进 / 字数）免费且确定；must_include / must_not 是自由文本，交模型判断并过证据门禁。
"""

from __future__ import annotations

import itertools
import sys
import tempfile
from datetime import datetime
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db

db.STORAGE = ROOT / "storage" / "test_plan_check"
db.DB_PATH = db.STORAGE / "test_plan_check.db"
db.PROJECTS_DIR = db.STORAGE / "projects"
db.STORAGE.mkdir(parents=True, exist_ok=True)
if db.DB_PATH.exists():
    db.DB_PATH.unlink()

from services.core.audit import run_plan_check
from services.core.db import init_db
from services.core.main import app
from services.core.models import (
    Chapter,
    ChapterPlan,
    ChapterStatus,
    ConsistencyFlag,
    ConsistencyReport,
    PlotThread,
    Project,
    StateDelta,
)
from services.core.repository import get_chapter, upsert_chapter

init_db()
client = TestClient(app)
PARENT = Path(tempfile.mkdtemp(prefix="novelgm_plancheck_"))
_seq = itertools.count(1)

BODY = "雨还在下，他把断掉的鳞片捡起来放进兜里，没有回头。"


def _project() -> Project:
    return Project(
        id="plancheck-unit",
        title="校验单测",
        created_at=datetime(2026, 1, 1),
        provider="mock",
        threads=[PlotThread(id="main", name="主线", kind="main", state="active")],
    )


def _chapter(plan: ChapterPlan, *, delta: StateDelta | None = None, body: str = BODY) -> Chapter:
    return Chapter(no=1, status=ChapterStatus.drafted, plan=plan, body_text=body, state_delta=delta)


# ---------------- 本地部分：脉络推进 ----------------


def test_no_plan_means_nothing_to_check():
    rep = run_plan_check(_project(), _chapter(None), [], semantic=False)  # type: ignore[arg-type]
    assert rep.flags == []
    assert rep.semantic_checked is False


def test_required_thread_without_extract_is_info_not_silence():
    plan = ChapterPlan(required_threads=["main"], target_word_count=0)
    rep = run_plan_check(_project(), _chapter(plan), [], semantic=False)
    assert len(rep.flags) == 1
    assert rep.flags[0].severity == "info"
    assert "还没提取摘要" in rep.flags[0].point


def test_required_thread_not_moved_is_warned():
    plan = ChapterPlan(required_threads=["main"], target_word_count=0)
    delta = StateDelta(thread_updates={})
    rep = run_plan_check(_project(), _chapter(plan, delta=delta), [], semantic=False)
    assert [f.severity for f in rep.flags] == ["warn"]
    assert "没有让它发生变化" in rep.flags[0].point


def test_required_thread_moved_is_clean():
    plan = ChapterPlan(required_threads=["main"], target_word_count=0)
    delta = StateDelta(thread_updates={"main": {"state": "climax"}})
    rep = run_plan_check(_project(), _chapter(plan, delta=delta), [], semantic=False)
    assert rep.flags == []
    assert rep.passed is True


def test_unknown_thread_id_is_flagged():
    plan = ChapterPlan(required_threads=["does-not-exist"], target_word_count=0)
    rep = run_plan_check(_project(), _chapter(plan), [], semantic=False)
    assert rep.flags[0].severity == "info"
    assert "不在本章设定里" in rep.flags[0].point


def test_target_word_count_drift_is_reported():
    plan = ChapterPlan(target_word_count=1000)
    rep = run_plan_check(_project(), _chapter(plan), [], semantic=False)
    assert any("正文实际" in f.point for f in rep.flags)


# ---------------- 模型部分：走 mock provider ----------------


def test_free_text_requirements_go_through_the_model():
    plan = ChapterPlan(must_not=["不要写血腥"])
    rep = run_plan_check(_project(), _chapter(plan), [], semantic=True)
    assert rep.semantic_checked is True


def test_no_free_text_means_model_is_not_called():
    plan = ChapterPlan(required_threads=["main"], target_word_count=0)
    delta = StateDelta(thread_updates={"main": {}})
    rep = run_plan_check(_project(), _chapter(plan, delta=delta), [], semantic=True)
    assert rep.semantic_checked is False


# ---------------- 端点：与审计报告互不覆盖 ----------------


def _new_chapter(plan: dict) -> tuple[str, int]:
    r = client.post(
        "/project",
        json={
            "title": f"校验{next(_seq)}",
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
    ch = client.post(
        f"/project/{pid}/chapters",
        json={"body_text": BODY, "body_rich": "", "plan": plan},
    )
    assert ch.status_code == 200, ch.text
    return pid, ch.json()["no"]


def test_plan_check_writes_its_own_field_and_leaves_the_audit_alone():
    pid, no = _new_chapter({"must_not": ["不要写血腥"], "target_word_count": 10})

    planted = ConsistencyReport(
        flags=[ConsistencyFlag(kind="semantic", severity="fail", point="与设定冲突", evidence="雨还在下")],
        passed=False,
        semantic_checked=True,
    )
    ch = get_chapter(pid, no)
    ch.consistency_report = planted
    upsert_chapter(pid, ch)

    r = client.post(f"/project/{pid}/chapter/{no}/plan-check")
    assert r.status_code == 200, r.text

    after = get_chapter(pid, no)
    assert after.plan_check is not None, "校验结果没有落盘"
    assert after.consistency_report is not None
    assert after.consistency_report.semantic_checked is True, "校验把审计报告覆盖了"
    assert len(after.consistency_report.flags) == 1
    assert after.consistency_report.flags[0].point == "与设定冲突"


def test_plan_check_does_not_write_the_consistency_report():
    pid, no = _new_chapter({"must_include": ["他捡起了鳞片"]})
    assert get_chapter(pid, no).consistency_report is None

    assert client.post(f"/project/{pid}/chapter/{no}/plan-check").status_code == 200

    assert get_chapter(pid, no).consistency_report is None
    assert get_chapter(pid, no).plan_check is not None
