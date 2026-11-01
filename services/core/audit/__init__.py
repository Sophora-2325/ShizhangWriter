from __future__ import annotations

import re

from ..models import (
    Chapter,
    ConsistencyFlag,
    ConsistencyReport,
    Project,
)
from ..providers import ChatRequest, model_for, resolve_for_project, thinking_for
from ..repository import add_usage
from ..jsonutil import safe_parse
from ..models import ChapterPlan
from ..stacked_constraints import compile_constraints, compile_direction
from pydantic import BaseModel, Field

_MAX_EV = 80
_MIN_REPAIR = 8


class AuditLLMOut(BaseModel):
    flags: list[ConsistencyFlag] = Field(default_factory=list)
    passed: bool = True


def _strip_wrap_quotes(s: str) -> str:
    s = s.strip()
    pairs = [('"', '"'), ("'", "'"), ("「", "」"), ("“", "”"), ("‘", "’")]
    changed = True
    while changed and len(s) >= 2:
        changed = False
        for a, b in pairs:
            if s.startswith(a) and s.endswith(b):
                s = s[len(a) : -len(b)].strip()
                changed = True
                break
    return s


def _longest_body_substring(
    candidate: str, body: str, *, min_len: int = _MIN_REPAIR, max_len: int = _MAX_EV
) -> str:
    text = candidate.strip()
    if not text or not body:
        return ""
    upper = min(len(text), max_len)
    for length in range(upper, min_len - 1, -1):
        for i in range(0, len(text) - length + 1):
            frag = text[i : i + length]
            if frag in body:
                return frag
    return ""


def normalize_audit_evidence(evidence: str, body: str) -> str:
    """把 semantic flag 的 evidence 收成正文逐字连续片段；做不到返回空串。"""
    if not evidence or not body:
        return ""
    ev = _strip_wrap_quotes(evidence)[:_MAX_EV]
    if ev and ev in body:
        return ev
    repaired = _longest_body_substring(evidence, body)
    if repaired:
        return repaired
    return _longest_body_substring(_strip_wrap_quotes(evidence), body)


def gate_semantic_flags(flags: list[ConsistencyFlag], body: str) -> list[ConsistencyFlag]:
    """对 semantic flag：有合格原文依据则保留 severity，否则降为 info。"""
    out: list[ConsistencyFlag] = []
    for f in flags:
        if f.kind != "semantic":
            out.append(f)
            continue
        fixed = normalize_audit_evidence(f.evidence or "", body)
        if fixed and fixed in body:
            f.evidence = fixed
            out.append(f)
            continue
        # 无合格原文依据：不得以 warn/fail 吓人
        point = f.point or ""
        if not point.startswith("无原文依据"):
            point = f"无原文依据：{point}" if point else "无原文依据"
        out.append(
            ConsistencyFlag(
                kind="semantic",
                severity="info",
                point=point,
                evidence="",
                suggestion=f.suggestion or "",
            )
        )
    return out


def _rule_checks(project: Project, body: str) -> list[ConsistencyFlag]:
    flags: list[ConsistencyFlag] = []
    for word in project.forbidden:
        if word and word in body:
            flags.append(
                ConsistencyFlag(
                    kind="rule",
                    severity="fail",
                    point=f"正文出现禁忌词：{word}",
                    evidence=word,
                    suggestion="删除或改写该处",
                )
            )
    # POV / tense soft hints
    if project.style.pov.startswith("third") and re.search(r"(?<![A-Za-z])我(?![们A-Za-z])", body):
        # crude: many third-person novels still have dialogue 我
        pass
    return flags


def _state_checks(project: Project, body: str, chapters: list[Chapter]) -> list[ConsistencyFlag]:
    flags: list[ConsistencyFlag] = []
    for c in project.characters:
        status = c.status or {}
        if status.get("dead") or status.get("status") == "dead":
            # if name speaks after death
            pattern = rf"{re.escape(c.name)}.{{0,8}}(说|道|问|笑|喊)"
            if re.search(pattern, body):
                flags.append(
                    ConsistencyFlag(
                        kind="state",
                        severity="fail",
                        point=f"角色 {c.name} 已标记死亡却仍有言行",
                        evidence=c.name,
                        suggestion="修正状态或删改本章言行",
                    )
                )
        # secret leak: if secret phrase appears near other characters' knowledge — soft
        if c.secret and len(c.secret) > 4 and c.secret in body:
            # only flag if known_facts empty for others — informational
            flags.append(
                ConsistencyFlag(
                    kind="state",
                    severity="info",
                    point=f"正文出现 {c.name} 的秘密表述，请确认知悉范围",
                    evidence=c.secret[:40],
                    suggestion="核对 known_facts",
                )
            )
    # thread must_not advance check via chapter plan
    return flags


SEMANTIC_AUDIT_PROMPT = (
    # 注意：本模板会走 str.format()，里面的**字面量 JSON 花括号必须写成 {{ }}**，
    # 否则 format 会把 {"flags": ... 当成替换字段，抛 KeyError '"flags"' → 接口 500。
    # 回归保护见 tests/test_prompt_templates_format_safe.py。
    "检查正文与设定是否冲突。只报告有原文依据的真实冲突。\n"
    "输出 JSON："
    '{{"flags":[{{"kind":"semantic","severity":"info|warn|fail","point":str,'
    '"evidence":str,"suggestion":str}}],"passed":bool}}\n'
    "规则：\n"
    "- evidence 必须是正文中连续出现的逐字子串（1–80 字）；禁止改写、拼接、概括。\n"
    "- 找不到合格原文片段则不要输出该条；若仍要提示，severity 只能为 info 且 evidence 填 \"\"。\n"
    "- 禁止把「未做的动作 / 禁忌 / 习惯 / 偏好」推断成「能力丧失」或「与动作场面矛盾」"
    "（例如：不敢解刀鞘结 ≠ 不能用刀；习惯待在肩上 ≠ 不能落地）。\n"
    "- 不要替作者挑措辞；只标设定与正文的硬冲突。\n"
    "设定：\n{bible}\n正文：\n{body}"
)


def run_audit(
    project: Project,
    chapter: Chapter,
    chapters: list[Chapter],
    semantic: bool = False,
) -> ConsistencyReport:
    body = chapter.body_text or ""
    flags = _rule_checks(project, body) + _state_checks(project, body, chapters)

    if semantic and body:
        provider, hint = resolve_for_project(project)
        model = model_for("audit", hint)
        bible_snip = "\n".join(
            f"{c.name}: secret={c.secret}; status={c.status}" for c in project.characters
        )
        prompt = SEMANTIC_AUDIT_PROMPT.format(bible=bible_snip, body=body[:8000])
        req: ChatRequest = {
            "model": model,
            "messages": [
                {
                    "role": "system",
                    "content": "你是一致性审计员，只找有原文依据的冲突，不改写正文，不做过度推断。",
                },
                {"role": "user", "content": prompt},
            ],
            "temperature": 0.2,
            "json_mode": True,
            "thinking": thinking_for(hint),
            "purpose": "audit",
        }
        resp = provider.chat(req)
        add_usage(
            project.id,
            getattr(provider, "name", hint),
            model,
            resp.get("input_tokens", 0),
            resp.get("output_tokens", 0),
            resp.get("cached_tokens", 0),
            resp.get("cost_usd", 0),
            resp.get("latency_ms", 0),
            "audit",
        )
        out, _ = safe_parse(resp["content"], AuditLLMOut)
        if out:
            for f in out.flags:
                if not f.kind:
                    f.kind = "semantic"
            flags.extend(gate_semantic_flags(out.flags, body))

    passed = not any(f.severity == "fail" for f in flags)
    # semantic_checked 表示「语义那一趟真的跑过」：正文为空时上面会跳过模型调用，
    # 那种情况不能算查过，否则界面会把「没查」显示成「查过且干净」。
    return ConsistencyReport(
        flags=flags, passed=passed, semantic_checked=semantic and bool(body)
    )


# ---------------------------------------------------------------- 本章校验
#
# 与 run_audit 的分工：
#   审计 = 正文与「设定」有没有冲突（每章都该关心）
#   校验 = 「本章计划」里对用户许下的约束有没有被照做（must_include / must_not / required_threads）
# 两者结果分开存（Chapter.consistency_report / Chapter.plan_check），互不覆盖。
#
# 这里同时取代了两处旧死代码：run_audit 里那个只会 `pass` 的 must_not 循环，
# 以及定义了却从未被调用的 assert_thread_not_advanced()——它比较的是 thread.state
# 这个数据字段，而模型写正文根本不会改它，所以那条路本身就走不通。

PLAN_CHECK_PROMPT = (
    "下面是本章的计划要求与已经写好的正文。请逐条核对正文有没有照做这些要求。\n"
    "输出 JSON："
    '{{"flags":[{{"kind":"semantic","severity":"info|warn|fail","point":str,'
    '"evidence":str,"suggestion":str}}],"passed":bool}}\n'
    "规则：\n"
    "- evidence 必须是正文中连续出现的逐字子串（1–80 字）；禁止改写、拼接、概括。\n"
    "- 找不到合格原文片段就不要输出该条；若仍要提示，severity 只能为 info 且 evidence 填 \"\"。\n"
    "- 计划里的「必须写到」如果正文换了别的方式达成同等效果，算通过，不要判违规。\n"
    "- 不要挑措辞、不要评情节优劣；只核对「照做了没有」。\n"
    "- 计划禁止的事在正文里发生了要报 warn；计划要求的事在正文里找不到才报 warn。\n"
    "计划「必须写到」：{must_include}\n"
    "计划「禁止写到」：{must_not}\n"
    "融合创作方案：{direction}\n"
    "世界硬规则与禁忌：{hard_rules}\n"
    "叠条核对走向、先后、同区间剧情关联与同领域侧重；百分比不是精确字数。"
    "叠条安排节奏，本章规划补充事件与细节，共同核对走向、先后、参与时机、关联与具体要求；"
    "未覆盖区间可合理承接。硬规则、禁忌、明确禁止项须遵守；矛盾需明确报告，不能按权重舍弃走向。\n"
    "正文：\n{body}"
)


def run_plan_check(
    project: Project,
    chapter: Chapter,
    chapters: list[Chapter],
    semantic: bool = True,
) -> ConsistencyReport:
    """核对本章计划的约束是否被正文照做；结果存 Chapter.plan_check。

    两层：脉络推进与字数在本地核对（免费、确定）；must_include / must_not 是自由文本，
    只能交给模型对着正文判断，并且同样过证据门禁（没有逐字依据就降级为 info）。
    """
    body = chapter.body_text or ""
    plan = chapter.plan
    from ..proposal_inputs import selected_adopted, compile_proposal
    adopted = selected_adopted(project, chapter, chapters)
    if adopted is not None:
        plan = adopted.final_plan
    stacked = compile_constraints(chapter.stacked_constraints, project)
    flags: list[ConsistencyFlag] = []

    if plan is None and not stacked:
        return ConsistencyReport(flags=[], passed=True, semantic_checked=False)
    plan = plan or ChapterPlan(target_word_count=0)

    # ① 「必须推进」的脉络：看提取摘要产出的 state_delta 里有没有这些 id
    if plan.required_threads:
        moved = set((chapter.state_delta.thread_updates or {}).keys()) if chapter.state_delta else set()
        known = {t.id for t in chapter.lore.threads} if chapter.lore else set()
        known = known or {t.id for t in project.threads}
        for tid in plan.required_threads:
            if tid not in known:
                flags.append(
                    ConsistencyFlag(
                        kind="plan",
                        severity="info",
                        point=f"计划要求推进的脉络「{tid}」不在本章设定里",
                        suggestion="检查规划区里选中的脉络",
                    )
                )
            elif chapter.state_delta is None:
                flags.append(
                    ConsistencyFlag(
                        kind="plan",
                        severity="info",
                        point=f"还没提取摘要，无法核对脉络「{tid}」有没有被推进",
                        suggestion="先运行「本章摘要」再校验",
                    )
                )
            elif tid not in moved:
                flags.append(
                    ConsistencyFlag(
                        kind="plan",
                        severity="warn",
                        point=f"计划要求推进脉络「{tid}」，但正文没有让它发生变化",
                        suggestion="确认这一章是否真的推进了这条线；确实不推进就改计划",
                    )
                )

    # ② 目标字数：本地核对
    target = plan.target_word_count or 0
    if target and body:
        got = len(body)
        if got < target * 0.7 or got > target * 1.45:
            flags.append(
                ConsistencyFlag(
                    kind="plan",
                    severity="info",
                    point=f"计划目标约 {target} 字，正文实际 {got} 字",
                    suggestion="",
                )
            )

    # ③ 自由文本的「必须写到 / 禁止写到」：交给模型，过证据门禁后再把 kind 改成 plan
    asked_model = bool(adopted or plan.must_include or plan.must_not or stacked or any(
        text and text.strip() for text in [plan.free_text, plan.misc, plan.scene_goal,
                                          plan.conflict, plan.stakes, plan.open_decision_for_user]))
    if stacked and (not semantic or not body):
        flags.append(ConsistencyFlag(kind="plan", severity="info", point="叠条走向尚未进行语义校验",
                                     suggestion="有正文后运行本章校验，并开启语义检查"))
    if semantic and body and asked_model:
        provider, hint = resolve_for_project(project)
        model = model_for("audit", hint)
        prompt = PLAN_CHECK_PROMPT.format(
            must_include="；".join(plan.must_include) or "（无）",
            must_not="；".join(plan.must_not) or "（无）",
            direction=compile_direction(plan, chapter.stacked_constraints, project)
                      + ("\n" + compile_proposal(adopted) if adopted else ""),
            hard_rules="；".join([*(e.content for e in project.entries if e.category == "rule" and e.hard), *project.forbidden]) or "（无）",
            body=body,
        )
        req: ChatRequest = {
            "model": model,
            "messages": [
                {
                    "role": "system",
                    "content": "核对正文是否落实融合方案中的节奏安排和具体要求，只依据原文，不评价文笔。",
                },
                {"role": "user", "content": prompt},
            ],
            "temperature": 0.2,
            "json_mode": True,
            "thinking": thinking_for(hint),
            "purpose": "audit",
        }
        resp = provider.chat(req)
        add_usage(
            project.id,
            getattr(provider, "name", hint),
            model,
            resp.get("input_tokens", 0),
            resp.get("output_tokens", 0),
            resp.get("cached_tokens", 0),
            resp.get("cost_usd", 0),
            resp.get("latency_ms", 0),
            "audit",
        )
        out, _ = safe_parse(resp["content"], AuditLLMOut)
        if out:
            # 先过证据门禁（它只处理 kind == "semantic" 的条目），再打上 plan 标签
            for f in gate_semantic_flags(out.flags, body):
                f.kind = "plan"
                flags.append(f)
        else:
            flags.append(ConsistencyFlag(kind="plan", severity="info", point="本章语义校验未完成，模型未返回有效结果",
                                         suggestion="请重试本章校验"))

    passed = not any(f.severity == "fail" for f in flags)
    return ConsistencyReport(
        flags=flags,
        passed=passed,
        semantic_checked=bool(semantic and body and asked_model and out is not None),
    )
