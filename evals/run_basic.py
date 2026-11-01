"""Run golden evals against Mock provider (no API key required)."""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from services.core.generation import run_plan
from services.core.jsonutil import safe_parse
from services.core.models import ChapterPlan, ChapterSummary, LoreEntry, Project, StyleProfile
from services.core.db import init_db, new_id, utcnow
from services.core.extraction import run_extract
from services.core.models import Chapter, ChapterStatus


def main() -> int:
    init_db()
    eval_path = ROOT / "evals" / "basic.json"
    spec = json.loads(eval_path.read_text(encoding="utf-8"))
    proj = Project(
        id=new_id(),
        title="eval",
        created_at=utcnow(),
        phase="PLAN",  # type: ignore
        entries=[LoreEntry(category="world", content="测试前提")],
        provider="mock",
        style=StyleProfile(),
        characters=[],
        threads=[],
    )
    plan, _ = run_plan(proj, [], notes="推进主线")
    for k in spec["expect"]["plan_keys"]:
        assert hasattr(plan, k), f"missing plan key {k}"
    ch = Chapter(no=1, title="1", status=ChapterStatus.drafted, plan=plan, body_text="雨还在下。他把杯子放回桌面。")
    summary, _, _ = run_extract(proj, ch, plan, [])
    for k in spec["expect"]["summary_keys"]:
        assert hasattr(summary, k), f"missing summary key {k}"
    print("evals OK:", spec["name"])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
