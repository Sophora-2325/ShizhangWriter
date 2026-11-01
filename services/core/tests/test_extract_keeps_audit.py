"""提取摘要不得碰语义审计的结果；以及「语义那一趟是否真的跑过」的标记。

回归的缺陷：`api_extract` 曾在同一列上顺带跑一次纯规则审计并覆盖 `consistency_report`。
规则层通常 0 命中 → 语义审计的 flags 被静默抹成空报告，界面上看起来就像「本章没有问题」。
现在两者各写各的：只有 `api_audit` 写 `consistency_report`。
"""

from __future__ import annotations

import itertools
import sys
import tempfile
from pathlib import Path

from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db

db.STORAGE = ROOT / "storage" / "test_extract_audit"
db.DB_PATH = db.STORAGE / "test_extract_audit.db"
db.PROJECTS_DIR = db.STORAGE / "projects"
db.STORAGE.mkdir(parents=True, exist_ok=True)
if db.DB_PATH.exists():
    db.DB_PATH.unlink()

from services.core.db import init_db
from services.core.main import app
from services.core.models import ConsistencyFlag, ConsistencyReport
from services.core.repository import get_chapter, upsert_chapter

init_db()
client = TestClient(app)
PARENT = Path(tempfile.mkdtemp(prefix="novelgm_extract_"))

BODY = "雨还在下。他把杯子放回桌面，慢了半拍。"

# 同一目录下作品文件夹名不能重复（重名会 409 FOLDER_EXISTS），所以每个测试用不同标题
_seq = itertools.count(1)


def _new_chapter(body: str = BODY) -> tuple[str, int]:
    r = client.post(
        "/project",
        json={
            "title": f"提取与审计{next(_seq)}",
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
    ch = client.post(f"/project/{pid}/chapters", json={"body_text": body, "body_rich": ""})
    assert ch.status_code == 200, ch.text
    assert ch.json()["no"] == 1
    return pid, 1


def _plant_semantic_report(pid: str, no: int) -> ConsistencyReport:
    """直接往章节里放一份「语义审计跑过、且有 1 条 fail」的报告。"""
    report = ConsistencyReport(
        flags=[
            ConsistencyFlag(
                kind="semantic",
                severity="fail",
                point="与设定冲突",
                evidence="雨还在下",
                suggestion="改写这一处",
            )
        ],
        passed=False,
        semantic_checked=True,
    )
    ch = get_chapter(pid, no)
    ch.consistency_report = report
    upsert_chapter(pid, ch)
    return report


def test_extract_does_not_touch_consistency_report():
    pid, no = _new_chapter()
    planted = _plant_semantic_report(pid, no)

    r = client.post(f"/project/{pid}/chapter/{no}/extract")
    assert r.status_code == 200, r.text

    now = get_chapter(pid, no).consistency_report
    assert now is not None, "提取把整份报告删了"
    assert now.semantic_checked is True, "提取把「查过语义审计」的标记抹了"
    assert now.passed is False, "提取把结论翻成了「通过」"
    assert len(now.flags) == 1, "提取把 flags 抹空了"
    assert now.flags[0].point == planted.flags[0].point

    # 反向确认提取本身确实干了活，不是整条路径空转
    assert r.json()["summary"] is not None


def test_extract_no_longer_manufactures_an_empty_report():
    """没审计过的章节，提取后仍然是「没有报告」，而不是一份看起来干净的空报告。"""
    pid, no = _new_chapter()
    assert get_chapter(pid, no).consistency_report is None

    assert client.post(f"/project/{pid}/chapter/{no}/extract").status_code == 200

    assert get_chapter(pid, no).consistency_report is None


def test_rule_only_audit_is_marked_as_not_semantically_checked():
    pid, no = _new_chapter()
    r = client.post(f"/project/{pid}/audit/{no}?semantic=false")
    assert r.status_code == 200, r.text
    rep = get_chapter(pid, no).consistency_report
    assert rep is not None
    assert rep.semantic_checked is False


def test_semantic_audit_is_marked_as_checked():
    pid, no = _new_chapter()
    r = client.post(f"/project/{pid}/audit/{no}?semantic=true")
    assert r.status_code == 200, r.text
    rep = get_chapter(pid, no).consistency_report
    assert rep is not None
    assert rep.semantic_checked is True


def test_semantic_audit_on_empty_body_is_not_marked_as_checked():
    """正文为空时不会真的调模型，所以不能标成「查过」——否则界面会把「没查」显示成「干净」。"""
    pid, no = _new_chapter(body="")
    r = client.post(f"/project/{pid}/audit/{no}?semantic=true")
    assert r.status_code == 200, r.text
    rep = get_chapter(pid, no).consistency_report
    assert rep is not None
    assert rep.semantic_checked is False


def test_planted_report_survives_a_full_extract_finalize_round_trip():
    """提取 + 定稿都不该动 consistency_report（定稿只写 style_report）。"""
    pid, no = _new_chapter()
    _plant_semantic_report(pid, no)

    assert client.post(f"/project/{pid}/chapter/{no}/finalize").status_code == 200
    assert client.post(f"/project/{pid}/chapter/{no}/extract").status_code == 200

    now = get_chapter(pid, no).consistency_report
    assert now is not None
    assert now.semantic_checked is True
    assert len(now.flags) == 1
    assert now.passed is False
