"""Canonical author inputs, proposal payloads, merge previews and validity proofs."""
from __future__ import annotations

import hashlib
import json
from typing import Any

from .models import Chapter, ChapterPlan, ChapterProposal, Project
from .stacked_constraints import associate, compile_direction, semantic_data

POLICY_VERSION = "proposal-v3-1"


def digest(value: Any) -> str:
    def normalize(x):
        if hasattr(x, "model_dump"):
            x = x.model_dump(mode="json")
        if isinstance(x, dict):
            return {k: normalize(v) for k, v in x.items()}
        if isinstance(x, (list, tuple)):
            return [normalize(v) for v in x]
        return x.replace("\r\n", "\n").replace("\r", "\n") if isinstance(x, str) else x
    return hashlib.sha256(json.dumps(normalize(value), ensure_ascii=False, sort_keys=True,
                                     separators=(",", ":"), default=str).encode()).hexdigest()


def answers_data(proposal: ChapterProposal) -> list[dict]:
    return [*({"id": q.id, "question": q.question, "answer": q.answer,
               "related_field": q.related_field} for q in proposal.questions),
            *(d.model_dump(mode="json") for d in proposal.author_decisions)]


def payload_hash(proposal: ChapterProposal) -> str:
    return digest({"plot_outline": proposal.plot_outline, "proposed_plan": proposal.proposed_plan,
                   "questions": proposal.questions, "decisions": proposal.author_decisions})


def merge_plan(chapter: Chapter, proposal: ChapterProposal) -> ChapterPlan:
    """The sole merge rule for preview AND adoption; protected author fields stay current."""
    result = (chapter.plan or ChapterPlan()).model_copy(deep=True)
    patch = proposal.proposed_plan
    for name in ("scene_goal", "conflict", "stakes", "target_word_count"):
        value = getattr(patch, name)
        if value is not None:
            setattr(result, name, value)
    for name in ("must_include", "must_not", "present_character_ids", "required_threads", "scene_types"):
        setattr(result, name, list(dict.fromkeys([*getattr(result, name), *getattr(patch, name)])))
    # An unanswered open choice is never erased by a model's null/default.
    resolved = any(x["answer"].strip() and x.get("related_field") == "open_decision_for_user"
                   for x in answers_data(proposal))
    if result.open_decision_for_user:
        if resolved and "open_decision_for_user" in patch.model_fields_set:
            result.open_decision_for_user = patch.open_decision_for_user
    elif patch.open_decision_for_user:
        result.open_decision_for_user = patch.open_decision_for_user
    return associate(result, chapter.stacked_constraints)


def plan_diff(chapter: Chapter, proposal: ChapterProposal) -> list[dict]:
    before = (chapter.plan or ChapterPlan()).model_dump(mode="json")
    after = merge_plan(chapter, proposal).model_dump(mode="json")
    return [{"field": k, "before": before[k], "after": v} for k, v in after.items() if before[k] != v]


def with_plan(project: Project, chapter: Chapter, plan: ChapterPlan) -> Chapter:
    """Add only required association snapshots, exactly as the ordinary plan editor."""
    from .repository import project_lore
    result = chapter.model_copy(deep=True)
    result.plan = plan.model_copy(deep=True)
    lore = result.lore or project_lore(project)
    for name, ids in (("characters", plan.present_character_ids), ("threads", plan.required_threads)):
        members = getattr(lore, name)
        known = {item.id for item in members}
        members.extend(item.model_copy(deep=True) for item in getattr(project, name) if item.id in ids and item.id not in known)
    result.lore = lore
    return result


def effective_inputs(project: Project, chapter: Chapter, chapters: list[Chapter], *,
                     plan: ChapterPlan | None = None, raw_history: bool = False) -> dict:
    from .context import build_layered_context
    from .repository import lore_view
    from .skills import skills_prompt
    target = with_plan(project, chapter, plan) if plan is not None else chapter.model_copy(deep=True)
    target.plan = plan if plan is not None else chapter.plan
    # Adoption also adds the candidate's associated master records to the chapter.
    # Validate and fingerprint that exact future lore, not its pre-adoption snapshot.
    view = lore_view(project, target.lore)
    # Canonical layout-independent order for both the hash and the actual prompts.
    target.stacked_constraints.tracks.sort(key=lambda t: (t.domain, t.element_id))
    for t in target.stacked_constraints.tracks:
        t.segments.sort(key=lambda s: (s.start, s.end, s.directive))
    selected = [c for c in chapters if c.no != chapter.no] + [target]
    prev = next((c for c in chapters if c.no == chapter.no - 1), None)
    layers = build_layered_context(view, target.plan, selected, prev.refinement if prev else None,
                                   target_chapter=chapter.no, raw_history=raw_history, allow_remote_retrieval=False)
    manifest = {"policy": POLICY_VERSION, "chapter_uid": chapter.chapter_uid,
                "retrieval_policy": "local-bm25",
                "plan": target.plan.model_dump(mode="json") if target.plan else None,
                "rhythm": semantic_data(target.stacked_constraints),
                "actions": target.character_actions, "layers": layers,
                "skills_plan": skills_prompt("plan", target.plan.scene_types if target.plan else []),
                "skills_generate": skills_prompt("generate", target.plan.scene_types if target.plan else []),
                "memory_policy": "raw" if raw_history else "extract"}
    return {"manifest": manifest, "hash": digest(manifest), "layers": layers, "view": view}


def proposal_state(proposal: ChapterProposal | None, inputs: dict, adopted: bool = False) -> dict:
    if proposal is None:
        return {"state": "none", "reasons": []}
    reasons = []
    if proposal.bound_input_hash != inputs["hash"]:
        old = proposal.source_manifest
        current = inputs["manifest"]
        if old.get("plan") != current.get("plan"): reasons.append("plan_changed")
        if old.get("rhythm") != current.get("rhythm"): reasons.append("rhythm_changed")
        if old.get("actions") != current.get("actions"): reasons.append("actions_changed")
        if (old.get("layers") or {}).get("L1") != current["layers"]["L1"]: reasons.append("lore_changed")
        if (old.get("layers") or {}).get("L2") != current["layers"]["L2"] or (old.get("layers") or {}).get("L3") != current["layers"]["L3"]:
            reasons.append("history_changed")
        if not reasons: reasons.append("rules_or_skills_changed")
    if adopted:
        proof = proposal.validation
        if (not proof or proof.result != "passed" or proof.policy_version != POLICY_VERSION
                or proof.effective_input_hash != inputs["hash"] or proof.payload_hash != payload_hash(proposal)
                or proposal.adopted_at is None or proposal.final_plan is None
                or proposal.chapter_uid != inputs["manifest"]["chapter_uid"]
                or digest(proposal.final_plan) != digest(inputs["manifest"]["plan"])):
            reasons.append("validation_changed")
    return {"state": "stale" if reasons else ("adopted" if adopted else "draft"), "reasons": reasons}


def selected_adopted(project: Project, chapter: Chapter, chapters: list[Chapter], *, raw_history=False):
    if chapter.proposal.adopted is None:
        return None
    inputs = effective_inputs(project, chapter, chapters, raw_history=raw_history)
    adopted = chapter.proposal.adopted
    return adopted if proposal_state(adopted, inputs, adopted=True)["state"] == "adopted" else None


def compile_proposal(proposal: ChapterProposal) -> str:
    lines = ["【作者已采用的具体剧情方案】", proposal.plot_outline,
             "【作者回答与决定】（仅非空回答是明确授权；未答事项保持开放，不猜测关键选择）"]
    for item in answers_data(proposal):
        lines.append(f"问题：{item['question']}\n作者回答：{item['answer'] or '（未回答，保持开放）'}")
    return "\n".join(lines)


def check_sources(project: Project, chapter: Chapter, proposal: ChapterProposal, plan: ChapterPlan) -> dict[str, dict]:
    sources = {}
    for name, value in plan.model_dump().items():
        if isinstance(value, str) or name in ("must_include", "must_not"):
            text = "\n".join(value) if isinstance(value, list) else value
            if text: sources[f"plan.{name}"] = {"kind": "plan", "field": name, "text": text}
    # Also retain the original unresolved choice as a source to validate release.
    if chapter.plan and chapter.plan.open_decision_for_user:
        sources["original.open_decision_for_user"] = {"kind": "plan", "field": "open_decision_for_user",
                                                     "text": chapter.plan.open_decision_for_user}
    sources["proposal.plot_outline"] = {"kind": "proposal", "text": proposal.plot_outline}
    for t in chapter.stacked_constraints.tracks:
        for s in t.segments:
            sources[f"stack.{t.id}.{s.id}"] = {"kind": "stack", "track_id": t.id, "segment_id": s.id,
                "name": t.name or t.element_id, "start": s.start, "end": s.end,
                "text": s.directive or f"{t.name or t.element_id}在{s.start:g}%–{s.end:g}%出现或推进"}
    for item in answers_data(proposal):
        sources[f"answer.{item['id']}"] = {"kind": "answer", "question": item["question"],
            "text": item["answer"] if item["answer"].strip() else f"问题：{item['question']}\n作者回答：（未回答，保持开放）"}
    sources["policy.actor_choice"] = {"kind": "rule", "text": "系统绝不替主角做选择；需要决策时写成开放情境并停止推进该决策。"}
    for entry in project.entries:
        if entry.category == "rule" and entry.hard:
            sources[f"rule.{entry.id}"] = {"kind": "rule", "text": entry.content}
    for i, text in enumerate(project.forbidden):
        sources[f"forbidden.{i}"] = {"kind": "rule", "text": text}
    for cid, text in chapter.character_actions.items():
        if text.strip(): sources[f"action.{cid}"] = {"kind": "action", "text": text}
    return sources
