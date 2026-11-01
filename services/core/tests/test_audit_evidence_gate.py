"""semantic flag evidence 门禁：无原文依据则降为 info。"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from services.core.audit import gate_semantic_flags, normalize_audit_evidence
from services.core.models import ConsistencyFlag

BODY = (
    "雨还在下。他把杯子放回桌面，慢了半拍。\n\n"
    "「你来晚了。」对面的人说。\n\n"
    "他没敢解开师父太刀刀鞘上的结。"
)


def test_normalize_exact_hit():
    assert normalize_audit_evidence("雨还在下", BODY) == "雨还在下"


def test_normalize_hallucination_clears():
    assert normalize_audit_evidence("星际舰队集结完毕", BODY) == ""


def test_gate_keeps_severity_when_evidence_in_body():
    flags = [
        ConsistencyFlag(
            kind="semantic",
            severity="fail",
            point="对峙开场",
            evidence="雨还在下",
            suggestion="核对",
        )
    ]
    out = gate_semantic_flags(flags, BODY)
    assert len(out) == 1
    assert out[0].severity == "fail"
    assert out[0].evidence == "雨还在下"


def test_gate_downgrades_hallucination_to_info():
    flags = [
        ConsistencyFlag(
            kind="semantic",
            severity="fail",
            point="它与正文中跳下地面不一致",
            evidence="皮卡丘跳下了地面发动攻击",
            suggestion="改设定",
        )
    ]
    out = gate_semantic_flags(flags, BODY)
    assert out[0].severity == "info"
    assert out[0].evidence == ""
    assert out[0].point.startswith("无原文依据")


def test_gate_repairs_substring_and_keeps_warn():
    flags = [
        ConsistencyFlag(
            kind="semantic",
            severity="warn",
            point="刀鞘",
            evidence="他没敢解开师父太刀刀鞘上的结。因此不能使用这把刀。",
            suggestion="",
        )
    ]
    out = gate_semantic_flags(flags, BODY)
    assert out[0].severity == "warn"
    assert out[0].evidence
    assert out[0].evidence in BODY


def test_gate_ignores_non_semantic():
    flags = [
        ConsistencyFlag(kind="rule", severity="fail", point="禁忌", evidence="xxx"),
        ConsistencyFlag(kind="semantic", severity="fail", point="幻觉", evidence="不存在的句子"),
    ]
    out = gate_semantic_flags(flags, BODY)
    assert out[0].severity == "fail"
    assert out[0].kind == "rule"
    assert out[1].severity == "info"
