"""bible 路径写入：style.* / genre / 人物字典字段 / 脉络优先级。

重点回归两件事：
1. 改风格的「人称/时态/句长/禁用词/替换对照」必须**重编译** compiled_system_constraints，
   否则 L0 继续用旧文本，改动等于没生效。
2. 范文只按换行切分——范文里带逗号是常态，绝不能被当成分隔符切碎。
"""

from __future__ import annotations

import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from fastapi import HTTPException

from services.core.context import build_layered_context
from services.core.main import BiblePatch, _apply_bible_path, _apply_bible_request
from services.core.models import BibleEdit, Character, Phase, PlotThread, Project


def _project() -> Project:
    return Project(
        id="p1",
        title="测试作品",
        created_at=datetime(2026, 1, 1, tzinfo=timezone.utc),
        phase=Phase.PLAN,
        characters=[Character(id="c1", name="结城悠真"), Character(id="c2", name="天野茜")],
        threads=[
            PlotThread(id="main", name="主线", kind="main", state="active"),
            PlotThread(id="romance", name="感情线", kind="romance", state="seeded"),
        ],
    )


# ---------- style.* ----------


def test_style_pov_recompiles_compiled_constraints():
    p = _project()
    assert p.style.compiled_system_constraints == ""
    _apply_bible_path(p, "style.pov", "first")
    assert p.style.pov == "first"
    # 关键：compiled 必须跟着变，否则 L0 里那行「人称：third_limited」永远不会更新
    assert "人称：first" in p.style.compiled_system_constraints


def test_style_banned_words_accepts_newlines_and_commas_and_recompiles():
    p = _project()
    _apply_bible_path(p, "style.banned_words", "心中一紧,五味杂陈\n渐渐地")
    assert p.style.banned_words == ["心中一紧", "五味杂陈", "渐渐地"]
    assert "心中一紧" in p.style.compiled_system_constraints


def test_style_reference_samples_keeps_commas_inside_a_sample():
    p = _project()
    _apply_bible_path(p, "style.reference_samples", "雨还在下，他把杯子放回桌面，慢了半拍。\n第二段范文")
    # 逗号必须留在第一段里，只有换行才是分隔符
    assert p.style.reference_samples == [
        "雨还在下，他把杯子放回桌面，慢了半拍。",
        "第二段范文",
    ]


@pytest.mark.parametrize("text,expected", [
    ("心中一紧→写具体动作", {"心中一紧": "写具体动作"}),
    ("仿佛->直接写本体", {"仿佛": "直接写本体"}),
    ("宛如 => 删掉比喻", {"宛如": "删掉比喻"}),
])
def test_style_preferred_replacements_separators(text, expected):
    p = _project()
    _apply_bible_path(p, "style.preferred_replacements", text)
    assert p.style.preferred_replacements == expected


def test_style_compiled_constraints_can_be_written_verbatim():
    p = _project()
    _apply_bible_path(p, "style.compiled_system_constraints", "自定义规则一\n自定义规则二")
    # 手写 compiled 时不应被 _compile_style 覆盖
    assert p.style.compiled_system_constraints == "自定义规则一\n自定义规则二"


def test_style_metrics_is_read_only():
    p = _project()
    with pytest.raises(HTTPException) as ei:
        _apply_bible_path(p, "style.metrics", "{}")
    assert ei.value.status_code == 400
    assert ei.value.detail["code"] == "READONLY_PATH"


def test_unknown_style_field_is_rejected():
    p = _project()
    with pytest.raises(HTTPException) as ei:
        _apply_bible_path(p, "style.nonsense", "x")
    assert ei.value.detail["code"] == "BAD_PATH"


# ---------- 作品级字段 ----------


def test_forbidden_still_splits_on_commas():
    p = _project()
    _apply_bible_path(p, "forbidden", "死亡, 肢解")
    assert p.forbidden == ["死亡", "肢解"]


def test_genre_tone_audience_and_npc_auto_act():
    p = _project()
    _apply_bible_path(p, "genre", "轻小说, 同人")
    _apply_bible_path(p, "tone", "欢快")
    _apply_bible_path(p, "audience", "中学生")
    _apply_bible_path(p, "npc_auto_act", "true")
    assert p.genre == ["轻小说", "同人"]
    assert p.tone == "欢快"
    assert p.audience == "中学生"
    assert p.npc_auto_act is True


# ---------- 人物级：非字符串字段 ----------


def test_character_public_face_is_plain_string():
    p = _project()
    _apply_bible_path(p, "characters.1.public_face", "骑士团见习生")
    assert p.characters[1].public_face == "骑士团见习生"


def test_character_known_facts_becomes_list():
    p = _project()
    _apply_bible_path(p, "characters.0.known_facts", "碑有两块, 悠真来自异世界")
    assert p.characters[0].known_facts == ["碑有两块", "悠真来自异世界"]


def test_character_status_becomes_dict():
    p = _project()
    _apply_bible_path(p, "characters.1.status", "dead=true\nwhere=旧灯塔")
    assert p.characters[1].status == {"dead": "true", "where": "旧灯塔"}


def test_thread_priority_becomes_int():
    p = _project()
    _apply_bible_path(p, "threads.1.priority", "3")
    assert p.threads[1].priority == 3


def test_legacy_next_milestone_edit_updates_pending_node_without_losing_details():
    p = _project()
    p.threads[0] = PlotThread(id="main", name="主线", milestones=[
        {"id": "done", "name": "相遇", "completed": True},
        {"id": "next", "name": "旧节点", "description": "保留说明"},
        {"id": "later", "name": "后续节点"},
    ])
    _apply_bible_path(p, "threads.0.next_milestone", " 新节点 ")
    assert p.threads[0].next_milestone == "新节点"
    node = p.threads[0].milestones[1]
    assert node.id == "next" and node.description == "保留说明"
    assert p.threads[0].milestones[0].completed
    assert p.threads[0].milestones[2].name == "后续节点"


def test_legacy_next_milestone_can_add_and_remove_the_current_pending_node():
    p = _project()
    _apply_bible_path(p, "threads.0.next_milestone", "相遇")
    assert len(p.threads[0].milestones) == 1
    assert p.threads[0].next_milestone == "相遇"
    _apply_bible_path(p, "threads.0.next_milestone", "")
    assert p.threads[0].milestones == [] and p.threads[0].next_milestone == ""


def test_thread_priority_rejects_non_numeric():
    p = _project()
    with pytest.raises(HTTPException) as ei:
        _apply_bible_path(p, "threads.1.priority", "高")
    assert ei.value.detail["code"] == "BAD_VALUE"


def test_index_out_of_range_is_a_400_not_a_crash():
    p = _project()
    for path in ("characters.9.name", "threads.9.name", "entries.9.content"):
        with pytest.raises(HTTPException) as ei:
            _apply_bible_path(p, path, "x")
        assert ei.value.status_code == 400
        assert ei.value.detail["code"] == "BAD_PATH"


def test_reference_samples_array_keeps_breaks_order_and_generation_limit():
    p = _project()
    p.title = ""
    p.style.diction = ["不要动"]
    p.style.punctuation_policy = {"dash": "keep"}
    p.npc_auto_act = True
    stored = ["第一段\n\n第二段，逗号。", "二", "三", "四"]
    result = _apply_bible_request(p, BiblePatch(reference_samples=["第一段\r\n\r\n第二段，逗号。", "  ", "二", "三", "四"]))
    assert p.style.reference_samples == stored
    assert result["event"]["reference_samples"] == stored
    assert result["reference_samples"]["old"] == []
    assert result["reference_samples"]["new"] == stored
    assert p.style.diction == ["不要动"]
    assert p.style.punctuation_policy == {"dash": "keep"}
    assert p.npc_auto_act is True
    assert "文风参考" in p.style.compiled_system_constraints
    layers = build_layered_context(p, None, [])
    assert "第一段\n\n第二段，逗号。" in layers["samples"]
    assert "\n---\n" in layers["samples"]
    assert "四" not in layers["samples"]


def test_sample_paths_conflict_changes_nothing():
    p = _project()
    p.style.reference_samples = ["旧范文"]
    p.style.diction = ["旧词"]
    body = BiblePatch(
        edits=[BibleEdit(path="style.reference_samples", new="新的一行")],
        reference_samples=["另一段"],
    )
    with pytest.raises(HTTPException) as ei:
        _apply_bible_request(p, body)
    assert ei.value.status_code == 400
    assert ei.value.detail["code"] == "SAMPLE_CONFLICT"
    assert p.style.reference_samples == ["旧范文"]
    assert p.style.diction == ["旧词"]


def test_sentence_edit_leaves_samples_and_hidden_fields():
    p = _project()
    p.style.reference_samples = ["甲\n\n乙", "二", "三", "四"]
    p.style.diction = ["文言"]
    p.style.punctuation_policy = {"dash": "keep"}
    p.npc_auto_act = True
    _apply_bible_request(p, BiblePatch(edits=[BibleEdit(path="style.sentence_length", new="terse")]))
    assert p.style.sentence_length == "terse"
    assert p.style.reference_samples == ["甲\n\n乙", "二", "三", "四"]
    assert p.style.diction == ["文言"]
    assert p.style.punctuation_policy == {"dash": "keep"}
    assert p.npc_auto_act is True
    assert "reference_samples" not in _apply_bible_request(
        p, BiblePatch(edits=[BibleEdit(path="style.sentence_length", new="terse")])
    )["event"]


def test_empty_lists_fall_back_to_builtin_defaults_when_read():
    p = _project()
    p.title = ""
    _apply_bible_request(p, BiblePatch(edits=[
        BibleEdit(path="style.banned_words", new=""),
        BibleEdit(path="style.preferred_replacements", new=""),
    ]))
    assert p.style.banned_words == []
    assert p.style.preferred_replacements == {}
    layers = build_layered_context(p, None, [])
    assert "仿佛" in layers["L0"]
    assert "心中一紧" in layers["L0"]


def test_empty_sample_array_clears_and_old_lines_still_split_on_newlines():
    p = _project()
    p.style.reference_samples = ["旧"]
    _apply_bible_request(p, BiblePatch(reference_samples=[]))
    assert p.style.reference_samples == []
    _apply_bible_path(p, "style.reference_samples", "雨还在下，杯子。\n第二段")
    assert p.style.reference_samples == ["雨还在下，杯子。", "第二段"]
