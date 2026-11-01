"""evidence 规范化 / 子串修复 / 置空。"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from services.core.extraction import _verify_evidence, normalize_evidence
from services.core.models import Beat, ChapterSummary


BODY = (
    "雨还在下。他把杯子放回桌面，慢了半拍。\n\n"
    "「你来晚了。」对面的人说。\n\n"
    "他没有接话，只看着窗外那串模糊的数字。"
)


def test_normalize_exact_hit():
    assert normalize_evidence("雨还在下", BODY) == "雨还在下"


def test_normalize_strip_quotes():
    # 去首尾成对「」后应命中正文（正文含「你来晚了。」整段或内部子串）
    fixed = normalize_evidence("「你来晚了。」", BODY)
    assert fixed
    assert fixed in BODY
    assert "你来晚了" in fixed


def test_normalize_merged_sentences_repairs_to_substring():
    # 模型把两句粘在一起 —— 应裁出仍在正文中的最长连续子串
    bad = "雨还在下。他把杯子放回桌面，慢了半拍。对面的人说。"
    fixed = normalize_evidence(bad, BODY)
    assert fixed
    assert fixed in BODY
    assert len(fixed) >= 8
    assert len(fixed) <= 40


def test_normalize_too_long_truncates_then_hits():
    long = "他把杯子放回桌面，慢了半拍。" * 3
    fixed = normalize_evidence(long, BODY)
    assert fixed
    assert fixed in BODY
    assert len(fixed) <= 40


def test_normalize_hallucination_clears():
    assert normalize_evidence("星际舰队集结完毕发动总攻", BODY) == ""


def test_verify_evidence_clears_and_flags():
    summary = ChapterSummary(
        beats=[
            Beat(order=1, what_happened="下雨", evidence="雨还在下"),
            Beat(order=2, what_happened="幻觉", evidence="星际舰队集结完毕发动总攻"),
            Beat(order=3, what_happened="空", evidence=""),
        ]
    )
    out, rate, failed = _verify_evidence(summary, BODY)
    assert out.beats[0].evidence == "雨还在下"
    assert out.beats[1].evidence == ""
    assert out.beats[2].evidence == ""
    assert rate == 0.5  # 1 hit / 2 attempted
    assert len(failed) == 1
    assert any("摘要证据未命中正文已清空" in f for f in out.continuity_flags)


def test_verify_merged_repairs_without_fail():
    summary = ChapterSummary(
        beats=[
            Beat(
                order=1,
                what_happened="会面",
                evidence="他把杯子放回桌面，慢了半拍。对面的人说",
            ),
        ]
    )
    out, rate, failed = _verify_evidence(summary, BODY, clear_failed=False)
    assert rate == 1.0
    assert not failed
    assert out.beats[0].evidence
    assert out.beats[0].evidence in BODY


def test_verify_keep_bad_when_not_clearing():
    summary = ChapterSummary(
        beats=[Beat(order=1, what_happened="幻觉", evidence="星际舰队集结完毕发动总攻")]
    )
    out, rate, failed = _verify_evidence(summary, BODY, clear_failed=False)
    assert rate == 0.0
    assert len(failed) == 1
    assert out.beats[0].evidence == "星际舰队集结完毕发动总攻"
    assert not out.continuity_flags
