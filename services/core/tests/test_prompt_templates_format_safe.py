"""所有走 str.format() 的提示词模板必须能安全格式化。

背景：`SEMANTIC_AUDIT_PROMPT` 里嵌入了一段**字面量 JSON 契约**，未转义的花括号会被
`str.format()` 当成替换字段，抛出 `KeyError: '"flags"'`，导致
`POST /project/{id}/audit/{n}?semantic=true` 直接返回 500。

这里把「模板能被格式化」固化成回归保护：以后任何人往这些模板里加 JSON 示例、
JSON Schema 或任何带花括号的示例文本，只要忘了写成 `{{ }}`，测试立刻失败。
"""

from __future__ import annotations

import sys
from collections import defaultdict
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from services.core import audit, extraction, generation, skill_sources
from services.core.providers import base as providers_base


class _AutoEmpty(defaultdict):
    """任意占位符都返回空串，因此不必逐个模板维护真实参数表。"""

    def __init__(self) -> None:
        super().__init__(str)

    def __missing__(self, key):
        return ""


TEMPLATES = [
    (audit, "SEMANTIC_AUDIT_PROMPT"),
    (audit, "PLAN_CHECK_PROMPT"),
    (extraction, "EXTRACT_PROMPT"),
    (extraction, "EVIDENCE_RETRY_PROMPT"),
    (generation, "PLAN_PROMPT"),
    (generation, "GENERATE_PROMPT"),
    (providers_base, "CONTINUE_INSTRUCTION"),
    (skill_sources, "DISTILL_PROMPT"),
]

TEMPLATE_IDS = [f"{mod.__name__}.{name}" for mod, name in TEMPLATES]


@pytest.mark.parametrize(("mod", "name"), TEMPLATES, ids=TEMPLATE_IDS)
def test_prompt_template_is_format_safe(mod, name):
    """未转义的字面量花括号会在这里变成 KeyError / ValueError。"""
    getattr(mod, name).format_map(_AutoEmpty())


def test_semantic_audit_prompt_uses_real_call_style():
    """复刻真实调用点（audit/__init__.py 的 .format(bible=…, body=…)）。"""
    out = audit.SEMANTIC_AUDIT_PROMPT.format(bible="<BIBLE>", body="<BODY>")
    assert "<BIBLE>" in out
    assert "<BODY>" in out


def test_semantic_audit_prompt_keeps_literal_json_contract():
    """花括号应当是「转义后仍是字面量」，契约内容不能被吃掉。"""
    out = audit.SEMANTIC_AUDIT_PROMPT.format(bible="<B>", body="<P>")
    assert '{"flags":[{"kind":"semantic"' in out
    assert '"suggestion":str}]' in out
    assert '],"passed":bool}' in out
