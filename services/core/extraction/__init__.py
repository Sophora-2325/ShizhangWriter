from __future__ import annotations

from ..context import assemble_messages, build_layered_context
from ..jsonutil import parse_model, safe_parse
from ..models import Beat, Chapter, ChapterPlan, ChapterSummary, Project, StateDelta
from ..providers import ChatRequest, model_for, resolve_for_project
from ..repository import add_usage

EXTRACT_PROMPT = """输入是本章定稿正文。只做提取，禁止创作。
输出严格 JSON：
{{
  "one_line": str,
  "beats": [{{"order":int,"what_happened":str,"who":[str],"consequence":str,"evidence":str}}],
  "state_changes": [str],
  "new_facts": [{{"fact":str,"known_by":[str]}}],
  "open_hooks": [str],
  "continuity_flags": [str],
  "protagonist_decision_from_user": bool|null
}}
禁令：不得引入正文中不存在的信息；不确定标 null。
每条 beat 的 evidence 必须是正文中**连续出现的逐字子串**，长度 1–40 字。
禁止拼接不相邻句、禁止改写/同义替换、禁止增删标点与引号（含「」）。
找不到合格片段时 evidence 填 ""。
计划（仅供对照，勿复述计划当事实）：{plan}
正文：
{body}
"""

EVIDENCE_RETRY_PROMPT = """上一次摘要里下列 beat 的 evidence 不是正文中的连续逐字片段。
请只输出完整 ChapterSummary JSON；重点修正这些 beat 的 evidence：
必须是正文中连续出现的逐字子串（1–40 字），禁止拼接、改写、改标点；找不到则填 ""。
失败项（含错误 evidence 供对照）：
{failed}
正文：
{body}
"""

_MAX_EV = 40
_MIN_REPAIR = 8
_HIT_THRESHOLD = 0.9


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
    """在 candidate 中找最长且出现在 body 里的连续子串。"""
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


def normalize_evidence(evidence: str, body: str) -> str:
    """把模型给的 evidence 收成正文中的逐字连续片段；做不到则返回空串。"""
    if not evidence or not body:
        return ""
    ev = _strip_wrap_quotes(evidence)[:_MAX_EV]
    if ev and ev in body:
        return ev
    repaired = _longest_body_substring(evidence, body)
    if repaired:
        return repaired
    return _longest_body_substring(_strip_wrap_quotes(evidence), body)


def _verify_evidence(
    summary: ChapterSummary, body: str, *, clear_failed: bool = True
) -> tuple[ChapterSummary, float, list[tuple[Beat, str]]]:
    """规范化每条 evidence。

    返回 (summary, 命中率, [(beat, 原始未通过的 evidence), ...])。
    命中率 = 非空且命中 / 曾给出非空 evidence 的 beat 数。
    clear_failed=False 时保留坏 evidence（供重试提示），只统计失败项。
    """
    hits = 0
    attempted = 0
    failed: list[tuple[Beat, str]] = []
    for b in summary.beats:
        raw = (b.evidence or "").strip()
        if not raw:
            b.evidence = ""
            continue
        attempted += 1
        fixed = normalize_evidence(raw, body)
        if fixed and fixed in body:
            b.evidence = fixed
            hits += 1
        else:
            failed.append((b, raw))
            if clear_failed:
                summary.continuity_flags.append(f"摘要证据未命中正文已清空: {raw[:20]}")
                b.evidence = ""
            else:
                b.evidence = raw
    rate = (hits / attempted) if attempted else 1.0
    if clear_failed and attempted and rate < 0.5:
        summary.continuity_flags.append(f"摘要忠实度偏低: {hits}/{attempted}")
    return summary, rate, failed


def _chat_extract(
    provider, hint: str, model: str, project_id: str, messages: list, temperature: float
) -> dict:
    req: ChatRequest = {
        "model": model,
        "messages": messages,  # type: ignore
        "temperature": temperature,
        "json_mode": True,
        "thinking": False,
        "purpose": "extract",
    }
    resp = provider.chat(req)
    add_usage(
        project_id,
        getattr(provider, "name", hint),
        model,
        resp.get("input_tokens", 0),
        resp.get("output_tokens", 0),
        resp.get("cached_tokens", 0),
        resp.get("cost_usd", 0),
        resp.get("latency_ms", 0),
        "extract",
    )
    return resp


def _parse_summary(
    provider,
    hint: str,
    model: str,
    project_id: str,
    messages: list,
    resp: dict,
    temperature: float,
) -> tuple[ChapterSummary, dict]:
    part, err = safe_parse(resp["content"], ChapterSummary)
    if part is not None:
        return part, resp
    messages = list(messages)
    messages.append({"role": "assistant", "content": resp["content"]})
    messages.append({"role": "user", "content": f"JSON 失败:{err}。只输出合法 JSON。"})
    resp2 = _chat_extract(provider, hint, model, project_id, messages, temperature)
    return parse_model(resp2["content"], ChapterSummary), resp2


def _retry_failed_evidence(
    summary: ChapterSummary,
    failed: list[tuple[Beat, str]],
    body: str,
    project: Project,
    plan: ChapterPlan | None,
    chapters: list[Chapter],
    provider,
    hint: str,
    model: str,
    target_chapter: int,
) -> tuple[ChapterSummary, dict]:
    failed_desc = "\n".join(
        f"- order={b.order} what_happened={b.what_happened!r} bad_evidence={raw!r}"
        for b, raw in failed
    )
    plan_s = plan.model_dump_json() if plan else "{}"
    task = EVIDENCE_RETRY_PROMPT.format(failed=failed_desc, body=body[:12000])
    layers = build_layered_context(project, plan, chapters, target_chapter=target_chapter)
    messages = assemble_messages(layers, task + f"\n计划对照：{plan_s[:400]}")
    resp = _chat_extract(provider, hint, model, project.id, messages, 0.1)
    part, resp = _parse_summary(provider, hint, model, project.id, messages, resp, 0.1)
    by_order = {b.order: b for b in part.beats}
    need = {b.order for b, _ in failed}
    for b in summary.beats:
        if b.order not in need:
            continue
        alt = by_order.get(b.order)
        if alt is None:
            continue
        fixed = normalize_evidence(alt.evidence or "", body)
        b.evidence = fixed if fixed and fixed in body else ""
    return summary, resp


def run_extract(
    project: Project,
    chapter: Chapter,
    plan: ChapterPlan | None,
    chapters: list[Chapter],
) -> tuple[ChapterSummary, StateDelta, dict]:
    body = chapter.body_text
    # segment by blank lines for long chapters — merge later
    chunks = [p for p in body.split("\n\n") if p.strip()]
    if len(chunks) > 12:
        mid = len(body) // 2
        segments = [body[:mid], body[mid:]]
    else:
        segments = [body]

    provider, hint = resolve_for_project(project)
    model = model_for("extract", hint)
    layers = build_layered_context(project, plan, chapters, target_chapter=chapter.no)
    all_beats: list[Beat] = []
    merged = ChapterSummary()
    last_resp: dict = {}

    for i, seg in enumerate(segments):
        plan_s = plan.model_dump_json() if plan else "{}"
        task = EXTRACT_PROMPT.format(plan=plan_s[:800], body=seg[:12000])
        messages = assemble_messages(layers, task)
        resp = _chat_extract(provider, hint, model, project.id, messages, 0.2)
        part, last_resp = _parse_summary(
            provider, hint, model, project.id, messages, resp, 0.2
        )
        for b in part.beats:
            b.order = len(all_beats) + 1
            all_beats.append(b)
        if i == 0:
            merged = part
        else:
            merged.state_changes.extend(part.state_changes)
            merged.new_facts.extend(part.new_facts)
            merged.open_hooks.extend(part.open_hooks)
            merged.continuity_flags.extend(part.continuity_flags)

    merged.beats = all_beats
    if not merged.one_line and all_beats:
        merged.one_line = all_beats[0].what_happened

    merged, rate, failed = _verify_evidence(merged, body, clear_failed=False)
    if rate < _HIT_THRESHOLD and failed:
        merged, last_resp = _retry_failed_evidence(
            merged, failed, body, project, plan, chapters, provider, hint, model, chapter.no
        )

    # 最终：再规范化一次，不合格清空并记 flag（不因 evidence 失败整章 abort）
    merged, _rate, _failed = _verify_evidence(merged, body, clear_failed=True)

    delta = StateDelta(
        facts_added=merged.new_facts,
        character_updates={},
        thread_updates={},
    )
    return merged, delta, last_resp
