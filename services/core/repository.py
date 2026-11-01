from __future__ import annotations

import os
import sqlite3
from datetime import datetime
from functools import wraps
from pathlib import Path
from threading import RLock
from typing import Any, Optional

from fastapi import HTTPException

from . import workspace as ws
from .db import (
    append_event,
    chapter_path,
    connect,
    dumps,
    loads,
    new_id,
    summary_path,
    utcnow,
)
from .freshness import RESULTS, body_hash, invalidate, stamp
from .stacked_constraints import normalize_stored_chapter
from .models import (
    LORE_CATEGORIES,
    Chapter,
    ChapterLore,
    ChapterPlan,
    ChapterStatus,
    ChapterSummary,
    Character,
    CharacterUpsert,
    ConsistencyReport,
    LoreEntry,
    Phase,
    PlotThread,
    Project,
    ProjectCreate,
    ReadingGate,
    Refinement,
    ReviewDraft,
    StateDelta,
    StyleProfile,
    StyleReport,
)

_locks: dict[tuple[str, str], RLock] = {}
_locks_guard = RLock()
FRESHNESS_FIELDS = ("body_hash", "derived_hashes", "stale_results", "summary_status", "summary_error", "upstream_changed", "plan_upstream_changed", "stacked_weight_migration_pending")


def chapter_lock(project_id: str):
    from . import db
    with _locks_guard:
        return _locks.setdefault((str(db.DB_PATH), project_id), RLock())


def _serialized(fn):
    @wraps(fn)
    def wrapped(project_id, *args, **kwargs):
        with chapter_lock(project_id):
            recover_pending(project_id)
            return fn(project_id, *args, **kwargs)
    return wrapped


def _not_found(message: str) -> HTTPException:
    return HTTPException(404, detail={"code": "NOT_FOUND", "message": message})


# ---- 旧格式（format 1）迁移：premise / world_rules → entries ----
#
# 只认键、不认版本号，处理完就把旧键 pop 掉，因此天然幂等：
# 再次读到同一份数据时没有旧键，直接原样返回。


def _legacy_entry_text(value: Any) -> str:
    return (value.get("rule") if isinstance(value, dict) else value) or ""


def _migrate_entries(data: dict[str, Any]) -> dict[str, Any]:
    """把 premise 与 world_rules 折进 entries；含旧键时返回新 dict，否则原样返回。"""
    if "premise" not in data and "world_rules" not in data:
        return data
    entries = list(data.get("entries") or [])
    premise = (data.get("premise") or "").strip()
    if premise:
        entries.insert(
            0,
            LoreEntry(id="", category="world", title="故事背景", content=premise).model_dump(),
        )
    for rule in data.get("world_rules") or []:
        text = str(_legacy_entry_text(rule)).strip()
        if not text:
            continue
        rid = rule.get("id", "") if isinstance(rule, dict) else ""
        hard = bool(rule.get("hard", True)) if isinstance(rule, dict) else True
        entries.append(
            LoreEntry(id=rid or "", category="rule", title="", content=text, hard=hard).model_dump()
        )
    out = {k: v for k, v in data.items() if k not in ("premise", "world_rules")}
    out["entries"] = entries
    return out


def _normalize_setting(data: dict[str, Any]) -> dict[str, Any]:
    return _migrate_entries(dict(data))


def _normalize_lore(lore: Optional[dict[str, Any]]) -> Optional[dict[str, Any]]:
    if not lore:
        return lore
    return _migrate_entries(dict(lore))


def _is_legacy(data: Optional[dict[str, Any]]) -> bool:
    """这份数据是否还带着 format 1 的旧键（用于决定要不要回写作品文件夹）。"""
    if not data:
        return False
    return "premise" in data or "world_rules" in data


def _root_for(conn: sqlite3.Connection, project_id: str) -> Optional[Path]:
    row = conn.execute("SELECT root_path FROM projects WHERE id=?", (project_id,)).fetchone()
    return Path(row["root_path"]) if row and row["root_path"] else None


def _row_to_project(row) -> Project:
    data = _normalize_setting(loads(row["data_json"]) or {})
    root = row["root_path"] or ""
    return Project(
        id=row["id"],
        title=row["title"],
        created_at=datetime.fromisoformat(row["created_at"]),
        phase=Phase(row["phase"]),
        current_chapter=row["current_chapter"],
        genre=data.get("genre", []),
        tone=data.get("tone", ""),
        audience=data.get("audience", ""),
        cover=data.get("cover") or {},
        entries=data.get("entries", []),
        characters=data.get("characters", []),
        forbidden=data.get("forbidden", []),
        style=StyleProfile(**data.get("style", {})),
        threads=data.get("threads", []),
        npc_auto_act=data.get("npc_auto_act", False),
        offline_mode=bool(row["offline_mode"]),
        provider=row["provider"],
        estimated_cost_usd=row["estimated_cost_usd"],
        root_path=root,
        root_missing=bool(root) and not Path(root).is_dir(),
    )


def _project_data(p: Project | ProjectCreate) -> dict[str, Any]:
    return {
        "genre": p.genre,
        "tone": p.tone,
        "audience": p.audience,
        "cover": p.cover.model_dump() if isinstance(p, Project) else {},
        "entries": [e.model_dump() if hasattr(e, "model_dump") else e for e in p.entries],
        "characters": [c.model_dump() if hasattr(c, "model_dump") else c for c in p.characters],
        "forbidden": p.forbidden,
        "style": p.style.model_dump() if hasattr(p.style, "model_dump") else p.style,
        "threads": [t.model_dump() if hasattr(t, "model_dump") else t for t in p.threads],
        "npc_auto_act": p.npc_auto_act,
    }


def _upsert_project_row(conn: sqlite3.Connection, proj: Project) -> None:
    conn.execute(
        """INSERT INTO projects(id,title,created_at,phase,current_chapter,data_json,offline_mode,provider,estimated_cost_usd,root_path)
           VALUES(?,?,?,?,?,?,?,?,?,?)
           ON CONFLICT(id) DO UPDATE SET
             title=excluded.title, phase=excluded.phase, current_chapter=excluded.current_chapter,
             data_json=excluded.data_json, offline_mode=excluded.offline_mode, provider=excluded.provider,
             estimated_cost_usd=excluded.estimated_cost_usd, root_path=excluded.root_path""",
        (
            proj.id,
            proj.title,
            proj.created_at.isoformat(),
            proj.phase.value,
            proj.current_chapter,
            dumps(_project_data(proj)),
            int(proj.offline_mode),
            proj.provider,
            proj.estimated_cost_usd,
            proj.root_path,
        ),
    )


def create_project(body: ProjectCreate) -> Project:
    pid = new_id()
    now = utcnow()
    # compile style constraints
    style = body.style
    if not style.compiled_system_constraints:
        style.compiled_system_constraints = _compile_style(style)
        body.style = style
    if not body.threads:
        # 没有背景设定就不给走向：主线的 directive 由第一条背景条目（通常是世界观）充当
        seed = next((e.content for e in body.entries if e.category == "world" and e.content.strip()), "")
        body.threads = [
            PlotThread(id="main", name="主线", kind="main", state="active", user_directive=seed)
        ]
    root = ws.new_folder(body.parent_dir, body.title) if body.parent_dir else None
    proj = Project(
        id=pid,
        title=body.title,
        created_at=now,
        phase=Phase.PLAN,
        current_chapter=0,
        root_path=str(root) if root else "",
        **{k: getattr(body, k) for k in (
            "genre", "tone", "audience", "entries",
            "characters", "forbidden", "style", "threads", "npc_auto_act",
        )},
    )
    with connect() as conn:
        _upsert_project_row(conn, proj)
        append_event(conn, pid, "ProjectCreated", proj.model_dump(mode="json"))
    if root:
        ws.write_project_file(root, proj.model_dump(mode="json"))
    return proj


def list_projects() -> list[Project]:
    with connect() as conn:
        rows = conn.execute("SELECT * FROM projects ORDER BY created_at DESC").fetchall()
    return [_row_to_project(r) for r in rows]


def get_project(project_id: str) -> Project:
    with connect() as conn:
        row = conn.execute("SELECT * FROM projects WHERE id=?", (project_id,)).fetchone()
    if not row:
        raise _not_found("project not found")
    return _row_to_project(row)


def save_project(proj: Project) -> Project:
    root = ws.check_root(ws.root_of(proj.id))
    with connect() as conn:
        conn.execute(
            """UPDATE projects SET title=?, phase=?, current_chapter=?, data_json=?,
               offline_mode=?, provider=?, estimated_cost_usd=? WHERE id=?""",
            (
                proj.title,
                proj.phase.value,
                proj.current_chapter,
                dumps(_project_data(proj)),
                int(proj.offline_mode),
                proj.provider,
                proj.estimated_cost_usd,
                proj.id,
            ),
        )
        # A failed atomic file write must also roll back the database update.
        if root:
            ws.write_project_file(root, proj.model_dump(mode="json"))
    return proj


def set_phase(project_id: str, phase: Phase, chapter_no: Optional[int] = None) -> Project:
    proj = get_project(project_id)
    proj.phase = phase
    if chapter_no is not None:
        proj.current_chapter = chapter_no
    save_project(proj)
    with connect() as conn:
        append_event(
            conn,
            project_id,
            "PhaseChanged",
            {"phase": phase.value, "chapter_no": chapter_no},
            chapter_no,
        )
    return proj


def _compile_style(style: StyleProfile) -> str:
    lines = [
        f"人称：{style.pov}",
        f"时态：{style.tense}",
        f"句长偏好：{style.sentence_length}",
    ]
    if style.banned_words:
        lines.append("禁用表达（优先删除）：" + "、".join(style.banned_words[:20]))
    if style.preferred_replacements:
        pairs = [f"{k}→{v}" for k, v in list(style.preferred_replacements.items())[:15]]
        lines.append("替换对照：" + "；".join(pairs))
    if style.reference_samples:
        lines.append("文风参考（靠近生成时注入原文片段，此处仅提示已提供范文）")
    return "\n".join(lines)


# ---- chapters ----


def _legacy_body(project_id: str, no: int) -> str:
    p = chapter_path(project_id, no)
    if not p.exists():
        return ""
    body = p.read_text(encoding="utf-8")
    if body.startswith("---"):
        parts = body.split("---", 2)
        if len(parts) >= 3:
            body = parts[2].lstrip("\n")
    return body


def _row_to_chapter(row, project_id: str, root: Optional[Path]) -> Chapter:
    if root is not None:
        body = ws.read_chapter_body(root, row["body_path"] or "")
    else:
        body = _legacy_body(project_id, row["no"]) if row["body_path"] else ""
    plan = loads(row["plan_json"])
    summary = loads(row["summary_json"])
    gate = loads(row["gate_json"])
    refinement = loads(row["refinement_json"])
    state_delta = loads(row["state_delta_json"])
    consistency = loads(row["consistency_json"])
    plan_check = loads(row["plan_check_json"])
    style = loads(row["style_json"])
    lore = _normalize_lore(loads(row["lore_json"]))
    review = loads(row["review_json"])
    actions = loads(row["actions_json"]) if "actions_json" in row.keys() else None
    stored, weights_changed = normalize_stored_chapter({
        "stacked_constraints": loads(row["stacked_constraints_json"]) or {},
        **(loads(row["freshness_json"]) or {}),
    })
    stored.pop("plan_check", None)
    chapter = Chapter(
        no=row["no"],
        chapter_uid=row["chapter_uid"],
        proposal=loads(row["proposal_json"]) or {},
        body_revision=loads(row["body_revision_json"]) or {},
        title=row["title"] or "",
        status=ChapterStatus(row["status"]),
        plan=ChapterPlan(**plan) if plan else None,
        body_text=body,
        body_rich=row["body_rich"] or "",
        word_count=row["word_count"] or 0,
        created_at=datetime.fromisoformat(row["created_at"]) if row["created_at"] else None,
        summary=ChapterSummary(**summary) if summary else None,
        state_delta=StateDelta(**state_delta) if state_delta else None,
        consistency_report=ConsistencyReport(**consistency) if consistency else None,
        plan_check=ConsistencyReport(**plan_check) if plan_check and not weights_changed else None,
        style_report=StyleReport(**style) if style else None,
        gate=ReadingGate(**gate) if gate else None,
        refinement=Refinement(**refinement) if refinement else None,
        lore=ChapterLore(**lore) if lore else None,
        lore_from=row["lore_from"],
        character_actions=actions or {},
        review=ReviewDraft(**review) if review else None,
        **stored,
    )
    revision = body_hash(body)
    if not chapter.body_hash:
        # Legacy baseline: persist on the next write, without discarding existing work.
        stamp(chapter, *(key for key in RESULTS if getattr(chapter, key) is not None))
    elif chapter.body_hash != revision:
        # Files can also be edited outside the app. Never serve outdated memory.
        invalidate(chapter)
        chapter.body_hash = revision
    return chapter


def _persist_weight_migration(project_id: str, row, chapter: Chapter) -> None:
    _, changed = normalize_stored_chapter({"stacked_constraints": loads(row["stacked_constraints_json"]) or {}})
    if changed:
        chapter.derived_hashes.pop("plan_check", None)
        upsert_chapter(project_id, chapter)
        with connect() as conn:
            append_event(conn, project_id, "StackedWeightsMigrated", {"minimum": 0.1}, chapter.no)
    # Persist a newly backfilled identity to the portable metadata as well.
    root = ws.root_of(project_id)
    if root is not None and root.is_dir():
        try:
            meta_path = root / ws.META_DIR / "chapters" / f"{chapter.no:04d}.json"
            meta = ws.read_json(meta_path) if meta_path.is_file() else {}
            if meta.get("chapter_uid") != chapter.chapter_uid:
                ws.write_chapter_meta(root, chapter.model_dump(mode="json"), row["body_path"] or "")
        except (OSError, ValueError):
            pass  # Viewing cached work remains possible when portable metadata is unavailable.


@_serialized
def list_chapters(project_id: str) -> list[Chapter]:
    with connect() as conn:
        root = _root_for(conn, project_id)
        rows = conn.execute(
            "SELECT * FROM chapters WHERE project_id=? ORDER BY no", (project_id,)
        ).fetchall()
    chapters = [_row_to_chapter(r, project_id, root) for r in rows]
    for row, chapter in zip(rows, chapters):
        _persist_weight_migration(project_id, row, chapter)
    external_changes = [r["no"] for r, ch in zip(rows, chapters)
                        if (loads(r["freshness_json"]) or {}).get("body_hash")
                        and (loads(r["freshness_json"]) or {})["body_hash"] != ch.body_hash]
    for ch in chapters:
        upstream = [no for no in external_changes if no < ch.no]
        if upstream:
            ch.upstream_changed = sorted(set(ch.upstream_changed + upstream))
            ch.plan_upstream_changed = sorted(set(ch.plan_upstream_changed + upstream))
            invalidate(ch)
    return chapters


@_serialized
def get_chapter(project_id: str, no: int) -> Chapter:
    with connect() as conn:
        root = _root_for(conn, project_id)
        row = conn.execute(
            "SELECT * FROM chapters WHERE project_id=? AND no=?", (project_id, no)
        ).fetchone()
    if not row:
        raise _not_found("chapter not found")
    chapter = _row_to_chapter(row, project_id, root)
    _persist_weight_migration(project_id, row, chapter)
    return chapter


def _write_chapter_row(conn: sqlite3.Connection, project_id: str, chapter: Chapter, body_path: str) -> None:
    conn.execute(
        """INSERT INTO chapters(
            project_id, no, title, status, plan_json, body_path, word_count, created_at,
            summary_json, state_delta_json, consistency_json, plan_check_json, style_json, gate_json, refinement_json, lore_json,
            body_rich, lore_from, review_json, actions_json, stacked_constraints_json, chapter_uid, proposal_json, body_revision_json
        ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
        ON CONFLICT(project_id, no) DO UPDATE SET
            title=excluded.title, status=excluded.status, plan_json=excluded.plan_json,
            body_path=excluded.body_path, word_count=excluded.word_count,
            created_at=COALESCE(chapters.created_at, excluded.created_at),
            summary_json=excluded.summary_json, state_delta_json=excluded.state_delta_json,
            consistency_json=excluded.consistency_json, plan_check_json=excluded.plan_check_json,
            style_json=excluded.style_json,
            gate_json=excluded.gate_json, refinement_json=excluded.refinement_json,
            lore_json=excluded.lore_json, body_rich=excluded.body_rich,
            lore_from=excluded.lore_from, review_json=excluded.review_json,
            actions_json=excluded.actions_json, stacked_constraints_json=excluded.stacked_constraints_json,
            chapter_uid=excluded.chapter_uid, proposal_json=excluded.proposal_json, body_revision_json=excluded.body_revision_json
        """,
        (
            project_id,
            chapter.no,
            chapter.title,
            chapter.status.value,
            dumps(chapter.plan) if chapter.plan else None,
            body_path,
            chapter.word_count,
            (chapter.created_at or utcnow()).isoformat(),
            dumps(chapter.summary) if chapter.summary else None,
            dumps(chapter.state_delta) if chapter.state_delta else None,
            dumps(chapter.consistency_report) if chapter.consistency_report else None,
            dumps(chapter.plan_check) if chapter.plan_check else None,
            dumps(chapter.style_report) if chapter.style_report else None,
            dumps(chapter.gate) if chapter.gate else None,
            dumps(chapter.refinement) if chapter.refinement else None,
            dumps(chapter.lore) if chapter.lore else None,
            chapter.body_rich or None,
            chapter.lore_from,
            dumps(chapter.review) if chapter.review else None,
            dumps(chapter.character_actions) if chapter.character_actions else None,
            dumps(chapter.stacked_constraints),
            chapter.chapter_uid,
            dumps(chapter.proposal),
            dumps(chapter.body_revision),
        ),
    )

    conn.execute("UPDATE chapters SET freshness_json=? WHERE project_id=? AND no=?",
                 (dumps({key: getattr(chapter, key) for key in FRESHNESS_FIELDS}), project_id, chapter.no))


def _replace_facts(conn, project_id: str, no: int, revision: str, facts: list[dict]) -> None:
    conn.execute("DELETE FROM facts WHERE project_id=? AND chapter_no=?", (project_id, no))
    for fact in facts:
        conn.execute("INSERT INTO facts(id,project_id,chapter_no,fact,known_by_json,source_hash) VALUES(?,?,?,?,?,?)",
                     (new_id(), project_id, no, fact["fact"], dumps(fact.get("known_by", [])), revision))


def context_revision(project_id: str, no: int, *, chapter_override: Chapter | None = None) -> str:
    """Optimistic token for the inputs to an AI call, including earlier chapter edits."""
    project = get_project(project_id)
    from .stacked_constraints import semantic_data
    chapters = list_chapters(project_id)
    if chapter_override is not None:
        chapters = [chapter_override if c.no == no else c for c in chapters]
    data = [(c.no, c.chapter_uid, c.title, body_hash(c.body_text), c.body_rich if c.no == no else "", c.status.value, c.plan, c.lore, c.upstream_changed,
             c.summary if c.no < no else None, sorted((c.character_actions or {}).items()),
             semantic_data(c.stacked_constraints) if c.no == no else None) for c in chapters if c.no <= no]
    return body_hash(dumps({"chapters": data, "style": project.style, "root": project.root_path,
                            "entries": project.entries, "characters": project.characters, "threads": project.threads,
                            "forbidden": project.forbidden}))


def save_analysis(project_id: str, original: Chapter, token: str, **results) -> Chapter:
    """Merge analysis fields into the newest chapter, preserving concurrent review edits."""
    with chapter_lock(project_id):
        latest = get_chapter(project_id, original.no)
        if latest.chapter_uid != original.chapter_uid or context_revision(project_id, original.no) != token:
            raise ws.conflict("正文或上下文已修改，请重新检查", "BODY_CHANGED")
        for key, value in results.items():
            setattr(latest, key, value)
        stamp(latest, *results)
        return upsert_chapter(project_id, latest, expected_hash=body_hash(original.body_text), expected_context=token)


def _apply_chapter_operation(project_id: str, root: Path, operation: dict) -> None:
    # Proposal artifacts are immutable and written before their DB/meta references.
    # The journal remains until every side effect has completed, including trimming.
    from .proposals import apply_history_files, finish_history_files
    apply_history_files(root, operation.get("proposal_io", {}))
    revision_io = operation.get("body_revision_io", {})
    if revision_io.get("backup"):
        from .versions import checkpoint
        backup = revision_io["backup"]
        checkpoint(project_id, backup["no"], backup["body_text"], backup["body_rich"], "ai")
    migrated = []
    for item in operation["changes"]:
        item["chapter"], weights_changed = normalize_stored_chapter(item["chapter"])
        if weights_changed:
            migrated.append(item["chapter"]["no"])
        ch = Chapter(**item["chapter"])
        if item.get("write_body", True):
            ws.write_chapter_body(root, item["file"], ch.body_text, item["old"])
        ws.write_chapter_meta(root, ch.model_dump(mode="json"), item["file"])
    with connect() as conn:
        for item in operation["changes"]:
            _write_chapter_row(conn, project_id, Chapter(**item["chapter"]), item["file"])
        for no in migrated:
            append_event(conn, project_id, "StackedWeightsMigrated", {"minimum": 0.1, "source": "pending-save"}, no)
        if operation["facts"] is not None:
            first = operation["changes"][0]["chapter"]
            _replace_facts(conn, project_id, first["no"], first["body_hash"], operation["facts"])
        receipt = operation.get("proposal_io", {}).get("receipt")
        if receipt:
            conn.execute("UPDATE proposal_receipts SET status='completed',response_json=? WHERE project_id=? AND chapter_uid=? AND request_id=?",
                         (dumps(receipt["response"]), project_id, receipt["chapter_uid"], receipt["request_id"]))
        receipt = revision_io.get("receipt")
        if receipt:
            conn.execute("UPDATE body_revision_receipts SET status='completed',response_json=? WHERE project_id=? AND chapter_uid=? AND request_id=?",
                         (dumps(receipt["response"]), project_id, receipt["chapter_uid"], receipt["request_id"]))
    ws.write_facts_file(project_id)
    for item in operation["changes"]:
        ws.finish_chapter_rename(root, item["file"], item["old"])
    finish_history_files(root, operation.get("proposal_io", {}))
    (ws.meta_root(root) / "pending-save.json").unlink(missing_ok=True)


def recover_pending(project_id: str) -> None:
    """Idempotent roll-forward of a durable chapter save; also called on startup."""
    with chapter_lock(project_id):
        root = ws.root_of(project_id)
        if root is None or not root.is_dir():
            return
        journal = root / ws.META_DIR / "pending-save.json"
        if journal.is_file():
            operation = ws.read_json(journal)
            # A journal in an imported folder is data, never an arbitrary write instruction.
            for item in operation["changes"]:
                for name in (item["file"], item["old"]):
                    if name and (Path(name).name != name or not name.lower().endswith(".md")):
                        raise ws.bad_request("保存日志包含无效章节路径", "INVALID_JOURNAL")
            _apply_chapter_operation(project_id, root, operation)


@_serialized
def upsert_chapter(project_id: str, chapter: Chapter, *, expected_hash: str | None = None,
                   facts: list[dict] | None = None, expected_context: str | None = None,
                   proposal_write: bool = False, proposal_io: dict | None = None,
                   body_revision_write: bool = False, body_revision_io: dict | None = None) -> Chapter:
    if expected_context is not None and context_revision(project_id, chapter.no) != expected_context:
        raise ws.conflict("正文或上下文已修改，请重试", "BODY_CHANGED")
    with connect() as conn:
        root = ws.check_root(_root_for(conn, project_id))
        previous = conn.execute("SELECT * FROM chapters WHERE project_id=? AND no=?", (project_id, chapter.no)).fetchone()
        old_chapter = _row_to_chapter(previous, project_id, root) if previous else None
        if old_chapter is not None:
            if (expected_context is not None or proposal_write or body_revision_write) and chapter.chapter_uid != old_chapter.chapter_uid:
                raise ws.conflict("章节身份已改变，请重新打开", "CHAPTER_CHANGED")
            chapter.chapter_uid = old_chapter.chapter_uid
            if not proposal_write:
                chapter.proposal = old_chapter.proposal.model_copy(deep=True)
            if not body_revision_write:
                chapter.body_revision = old_chapter.body_revision.model_copy(deep=True)
        # An AI result owns generated fields, never the author's layout. Layout-only saves
        # deliberately do not change the semantic context token and must still survive.
        if expected_context is not None and old_chapter is not None:
            chapter.stacked_constraints = old_chapter.stacked_constraints.model_copy(deep=True)
            chapter.stacked_weight_migration_pending = old_chapter.stacked_weight_migration_pending
        if expected_hash is not None and (old_chapter is None or body_hash(old_chapter.body_text) != expected_hash):
            raise ws.conflict("正文已修改，请重试；本次旧版本结果未写入", "BODY_CHANGED")
        revision = body_hash(chapter.body_text)
        persisted_hash = (loads(previous["freshness_json"]) or {}).get("body_hash") if previous else None
        changed = previous is not None and (persisted_hash or body_hash(old_chapter.body_text)) != revision
        if changed:
            invalidate(chapter)
            facts = []
        elif old_chapter is not None:
            from .stacked_constraints import semantic_data
            if old_chapter.plan != chapter.plan or semantic_data(old_chapter.stacked_constraints) != semantic_data(chapter.stacked_constraints):
                chapter.plan_upstream_changed = []
                if chapter.plan_check:
                    chapter.stale_results["plan_check"] = chapter.plan_check.model_dump(mode="json")
                chapter.plan_check = None
                chapter.derived_hashes.pop("plan_check", None)
            if old_chapter.lore != chapter.lore:
                invalidate(chapter)
                facts = []
        chapter.body_hash = revision
        for key in RESULTS:
            if getattr(chapter, key) is not None and key not in chapter.derived_hashes:
                stamp(chapter, key)
        if root is not None:
            rows = conn.execute(
                "SELECT no, body_path FROM chapters WHERE project_id=?", (project_id,)
            ).fetchall()
            old = next((r["body_path"] for r in rows if r["no"] == chapter.no), None) or None
            taken = [r["body_path"] or "" for r in rows if r["no"] != chapter.no]
            file_name = ws.chapter_file_name(chapter.title, taken, old)
            changes = [{"chapter": chapter.model_dump(mode="json"), "file": file_name, "old": old,
                        "write_body": not proposal_write}]
            if changed:
                later = conn.execute("SELECT * FROM chapters WHERE project_id=? AND no>?", (project_id, chapter.no)).fetchall()
                for row in later:
                    dependent = _row_to_chapter(row, project_id, root)
                    if chapter.no not in dependent.upstream_changed:
                        dependent.upstream_changed.append(chapter.no)
                    dependent.plan_upstream_changed = sorted(set(dependent.plan_upstream_changed + [chapter.no]))
                    invalidate(dependent)
                    changes.append({"chapter": dependent.model_dump(mode="json"), "file": row["body_path"], "old": row["body_path"], "write_body": False})
            operation = {"changes": changes, "facts": facts, "proposal_io": proposal_io or {}, "body_revision_io": body_revision_io or {}}
            ws.write_json(ws.meta_root(root) / "pending-save.json", operation)
        else:
            body_p = chapter_path(project_id, chapter.no)
            front = f"---\nno: {chapter.no}\ntitle: {chapter.title}\nstatus: {chapter.status.value}\nword_count: {chapter.word_count}\n---\n\n"
            ws.atomic_text(body_p, front + chapter.body_text)
            if chapter.summary:
                ws.atomic_text(summary_path(project_id, chapter.no), dumps(chapter.summary))
            _write_chapter_row(conn, project_id, chapter, str(body_p))
            if facts is not None:
                _replace_facts(conn, project_id, chapter.no, revision, facts)
            if changed:
                for row in conn.execute("SELECT * FROM chapters WHERE project_id=? AND no>?", (project_id, chapter.no)).fetchall():
                    dependent = _row_to_chapter(row, project_id, None)
                    dependent.upstream_changed = sorted(set(dependent.upstream_changed + [chapter.no]))
                    dependent.plan_upstream_changed = sorted(set(dependent.plan_upstream_changed + [chapter.no]))
                    invalidate(dependent)
                    _write_chapter_row(conn, project_id, dependent, row["body_path"])

    if root is not None:
        _apply_chapter_operation(project_id, root, operation)
    return chapter


def sync_chapter_metas(project_id: str) -> None:
    """数据库里直接改过的字段（如回滚改状态）同步回章节元数据文件。"""
    with connect() as conn:
        root = _root_for(conn, project_id)
        if root is None or not root.is_dir():
            return
        rows = conn.execute("SELECT * FROM chapters WHERE project_id=?", (project_id,)).fetchall()
    for r in rows:
        ch = _row_to_chapter(r, project_id, root)
        ws.write_chapter_meta(root, ch.model_dump(mode="json"), r["body_path"] or "")


@_serialized
def supersede_after(project_id: str, from_no: int) -> None:
    with connect() as conn:
        conn.execute(
            "UPDATE chapters SET status=? WHERE project_id=? AND no>?",
            (ChapterStatus.superseded.value, project_id, from_no),
        )
        append_event(conn, project_id, "ChapterRolledBack", {"from_no": from_no}, from_no)
    sync_chapter_metas(project_id)


# ---- lore (per-chapter settings snapshot) ----


def project_lore(proj: Project) -> ChapterLore:
    return ChapterLore(
        entries=list(proj.entries), characters=list(proj.characters), threads=list(proj.threads)
    ).model_copy(deep=True)


def _master_characters(proj: Project) -> dict[str, Character]:
    return {c.id: c for c in proj.characters}


def _lore_with_master_chars(proj: Project, lore: ChapterLore, *, create_missing: bool = False) -> ChapterLore:
    """把章内人物快照的公共资料换成作品主档；主档没有的 id 保留快照（可顺带建立主档）。"""
    master = _master_characters(proj)
    out = lore.model_copy(deep=True)
    chars: list[Character] = []
    for c in out.characters:
        owner = master.get(c.id)
        if owner is not None:
            chars.append(owner.model_copy(deep=True))
            continue
        if create_missing:
            # 兼容旧数据：章内已有、主档还没有的人物，第一次保存时建立共享记录。
            master[c.id] = c
            proj.characters.append(c)
        chars.append(c)
    out.characters = chars
    return out


def lore_view(proj: Project, lore: Optional[ChapterLore]) -> Project:
    """用某章的设定快照覆盖作品级设定，供生成/提取/审计使用；不要保存返回值。

    人物公共资料以作品主档（project.characters）为准，章节快照只在主档缺失该 id 时兜底。
    """
    if lore is None:
        return proj
    master = _master_characters(proj)
    characters = [
        master[c.id].model_copy(deep=True) if c.id in master else c for c in lore.characters
    ]
    return proj.model_copy(
        update={"entries": lore.entries, "threads": lore.threads, "characters": characters}
    )


def latest_chapter_no(chapters: list[Chapter]) -> Optional[int]:
    nos = [c.no for c in chapters if c.status != ChapterStatus.superseded]
    return max(nos) if nos else None


def _merge_lore(lore: ChapterLore, base: ChapterLore) -> ChapterLore:
    """补 id、校验必填，并保留客户端表单里没有的旧字段。

    known_facts / status / public_face / recent_thoughts 不再出现在新人物卡布局里，
    请求为空时沿用 base 里的值，避免一次保存把旧资料清空。
    """
    old_chars = {c.id: c for c in base.characters}
    chars: list[Character] = []
    for c in lore.characters:
        if not c.id.strip():
            c.id = new_id()
        missing = c.missing_required()
        if missing:
            raise HTTPException(
                400,
                detail={
                    "code": "REQUIRED_FIELDS",
                    "message": f"人物「{c.name or '未命名'}」必填项未完成：" + "、".join(missing),
                    "missing": missing,
                },
            )
        old = old_chars.get(c.id)
        if old:
            if not c.known_facts:
                c.known_facts = old.known_facts
            if not c.status:
                c.status = old.status
            if not c.public_face:
                c.public_face = old.public_face
            if not c.recent_thoughts:
                c.recent_thoughts = old.recent_thoughts
        chars.append(c)
    threads: list[PlotThread] = []
    for t in lore.threads:
        if not t.id.strip():
            t.id = new_id()
        if not t.name.strip():
            raise HTTPException(400, detail={"code": "REQUIRED_FIELDS", "message": "脉络名称不能为空"})
        threads.append(t)
    entries: list[LoreEntry] = []
    for e in lore.entries:
        if not e.title.strip() and not e.content.strip():
            continue  # 空条目静默丢弃，免得界面上多出一张空卡片
        if e.category not in LORE_CATEGORIES:
            raise HTTPException(
                400,
                detail={
                    "code": "BAD_CATEGORY",
                    "message": f"未知的背景分类：{e.category}",
                    "allowed": list(LORE_CATEGORIES),
                },
            )
        if not e.id.strip():
            e.id = new_id()
        if e.category != "rule":
            e.hard = False  # hard 只对「规则」有意义
        e.title = e.title.strip()
        e.content = e.content.strip()
        entries.append(e)
    return ChapterLore(entries=entries, characters=chars, threads=threads)


@_serialized
def save_lore(project_id: str, lore: ChapterLore, chapter_no: Optional[int]) -> Project:
    """chapter_no 为空时保存作品级（初始）设定；保存最后一章时同步作品级，供下一章继承。

    人物公共资料只认作品主档：章节级保存不会用章节快照覆盖 project.characters，
    主档里还没有的人物 id 会首次建立共享记录；最新章只同步 entries / threads。
    """
    proj = get_project(project_id)
    chapters = list_chapters(project_id)
    latest = latest_chapter_no(chapters)
    changed_chars = False
    if chapter_no is not None:
        ch = next((c for c in chapters if c.no == chapter_no), None)
        if ch is None:
            raise _not_found("chapter not found")
        lore = _merge_lore(lore, ch.lore or project_lore(proj))
        # 已有主档的人物一律以主档最新公共资料为准；缺失的才用本章记录兜底并建主档。
        before = len(proj.characters)
        lore = _lore_with_master_chars(proj, lore, create_missing=True)
        changed_chars = len(proj.characters) != before
        ch.lore = lore
        upsert_chapter(project_id, ch)
    else:
        lore = _merge_lore(lore, project_lore(proj))
    if chapter_no is None:
        proj.entries = lore.entries
        proj.characters = lore.characters
        proj.threads = lore.threads
        save_project(proj)
    elif chapter_no == latest:
        proj.entries = lore.entries
        proj.threads = lore.threads
        save_project(proj)
    elif changed_chars:
        save_project(proj)
    with connect() as conn:
        append_event(conn, project_id, "LoreEdited", {"chapter_no": chapter_no}, chapter_no)
    return get_project(project_id)


@_serialized
def inherit_lore(project_id: str, no: int, from_no: int) -> Chapter:
    """用第 from_no 章的设定整体覆盖第 no 章设定，之后可在第 no 章上自由增删改。"""
    proj = get_project(project_id)
    chapters = list_chapters(project_id)
    ch = next((c for c in chapters if c.no == no), None)
    src = next((c for c in chapters if c.no == from_no), None)
    if ch is None or src is None:
        raise _not_found("chapter not found")
    if from_no >= no or src.status == ChapterStatus.superseded:
        raise HTTPException(400, detail={"code": "BAD_SOURCE", "message": "只能继承本章之前、未作废章节的设定"})
    if ch.status == ChapterStatus.superseded:
        raise HTTPException(400, detail={"code": "SUPERSEDED", "message": "已作废章节的设定不可修改"})
    copied = (src.lore or project_lore(proj)).model_copy(deep=True)
    before = len(proj.characters)
    copied = _lore_with_master_chars(proj, copied, create_missing=True)
    ch.lore = copied
    ch.lore_from = from_no
    upsert_chapter(project_id, ch)
    if no == latest_chapter_no(chapters):
        proj.entries = copied.entries
        proj.threads = copied.threads
        save_project(proj)
    elif len(proj.characters) != before:
        save_project(proj)
    with connect() as conn:
        append_event(conn, project_id, "LoreInherited", {"chapter_no": no, "from": from_no}, no)
    return get_chapter(project_id, no)


# ---- characters（作品主档 + 本章行动） ----

# 新人物卡布局里已经不再出现的旧字段：请求为空时不能把主档/章节里的旧值抹掉。
LEGACY_CARD_FIELDS = ("recent_thoughts", "public_face", "known_facts", "status")


def _character_error(message: str, code: str) -> HTTPException:
    return HTTPException(400, detail={"code": code, "message": message})


@_serialized
def upsert_character(project_id: str, body: CharacterUpsert) -> Character:
    """统一保存人物主档；可选地把人物加进某章名单并写入该章行动。"""
    proj = get_project(project_id)
    chapters = list_chapters(project_id)
    cid = (body.id or "").strip() or new_id()

    if body.chapter_action is not None and body.chapter_no is None:
        raise _character_error("写入本章行动需要指定章节", "CHAPTER_REQUIRED")

    chapter: Optional[Chapter] = None
    if body.chapter_no is not None:
        chapter = next((c for c in chapters if c.no == body.chapter_no), None)
        if chapter is None:
            raise _character_error("目标章节不存在", "CHAPTER_NOT_FOUND")
        if chapter.status == ChapterStatus.superseded:
            raise _character_error("目标章节已作废，不能修改", "SUPERSEDED")

    master = next((c for c in proj.characters if c.id == cid), None)
    # 兼容旧数据：主档没有这个人时，允许以目标章的 lore 记录作为来源，保存后建立主档。
    snapshot = None
    if master is None and chapter is not None and chapter.lore is not None:
        snapshot = next((c for c in chapter.lore.characters if c.id == cid), None)

    data = body.model_dump(exclude={"chapter_no", "chapter_action"})
    data["id"] = cid
    char = Character(**data)
    existing = master or snapshot
    if existing is not None:
        for field in LEGACY_CARD_FIELDS:
            if not getattr(char, field) and getattr(existing, field):
                setattr(char, field, getattr(existing, field))

    missing = char.missing_required()
    if missing:
        raise HTTPException(
            400,
            detail={
                "code": "REQUIRED_FIELDS",
                "message": "个人资料必填项未完成：" + "、".join(missing),
                "missing": missing,
            },
        )

    chars = list(proj.characters)
    idx = next((i for i, c in enumerate(chars) if c.id == cid), None)
    if idx is None:
        chars.append(char)
        event = "CharacterCreated"
    else:
        chars[idx] = char
        event = "CharacterUpdated"
    proj.characters = chars

    if chapter is not None:
        if chapter.lore is None:
            lore = project_lore(proj)  # 主档已含最新人物
        else:
            lore = chapter.lore.model_copy(deep=True)
            old_by_id = {c.id: c for c in lore.characters}
            ids = [c.id for c in lore.characters]
            if cid not in ids:
                ids.append(cid)
            master_by_id = {c.id: c for c in proj.characters}
            lore.characters = [
                (master_by_id[i] if i in master_by_id else old_by_id[i]).model_copy(deep=True)
                for i in ids
            ]
        chapter.lore = lore
        if body.chapter_action is not None:
            action = (body.chapter_action or "").strip()
            actions = dict(chapter.character_actions)
            if action:
                actions[cid] = action
            else:
                actions.pop(cid, None)
            chapter.character_actions = actions
        upsert_chapter(project_id, chapter)

    save_project(proj)
    with connect() as conn:
        append_event(conn, project_id, event, char.model_dump(), None)
    return char


@_serialized
def delete_character(project_id: str, char_id: str) -> Project:
    """删除人物主档，并从所有章节名单/本章行动里清掉该 id，避免快照复活。"""
    proj = get_project(project_id)
    before = len(proj.characters)
    proj.characters = [c for c in proj.characters if c.id != char_id]
    removed = len(proj.characters) != before

    chapters = list_chapters(project_id)
    for ch in chapters:
        changed = False
        if ch.lore is not None and any(c.id == char_id for c in ch.lore.characters):
            kept = [c for c in ch.lore.characters if c.id != char_id]
            ch.lore = ch.lore.model_copy(update={"characters": kept}, deep=True)
            changed = True
        if char_id in (ch.character_actions or {}):
            actions = dict(ch.character_actions)
            actions.pop(char_id, None)
            ch.character_actions = actions
            changed = True
        if changed:
            removed = True
            upsert_chapter(project_id, ch)

    if not removed:
        raise _not_found("人物不存在")

    save_project(proj)
    with connect() as conn:
        append_event(conn, project_id, "CharacterDeleted", {"id": char_id})
    return get_project(project_id)


# ---- work folder operations ----


def import_folder(path: str) -> Project:
    root = Path((path or "").strip())
    if not root.is_dir():
        raise ws.bad_request("文件夹不存在", "NO_FOLDER")
    data = ws.read_project_file(root)
    migrated = _is_legacy(data)
    data = _normalize_setting(data)
    wrote_id = False
    if not data.get("id"):
        data["id"] = new_id()
        wrote_id = True
    data.setdefault("title", root.name)
    data.setdefault("created_at", utcnow().isoformat())
    data["root_path"] = str(root)
    data["root_missing"] = False
    proj = Project(**data)
    with connect() as conn:
        _upsert_project_row(conn, proj)
    recover_pending(proj.id)
    metas = ws.read_chapter_metas(root)
    seen_uids = set()
    for index, (meta, file_name) in enumerate(metas):
        if not meta.get("chapter_uid") or meta["chapter_uid"] in seen_uids:
            from uuid import uuid4
            meta["chapter_uid"] = uuid4().hex
            # A copied chapter cannot inherit another chapter's proposal ownership.
            meta["proposal"] = {}
            meta["body_revision"] = {}
            migrated = True
        seen_uids.add(meta["chapter_uid"])
        if _is_legacy(meta.get("lore")):
            migrated = True
        meta["lore"] = _normalize_lore(meta.get("lore"))
        meta, weights_changed = normalize_stored_chapter(meta)
        metas[index] = (meta, file_name)
        if weights_changed:
            migrated = True
            with connect() as conn:
                append_event(conn, proj.id, "StackedWeightsMigrated", {"minimum": 0.1}, meta["no"])
    facts = ws.read_facts_file(root)
    with connect() as conn:
        conn.execute("DELETE FROM chapters WHERE project_id=?", (proj.id,))
        conn.execute("DELETE FROM facts WHERE project_id=?", (proj.id,))
        _upsert_project_row(conn, proj)
        for meta, file_name in metas:
            _write_chapter_row(conn, proj.id, Chapter(**meta), file_name)
        append_event(conn, proj.id, "ProjectOpened", {"path": str(root)})
    from .retrieval import upsert_fact_embedding

    for f in facts:
        if f.get("fact"):
            upsert_fact_embedding(proj.id, f.get("chapter_no"), f["fact"], f.get("known_by") or [], f.get("source_hash", ""))
    if wrote_id or migrated:
        # 迁移过就把新格式写回作品文件夹，旧键不再出现（幂等：下次 migrated 为假）
        ws.write_project_file(root, proj.model_dump(mode="json"))
        sync_chapter_metas(proj.id)
    ws.write_facts_file(proj.id)
    return get_project(proj.id)


def migrate_project(project_id: str, parent_dir: str) -> Project:
    proj = get_project(project_id)
    if proj.root_path:
        raise ws.conflict("该作品已保存在作品文件夹中", "ALREADY_MIGRATED")
    chapters = list_chapters(project_id)
    root = ws.new_folder(parent_dir, proj.title)
    with connect() as conn:
        conn.execute("UPDATE projects SET root_path=? WHERE id=?", (str(root), project_id))
        # 旧的 body_path 是绝对路径，清空后按标题重新分配文件名
        conn.execute("UPDATE chapters SET body_path='' WHERE project_id=?", (project_id,))
        append_event(conn, project_id, "ProjectMigrated", {"path": str(root)})
    proj = get_project(project_id)
    save_project(proj)
    for ch in chapters:
        upsert_chapter(project_id, ch)
    ws.write_facts_file(project_id)
    return get_project(project_id)


def rename_project(project_id: str, title: str) -> Project:
    title = (title or "").strip()
    if not title:
        raise ws.bad_request("作品名不能为空", "REQUIRED")
    proj = get_project(project_id)
    root = ws.check_root(ws.root_of(project_id))
    if root is not None:
        new_root = root.parent / ws.sanitize_name(title, "未命名作品")
        if new_root.name != root.name:
            if str(new_root).lower() != str(root).lower() and new_root.exists():
                raise ws.conflict(f"已存在同名文件夹：{new_root}", "FOLDER_EXISTS")
            try:
                os.rename(root, new_root)
            except OSError as e:
                raise ws.conflict(f"无法重命名作品文件夹（可能有文件正被占用）：{e}", "RENAME_FAILED")
            with connect() as conn:
                conn.execute("UPDATE projects SET root_path=? WHERE id=?", (str(new_root), project_id))
            proj.root_path = str(new_root)
    proj.title = title
    save_project(proj)
    return get_project(project_id)


def delete_project(project_id: str) -> None:
    get_project(project_id)
    with connect() as conn:
        conn.execute("DELETE FROM facts WHERE project_id=?", (project_id,))
        conn.execute("DELETE FROM events WHERE project_id=?", (project_id,))
        conn.execute("DELETE FROM chapters WHERE project_id=?", (project_id,))
        conn.execute("DELETE FROM projects WHERE id=?", (project_id,))


@_serialized
def rename_chapter(project_id: str, no: int, title: str) -> Chapter:
    title = (title or "").strip()
    if not title:
        raise ws.bad_request("章节名不能为空", "REQUIRED")
    ch = get_chapter(project_id, no)
    with connect() as conn:
        root = _root_for(conn, project_id)
        others = conn.execute(
            "SELECT title, body_path FROM chapters WHERE project_id=? AND no<>?", (project_id, no)
        ).fetchall()
    if root is not None:
        wanted = f"{ws.sanitize_name(title, '未命名章节')}.md".lower()
        if any((r["body_path"] or "").lower() == wanted for r in others):
            raise ws.conflict(f"已有同名章节「{title}」", "CHAPTER_EXISTS")
    ch.title = title
    upsert_chapter(project_id, ch)
    return get_chapter(project_id, no)


@_serialized
def delete_chapter_facts(project_id: str, no: int) -> None:
    with connect() as conn:
        conn.execute("DELETE FROM facts WHERE project_id=? AND chapter_no=?", (project_id, no))
    ws.write_facts_file(project_id)


@_serialized
def clear_chapter(project_id: str, no: int) -> Chapter:
    ch = get_chapter(project_id, no)
    # 清空正文前留一版（source=restore-backup），避免误清空后无法找回
    from . import versions as versions_store

    versions_store.checkpoint(project_id, no, ch.body_text, ch.body_rich, "restore-backup")
    ch.body_text = ""
    ch.body_rich = ""
    ch.word_count = 0
    ch.summary = None
    ch.state_delta = None
    ch.consistency_report = None
    ch.style_report = None
    ch.gate = ReadingGate(chapter_no=no)
    ch.refinement = None
    ch.review = None
    ch.status = ChapterStatus.planned
    upsert_chapter(project_id, ch)
    delete_chapter_facts(project_id, no)
    proj = get_project(project_id)
    if proj.current_chapter == no and proj.phase != Phase.PLAN:
        proj.phase = Phase.PLAN
        save_project(proj)
    with connect() as conn:
        append_event(conn, project_id, "ChapterCleared", {}, no)
    return get_chapter(project_id, no)


@_serialized
def delete_chapter(project_id: str, no: int) -> Project:
    chapters = list_chapters(project_id)
    if not any(c.no == no for c in chapters):
        raise _not_found("chapter not found")
    was_latest = latest_chapter_no(chapters) == no
    later = sorted((c for c in chapters if c.no > no), key=lambda c: c.no)
    with connect() as conn:
        root = ws.check_root(_root_for(conn, project_id))
        files = {
            r["no"]: r["body_path"] or ""
            for r in conn.execute("SELECT no, body_path FROM chapters WHERE project_id=?", (project_id,))
        }
        conn.execute("DELETE FROM chapters WHERE project_id=? AND no>=?", (project_id, no))
        conn.execute("DELETE FROM facts WHERE project_id=? AND chapter_no=?", (project_id, no))
        conn.execute(
            "UPDATE facts SET chapter_no=chapter_no-1 WHERE project_id=? AND chapter_no>?", (project_id, no)
        )
        append_event(conn, project_id, "ChapterDeleted", {"no": no}, no)

    for n in [no] + [c.no for c in later]:
        if root is not None:
            ws.remove_chapter_files(root, n, files.get(n))
        else:
            for p in (chapter_path(project_id, n), summary_path(project_id, n)):
                if p.exists():
                    p.unlink()

    # 章节号变化：历史版本目录与笔记归属随章节一起前移
    if root is not None:
        from . import notes as notes_store
        from . import versions as versions_store
        from .proposals import _history_dir
        import shutil
        removed = next(c for c in chapters if c.no == no)
        history_dir = _history_dir(root, removed.chapter_uid)
        if history_dir.is_dir():
            shutil.rmtree(history_dir)
        with connect() as conn:
            conn.execute("DELETE FROM proposal_receipts WHERE project_id=? AND chapter_uid=?", (project_id, removed.chapter_uid))
            conn.execute("DELETE FROM body_revision_receipts WHERE project_id=? AND chapter_uid=?", (project_id, removed.chapter_uid))

        versions_store.shift(project_id, no, -1)
        notes_store.shift(project_id, no, -1)

    for c in later:
        old_no = c.no
        c.no = old_no - 1
        if c.title == f"第{old_no}章":
            c.title = f"第{c.no}章"
        if c.gate:
            c.gate.chapter_no = c.no
        if c.refinement:
            c.refinement.chapter_no = c.no
        if c.lore_from is not None:
            c.lore_from = None if c.lore_from == no else (c.lore_from - 1 if c.lore_from > no else c.lore_from)
        upsert_chapter(project_id, c)

    proj = get_project(project_id)
    if proj.current_chapter > no:
        proj.current_chapter -= 1
    elif proj.current_chapter == no:
        proj.current_chapter = max(no - 1, 0)
        proj.phase = Phase.PLAN
    remaining = list_chapters(project_id)
    if was_latest:
        new_latest = next((c for c in remaining if c.no == latest_chapter_no(remaining)), None)
        if new_latest is not None and new_latest.lore is not None:
            # 只回退章节级背景/脉络；人物公共资料以主档为准，绝不被旧章快照覆盖。
            proj.entries = new_latest.lore.entries
            proj.threads = new_latest.lore.threads
    save_project(proj)
    ws.write_facts_file(project_id)
    return get_project(project_id)


@_serialized
def insert_chapter(project_id: str, after_no: int) -> Chapter:
    """在第 after_no 章后面插一章空白章，后面的章节顺延编号（与 delete_chapter 对称）。"""
    chapters = list_chapters(project_id)
    base = next((c for c in chapters if c.no == after_no), None)
    if base is None:
        raise _not_found("chapter not found")
    if base.status == ChapterStatus.superseded:
        raise ws.conflict("已作废章节不可修改", "SUPERSEDED")
    proj = get_project(project_id)
    new_no = after_no + 1
    later = sorted((c for c in chapters if c.no > after_no), key=lambda c: c.no)
    appending = not any(c.status != ChapterStatus.superseded for c in later)
    with connect() as conn:
        root = ws.check_root(_root_for(conn, project_id))
        files = {
            r["no"]: r["body_path"] or ""
            for r in conn.execute("SELECT no, body_path FROM chapters WHERE project_id=?", (project_id,))
        }
        conn.execute("DELETE FROM chapters WHERE project_id=? AND no>?", (project_id, after_no))
        conn.execute(
            "UPDATE facts SET chapter_no=chapter_no+1 WHERE project_id=? AND chapter_no>?", (project_id, after_no)
        )
        append_event(conn, project_id, "ChapterInserted", {"no": new_no}, new_no)

    for c in later:
        if root is not None:
            ws.remove_chapter_files(root, c.no, files.get(c.no))
        else:
            for p in (chapter_path(project_id, c.no), summary_path(project_id, c.no)):
                if p.exists():
                    p.unlink()

    # 章节号变化：历史版本目录与笔记归属随章节一起后移
    if root is not None:
        from . import notes as notes_store
        from . import versions as versions_store

        versions_store.shift(project_id, after_no + 1, 1)
        notes_store.shift(project_id, after_no + 1, 1)

    for c in later:
        old_no = c.no
        c.no = old_no + 1
        if c.title == f"第{old_no}章":
            c.title = f"第{c.no}章"
        if c.gate:
            c.gate.chapter_no = c.no
        if c.refinement:
            c.refinement.chapter_no = c.no
        if c.lore_from is not None and c.lore_from > after_no:
            c.lore_from += 1
        upsert_chapter(project_id, c)

    upsert_chapter(
        project_id,
        Chapter(
            no=new_no,
            title=f"第{new_no}章",
            status=ChapterStatus.planned,
            created_at=utcnow(),
            gate=ReadingGate(chapter_no=new_no),
            # 插在中间的章不走「下一步」流程，给一个空细修，免得挡住后面章节的生成
            refinement=None if appending else Refinement(chapter_no=new_no),
            lore=(base.lore or project_lore(proj)).model_copy(deep=True),
            lore_from=after_no,
        ),
    )
    proj = get_project(project_id)
    if appending:
        proj.current_chapter = new_no
        proj.phase = Phase.PLAN
    elif proj.current_chapter > after_no:
        proj.current_chapter += 1
    save_project(proj)
    ws.write_facts_file(project_id)
    return get_chapter(project_id, new_no)


def append_chapters(project_id: str, items: list[tuple[str, str]]) -> list[Chapter]:
    """把（标题, 正文）依次追加到末尾，用于导入外部 TXT / Markdown。"""
    if not items:
        raise ws.bad_request("没有可导入的章节", "EMPTY")
    proj = get_project(project_id)
    chapters = list_chapters(project_id)
    no = latest_chapter_no(chapters) or 0
    prev = next((c for c in chapters if c.no == no), None)
    lore = (prev.lore if prev and prev.lore else project_lore(proj))
    created: list[Chapter] = []
    for title, body in items:
        no += 1
        text = (body or "").replace("\r\n", "\n").strip("\n")
        ch = Chapter(
            no=no,
            title=(title or "").strip() or f"第{no}章",
            status=ChapterStatus.drafted if text.strip() else ChapterStatus.planned,
            body_text=text,
            word_count=len(text),
            created_at=utcnow(),
            gate=ReadingGate(chapter_no=no),
            lore=lore.model_copy(deep=True),
            lore_from=no - 1 if no > 1 else None,
        )
        upsert_chapter(project_id, ch)
        created.append(ch)
    proj = get_project(project_id)
    proj.current_chapter = no
    proj.phase = Phase.PLAN
    save_project(proj)
    with connect() as conn:
        append_event(conn, project_id, "ChaptersImported", {"count": len(created)}, no)
    return [get_chapter(project_id, c.no) for c in created]


def chapter_file(project_id: str, no: int) -> str:
    """本章正文 .md 的完整路径。"""
    get_chapter(project_id, no)
    with connect() as conn:
        root = ws.check_root(_root_for(conn, project_id))
        row = conn.execute(
            "SELECT body_path FROM chapters WHERE project_id=? AND no=?", (project_id, no)
        ).fetchone()
    name = (row["body_path"] if row else "") or ""
    if root is None:
        return name
    return str(root / name) if name else ""


# ---- events / usage / facts ----


def list_events(project_id: str, limit: int = 100) -> list[dict[str, Any]]:
    with connect() as conn:
        rows = conn.execute(
            "SELECT * FROM events WHERE project_id=? ORDER BY id DESC LIMIT ?",
            (project_id, limit),
        ).fetchall()
    return [
        {
            "id": r["id"],
            "chapter_no": r["chapter_no"],
            "event_type": r["event_type"],
            "payload": loads(r["payload_json"]),
            "created_at": r["created_at"],
        }
        for r in rows
    ]


def add_usage(
    project_id: Optional[str],
    provider: str,
    model: str,
    input_tokens: int,
    output_tokens: int,
    cached_tokens: int,
    cost_usd: float,
    latency_ms: float,
    purpose: str,
) -> None:
    with connect() as conn:
        conn.execute(
            """INSERT INTO usage_log(
                project_id, provider, model, input_tokens, output_tokens, cached_tokens,
                cost_usd, latency_ms, purpose, created_at
            ) VALUES (?,?,?,?,?,?,?,?,?,?)""",
            (
                project_id,
                provider,
                model,
                input_tokens,
                output_tokens,
                cached_tokens,
                cost_usd,
                latency_ms,
                purpose,
                utcnow().isoformat(),
            ),
        )
        if project_id:
            conn.execute(
                "UPDATE projects SET estimated_cost_usd = estimated_cost_usd + ? WHERE id=?",
                (cost_usd, project_id),
            )


def usage_summary(project_id: Optional[str] = None) -> dict[str, Any]:
    with connect() as conn:
        if project_id:
            rows = conn.execute(
                "SELECT * FROM usage_log WHERE project_id=? ORDER BY id DESC LIMIT 200",
                (project_id,),
            ).fetchall()
            total = conn.execute(
                "SELECT COALESCE(SUM(cost_usd),0) AS c FROM usage_log WHERE project_id=?",
                (project_id,),
            ).fetchone()["c"]
        else:
            rows = conn.execute(
                "SELECT * FROM usage_log ORDER BY id DESC LIMIT 200"
            ).fetchall()
            total = conn.execute(
                "SELECT COALESCE(SUM(cost_usd),0) AS c FROM usage_log"
            ).fetchone()["c"]
    from .providers import instance_spec, lookup_price, provider_config

    custom_price: dict[str, bool] = {}

    def price_known(provider: str, model: str) -> bool:
        spec = instance_spec(provider)
        if spec is None or spec.kind in ("local", "mock") or lookup_price(model) is not None:
            return True
        if provider not in custom_price:
            custom_price[provider] = provider_config(provider)["price"] is not None
        return custom_price[provider]

    entries = [
        {
            "provider": r["provider"],
            "model": r["model"],
            "input_tokens": r["input_tokens"],
            "output_tokens": r["output_tokens"],
            "cached_tokens": r["cached_tokens"],
            "cost_usd": r["cost_usd"],
            "latency_ms": r["latency_ms"],
            "purpose": r["purpose"],
            "created_at": r["created_at"],
            "price_known": price_known(r["provider"], r["model"]),
        }
        for r in rows
    ]
    return {
        "total_cost_usd": total,
        "entries": entries,
        "has_unknown_price": any(not e["price_known"] for e in entries),
    }


def add_fact(project_id: str, chapter_no: int, fact: str, known_by: list[str]) -> str:
    from .retrieval import upsert_fact_embedding

    fid = upsert_fact_embedding(project_id, chapter_no, fact, known_by)
    ws.write_facts_file(project_id)
    return fid


def search_facts(project_id: str, query: str, limit: int = 8, *, before_chapter: int | None = None,
                 character_ids: list[str] | None = None, allow_remote: bool = True) -> list[dict[str, Any]]:
    from .retrieval import search_similar

    return search_similar(project_id, query, limit=limit, before_chapter=before_chapter, character_ids=character_ids, allow_remote=allow_remote)
