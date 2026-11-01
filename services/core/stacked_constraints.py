"""Canonical interval compiler. Vertical placement and insertion order have no semantic effect."""
from __future__ import annotations

from copy import deepcopy

from .models import ChapterPlan, Project, StackedConstraints

DOMAIN_NAMES = {"thread": "脉络", "character": "人物", "scene": "戏份"}


def normalize_stored_chapter(data: dict) -> tuple[dict, bool]:
    """Compatibility at storage boundaries only; API submissions remain strict."""
    data = deepcopy(data)
    changed = False
    for track in (data.get("stacked_constraints") or {}).get("tracks", []):
        weight = track.get("weight", 0.5)
        if isinstance(weight, (int, float)) and not isinstance(weight, bool) and 0 <= weight < 0.1:
            track["weight"] = 0.1
            changed = True
    if changed:
        data["stacked_weight_migration_pending"] = True
        data["plan_check"] = None
        data.get("derived_hashes", {}).pop("plan_check", None)
    return data, changed


def semantic_data(value: StackedConstraints) -> list[dict]:
    return sorted([
        {"domain": t.domain, "element_id": t.element_id, "weight": t.weight,
         "segments": sorted([{"start": s.start, "end": s.end, "directive": s.directive}
                             for s in t.segments], key=lambda s: (s["start"], s["end"], s["directive"]))}
        for t in value.tracks if t.segments
    ], key=lambda t: (t["domain"], t["element_id"]))


def associate(plan: ChapterPlan, value: StackedConstraints) -> ChapterPlan:
    """Keep author-selected elements when the model replaces a plan."""
    for track in value.tracks:
        selected = {"thread": plan.required_threads, "character": plan.present_character_ids,
                    "scene": plan.scene_types}[track.domain]
        if track.element_id not in selected:
            selected.append(track.element_id)
    return plan


def intervals(value: StackedConstraints) -> list[tuple[float, float, list[tuple]]]:
    segments = [(t, s) for t in value.tracks for s in t.segments]
    boundaries = sorted({x for _, s in segments for x in (s.start, s.end)})
    return [(a, b, [(t, s) for t, s in segments if s.start < b and s.end > a])
            for a, b in zip(boundaries, boundaries[1:])
            if any(s.start < b and s.end > a for _, s in segments)]


def compile_constraints(value: StackedConstraints, project: Project | None = None) -> str:
    slices = intervals(value)
    if not slices:
        return ""
    names = {} if project is None else {
        **{("character", c.id): c.name for c in project.characters},
        **{("thread", t.id): t.name for t in project.threads},
    }
    lines = ["【本章叠条约束·作者待执行意图】",
             "进度是本章叙事先后，不是精确字数。每段走向必须遵循，权重仅决定同领域的笔墨侧重；"
             "权重范围0.1–1，同权重平等。不同领域不比较权重。",
             "叠条定义三元素的参与时机、发展顺序、重叠关联和描写侧重，构成本章节奏骨架。"
             "本章规划在此骨架上补充具体事件、冲突、动机、表现方式和承接内容，两者共同组成一份创作方案。"
             "未覆盖区间允许合理承接，不得擅自改变已安排的节奏。"
             "世界硬规则、禁忌和明确禁止项仍须遵守；明确矛盾须请作者调整，不可按权重静默舍弃或重排作者内容。",
             "同一区间内列出的元素在剧情上相关，但不自动构成因果或人物知情；"
             "不同区间不传递关联，不把计划当作已发生事实。",
             "续写保留已有正文，结合已发生部分继续推进，不重复插入前段事件。"]
    for a, b, active in slices:
        lines.append(f"本章进度 {a:g}%–{b:g}%（此区间相关元素）：")
        for t, s in active:
            default = "在此区间出现并按作者安排推进"
            name = names.get((t.domain, t.element_id)) or t.name or t.element_id
            lines.append(f"- {DOMAIN_NAMES[t.domain]}「{name}」({t.element_id})；"
                         f"权重={t.weight:g}；片段 {s.start:g}%–{s.end:g}%：{s.directive.strip() or default}")
    return "\n".join(lines)


def compile_direction(plan: ChapterPlan | None, value: StackedConstraints,
                      project: Project | None = None) -> str:
    """One authoring scheme: rhythm, concrete content, then writing requirements."""
    rhythm = compile_constraints(value, project)
    if not plan and not rhythm:
        return ""
    lines = ["【本章融合创作方案】", "一、节奏骨架", rhythm or "（未安排叠条，按本章规划自然发展）",
             "二、具体内容（在节奏骨架内展开，不改写作者原文）"]
    if plan:
        lines.append(f"本章计划：goal={plan.scene_goal}; conflict={plan.conflict}; stakes={plan.stakes}; "
                     f"characters={plan.present_character_ids}; threads={plan.required_threads}; scene_types={plan.scene_types}")
        if plan.free_text.strip():
            lines.append("用户自由规划：\n" + plan.free_text.strip())
        if plan.misc.strip():
            lines.append("本章补充说明：\n" + plan.misc.strip())
    else:
        lines.append("（具体内容待补充，请沿骨架规划事件与过渡）")
    lines.append("三、写作要求")
    if plan:
        lines.extend([f"必须写到：{plan.must_include}", f"禁止写到：{plan.must_not}",
                      f"目标字数：{plan.target_word_count}"])
        if plan.open_decision_for_user:
            lines.append("待主角（用户）决策，不得替其决定：" + plan.open_decision_for_user)
    lines.append("规划沿节奏骨架补充细节；生成和续写共同落实节奏与具体要求。"
                 "续写结合已有正文继续推进，不重复补插前段事件；遗漏由本章校验提示。")
    return "\n".join(lines)
