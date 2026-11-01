from __future__ import annotations

from typing import Any

from ..models import (
    LORE_CATEGORIES,
    LORE_CATEGORY_LABELS,
    Chapter,
    ChapterPlan,
    Project,
    Refinement,
)
from ..repository import search_facts
from ..skills import skills_prompt
from ..freshness import summary_valid
from ..stacked_constraints import compile_direction


DEFAULT_BANNED = [
    "仿佛", "宛如", "似乎", "渐渐地", "不知不觉", "不由得", "不禁",
    "莫名地", "五味杂陈", "百感交集", "思绪万千", "心中一紧", "心中一沉",
    "瞳孔骤缩", "呼吸一滞", "嘴角勾起一抹弧度", "眼底闪过一丝",
    "片刻之后", "良久", "许久",
]


def build_layered_context(
    project: Project,
    plan: ChapterPlan | None,
    chapters: list[Chapter],
    refinement: Refinement | None = None,
    query: str = "",
    target_chapter: int | None = None,
    raw_history: bool = False,
    note_reference: str = "",
    allow_remote_retrieval: bool = True,
) -> dict[str, str]:
    """Return L0–L3 prompt segments. L0 must stay byte-stable for caching."""
    boundary = target_chapter if target_chapter is not None else max((c.no for c in chapters if c.status.value != "superseded"), default=0) + 1
    # 目标章可能就在 chapters 里（重规划/生成/提取时）；只取这一章的行动，绝不读别的章。
    target = next(
        (c for c in chapters if c.no == boundary and c.status.value != "superseded"), None
    )
    chapters = [c for c in chapters if 0 < c.no < boundary and c.status.value != "superseded"]
    style = project.style
    banned = style.banned_words or DEFAULT_BANNED
    replacements = style.preferred_replacements or {
        "心中一紧": "写手的具体动作",
        "仿佛/宛如": "直接写本体，不用比喻",
        "五味杂陈": "删掉，用一个具体念头替代",
        "渐渐地/不知不觉": "删掉，或写时间标记",
    }

    # L0 — stable prefix
    hard_rules = [
        e.content for e in project.entries if e.category == "rule" and e.hard and e.content.strip()
    ]
    l0 = "\n".join(
        [
            "【硬规则·风格】",
            style.compiled_system_constraints
            or f"人称:{style.pov}; 时态:{style.tense}",
            "【硬规则·世界】",
            "\n".join(f"- {r}" for r in hard_rules) or "- （无）",
            "【禁忌】",
            "\n".join(f"- {f}" for f in project.forbidden) or "- （无）",
            "【禁用表达 P0】",
            "、".join(banned[:20]),
            "【替换对照】",
            "；".join(f"{k}→{v}" for k, v in list(replacements.items())[:15]),
            "【行动者规则】",
            "系统绝不替主角做选择；需要决策时写成开放情境并停止推进该决策。",
        ]
    )

    # L1 Bible（硬规则已进 L0，这里不再重复）
    entry_lines: list[str] = []
    for cat in LORE_CATEGORIES:
        group = [
            e
            for e in project.entries
            if e.category == cat and e.content.strip() and not (cat == "rule" and e.hard)
        ]
        if not group:
            continue
        entry_lines.append(f"{LORE_CATEGORY_LABELS[cat]}：")
        for e in group:
            entry_lines.append(f"- {e.title}：{e.content}" if e.title.strip() else f"- {e.content}")
    chars = []
    for c in project.characters:
        rel = "；".join(
            f"{r.label or r.type}->{r.target_id}" for r in (c.relations or [])
        ) or "无"
        goals = "、".join(c.goals) if c.goals else "无"
        chars.append(
            f"- {c.name}({c.id}) 年龄={c.age or '?'}；性别={c.gender or '未填'}；"
            f"性格={c.personality or '未填'}；爱好={c.hobbies or '无'}；"
            f"所属组织={c.organization or '无'}；思维习惯={c.thinking_habits or '无'}；"
            f"相貌={c.appearance or '未填'}；着装={c.outfit or '未填'}；"
            f"最近所想={c.recent_thoughts or '未填'}；"
            f"身份={c.role or '未填'}；长久目标={goals}；出场原因={c.join_reason or '无'}；"
            f"关系={rel}；声口={c.voice or '无'}；状态={c.status or {}}；"
            f"秘密（仅作者知道，不代表其他人物知情）={c.secret or '无'}；已知事实={c.known_facts}"
        )
    threads = []
    for t in project.threads:
        nodes = "；".join(
            f"{i + 1}. {m.name}（作者标记：{'完成' if m.completed else '未完成'}）{m.description}"
            for i, m in enumerate(t.milestones)
        ) or "无"
        threads.append(
            f"- [{t.id}] {t.name}/{t.kind} state={t.state} "
            f"故事时间位置={t.time_position}（过去/现在/未来，相对于当前故事时间，不是语法时态）；"
            f"发展脉络={t.user_directive}；脉络基调={t.tone}；"
            f"有序里程碑={nodes}；下一节点={t.next_milestone}"
            + (f"；脉络补充说明（作者意图，不自动视为已发生事实）={t.misc}" if t.misc.strip() else "")
        )
    l1 = "\n".join(
        [
            f"标题：{project.title}",
            "【背景设定】",
            "\n".join(entry_lines) or "- （无）",
            "角色：",
            "\n".join(chars) or "- （无）",
            "脉络：",
            "脉络的未来节点与发展方向是作者计划，不是已发生事实；完成标记仅为作者记录，"
            "不能据此杜撰历史事实。不同脉络可处于不同故事时间，切换时应明确时间关系。",
            "\n".join(threads) or "- （无）",
        ]
    )

    # L2 mid window: 首章摘要（起点）+ 最近 3 章摘要 + 上一章结尾
    finals = sorted(
        (c for c in chapters if c.status.value in ("final", "drafted") and c.no > 0),
        key=lambda c: c.no,
    )

    def _one_line(c: Chapter) -> str:
        if raw_history:
            return c.body_text if len(c.body_text) <= 6000 else c.body_text[:3000] + "\n（中段省略）\n" + c.body_text[-3000:]
        if summary_valid(c) and c.summary.one_line:
            return c.summary.one_line
        return f"{c.body_text[:120]}…" if c.body_text else ""

    picked = finals[-3:]
    if finals and (not picked or picked[0].no != finals[0].no):
        picked = [finals[0], *picked]  # 首章摘要是「故事从哪开始」的锚点
    summary_bits = [f"第{c.no}章：{_one_line(c)}" for c in picked if _one_line(c)]
    prev_ending = ""
    if finals and finals[-1].body_text:
        prev_ending = finals[-1].body_text[-300:]
    l2 = "\n".join(
        [
            "近章原文（按预算截取）：" if raw_history else "近章摘要：",
            "\n".join(summary_bits) or "- （尚无）",
            "上一章结尾：",
            prev_ending or "（尚无）",
        ]
    )

    # L3 retrieved facts
    first_summary = _one_line(finals[0]) if finals else ""
    last_summary = _one_line(finals[-1]) if finals else ""
    q = next(
        (
            x.strip()
            for x in [
                query,
                plan.scene_goal if plan else "",
                plan.free_text if plan else "",
                first_summary,
                last_summary,
                project.title,
            ]
            if x and x.strip()
        ),
        "",
    )
    facts = search_facts(project.id, q, limit=8, before_chapter=boundary,
                         character_ids=plan.present_character_ids if plan else None,
                         allow_remote=allow_remote_retrieval) if q else []
    l3 = "相关历史事实（按检索）：\n" + (
        "\n".join(f"- [ch{f['chapter_no']}] {f['fact']}；知情者={f['known_by'] or '未注明，不能假定所有人知道'}" for f in facts) or "- （无）"
    )

    # Refinement / plan short
    extra = ""
    if refinement:
        extra += "\n"
        if refinement.next_chapter_intent.strip():
            extra += f"用户细修意图：{refinement.next_chapter_intent}\n"
        extra += f"评注：{refinement.user_annotations}\n"
        if refinement.constraints_for_next:
            extra += "下一章约束：" + "；".join(refinement.constraints_for_next)
    # Planning also reads the target's existing details, as a supplement to its rhythm.
    from ..models import StackedConstraints
    direction = compile_direction(plan or (target.plan if target else None),
                                  target.stacked_constraints if target else StackedConstraints(), project)
    if direction:
        extra += "\n" + direction
    # 目标章的「本章行动」只属于这一章的作者计划上下文：是待执行意图，不是已经发生的事实。
    # 其他章节的行动一律不读取。
    if target is not None and target.character_actions:
        names = {c.id: c.name for c in project.characters}
        action_lines = [
            f"- {names.get(cid) or cid}({cid})：{str(text).strip()}"
            for cid, text in target.character_actions.items()
            if str(text).strip()
        ]
        if action_lines:
            extra += (
                "\n本章角色行动（作者为本章手写的待执行意图，不是已发生事实，"
                "不得写成已经发生/完成；只适用于本章）：\n" + "\n".join(action_lines)
            )

    # note_reference 参数只为旧客户端兼容保留。个人笔记不参与 AI 创作。

    # Reference samples near generation (L2-ish weight)
    samples = ""
    if style.reference_samples:
        samples = "文风范文：\n" + "\n---\n".join(style.reference_samples[:3])

    return {
        "L0": l0,
        "L1": l1,
        "L2": l2,
        "L3": l3,
        "extra": extra,
        "samples": samples,
    }


def assemble_messages(
    layers: dict[str, str],
    task_prompt: str,
    prefix: str = "",
    purpose: str | None = None,
    scenes: list[str] | None = None,
) -> list[dict[str, Any]]:
    """purpose 为 plan / generate 时，把已启用、适用于该用途且与本章戏份 scenes 匹配的写作 Skills 追加到 system 消息末尾。"""
    system = layers["L0"] + "\n\n" + layers["L1"]
    if purpose:
        skill_text = skills_prompt(purpose, scenes)
        if skill_text:
            system += "\n\n" + skill_text
    user = "\n\n".join(
        x for x in [layers["L2"], layers["L3"], layers["extra"], layers["samples"], task_prompt] if x
    )
    messages: list[dict[str, Any]] = [
        {"role": "system", "content": system},
        {"role": "user", "content": user},
    ]
    if prefix:
        messages.append({"role": "assistant", "content": prefix, "prefix": True})
    return messages
