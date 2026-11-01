"""作品级个人笔记与分类存储。旧提炼字段仅保留以兼容历史文件。

存储：``<作品文件夹>/.novel-gm/notes.json`` 与 ``note-folders.json``。
正文与历史提炼字段原样保留；界面不再提炼，AI 上下文不再读取笔记。

文件不存在、字段缺失或损坏时按空笔记列表处理，不让坏文件拖垮章节/生成流程；
只有显式写操作（路由）在作品文件夹缺失时才报 ``ROOT_MISSING``。
"""
from __future__ import annotations

import json
from datetime import datetime, timezone
from threading import RLock
from typing import Any

from . import workspace as ws
from .db import new_id
from .jsonutil import loads_lenient

NOTES_FILE = "notes.json"
NOTES_VERSION = 1

STATUS_NONE = "none"
STATUS_RUNNING = "running"
STATUS_READY = "ready"
STATUS_STALE = "stale"
STATUS_FAILED = "failed"
STATUSES = (STATUS_NONE, STATUS_RUNNING, STATUS_READY, STATUS_STALE, STATUS_FAILED)
# 合并一章多篇笔记时取「最差」状态：failed > stale > running > none > ready
_STATUS_RANK = {STATUS_READY: 0, STATUS_NONE: 1, STATUS_RUNNING: 2, STATUS_STALE: 3, STATUS_FAILED: 4}

DISTILL_SYSTEM_PROMPT = """你是小说写作助手，只负责把作者的本章笔记压缩成「本章写作参考」。
要求：
1. 只使用笔记里已经写出的信息，不得扩写剧情、不得新增设定或人物。
2. 严格区分「构想」（计划、意图、待定、如果……）与「事实」（已经发生、已经确定）。
3. 用中文，短句分行，控制在 400 字以内。
4. 只输出 JSON：{"reference": "..."}，不要输出其它内容。"""

# 进程内串行：同一作品同一时间只跑一个提炼，避免重复调用模型并互相覆盖结果
_locks: dict[tuple[str, str], RLock] = {}
_distill_locks: dict[tuple[str, str], RLock] = {}
_locks_guard = RLock()
# 正在提炼的笔记；用于识别进程重启后遗留的 running 状态
_running: set[tuple[str, str, str]] = set()


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _key(project_id: str) -> tuple[str, str]:
    from . import db as db_module

    return (str(db_module.DB_PATH), project_id)


def _running_key(project_id: str, note_id: str) -> tuple[str, str, str]:
    return (_key(project_id)[0], project_id, note_id)


def _lock(project_id: str) -> RLock:
    with _locks_guard:
        return _locks.setdefault(_key(project_id), RLock())


def _distill_lock(project_id: str) -> RLock:
    with _locks_guard:
        return _distill_locks.setdefault(_key(project_id), RLock())


# ---- 存储 ----


def _empty_distill(now: str) -> dict[str, Any]:
    return {
        "status": STATUS_NONE,
        "auto_reference": "",
        "reference": "",
        "source_version": 0,
        "updated_at": now,
        "error": "",
    }


def _normalize(project_id: str, raw: dict[str, Any]) -> dict[str, Any]:
    """把磁盘上的一条记录补成完整结构；坏字段退化为默认值。"""
    distill_raw = raw.get("distill") if isinstance(raw.get("distill"), dict) else {}
    now = _now()
    note_id = str(raw.get("id") or "").strip() or new_id()
    try:
        chapter_no = int(raw.get("chapter_no") or 0)
    except (TypeError, ValueError):
        chapter_no = 0
    try:
        content_version = int(raw.get("content_version") or 1)
    except (TypeError, ValueError):
        content_version = 1
    status = str(distill_raw.get("status") or STATUS_NONE)
    if status not in STATUSES:
        status = STATUS_NONE
    try:
        source_version = int(distill_raw.get("source_version") or 0)
    except (TypeError, ValueError):
        source_version = 0
    note = {
        "id": note_id,
        "chapter_no": chapter_no,
        "folder": str(raw.get("folder") or "default"),
        "title": str(raw.get("title") or ""),
        "body": str(raw.get("body") or ""),
        "content_version": max(1, content_version),
        "created_at": str(raw.get("created_at") or now),
        "updated_at": str(raw.get("updated_at") or now),
        "pinned": raw.get("pinned") is True,
        "sort_order": raw.get("sort_order") if type(raw.get("sort_order")) is int else None,
        "distill": {
            "status": status,
            "auto_reference": str(distill_raw.get("auto_reference") or ""),
            "reference": str(distill_raw.get("reference") or ""),
            "source_version": source_version,
            "updated_at": str(distill_raw.get("updated_at") or now),
            "error": str(distill_raw.get("error") or ""),
        },
    }
    distill = note["distill"]
    # 进程重启后遗留的 running：没有对应的活动提炼任务就按失败处理，界面可重试
    if distill["status"] == STATUS_RUNNING and _running_key(project_id, note_id) not in _running:
        distill["status"] = STATUS_FAILED
        distill["error"] = "提炼被中断，请重试"
    # 正文改过（或结果缺失）就不再是 ready
    if distill["status"] in (STATUS_READY, STATUS_STALE) and not distill["auto_reference"]:
        distill["status"] = STATUS_NONE
    elif distill["status"] == STATUS_READY and distill["source_version"] != note["content_version"]:
        distill["status"] = STATUS_STALE
    return note


def _read(project_id: str) -> list[dict[str, Any]]:
    root = ws.root_of(project_id)
    if root is None or not root.is_dir():
        return []
    path = root / ws.META_DIR / NOTES_FILE
    if not path.is_file():
        return []
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, UnicodeError):
        return []
    raw = data.get("notes") if isinstance(data, dict) else None
    if not isinstance(raw, list):
        return []
    notes = [_normalize(project_id, item) for item in raw if isinstance(item, dict)]
    legacy = sorted((n for n in notes if n["sort_order"] is None),
                    key=lambda n: (n["updated_at"], n["id"]), reverse=True)
    tail = max((n["sort_order"] for n in notes if n["sort_order"] is not None), default=-1) + 1
    for index, note in enumerate(legacy):
        note["sort_order"] = tail + index
    return notes


def _read_operations(project_id: str) -> dict[str, Any]:
    root = ws.root_of(project_id)
    if root is None:
        return {}
    try:
        data = json.loads((root / ws.META_DIR / NOTES_FILE).read_text(encoding="utf-8"))
    except (OSError, ValueError, UnicodeError):
        return {}
    records = data.get("operations") if isinstance(data, dict) else None
    return records if isinstance(records, dict) else {}


def _previous_operation(records: dict[str, Any], operation_id: str | None,
                        request: dict[str, Any]) -> dict[str, Any] | None:
    if operation_id is None or operation_id not in records:
        return None
    previous = records[operation_id]
    if not isinstance(previous, dict) or previous.get("request") != request:
        raise ws.conflict("操作标识已用于其他请求，请重新执行操作", "NOTE_OPERATION")
    return previous


def _write(project_id: str, notes: list[dict[str, Any]], operations: dict[str, Any] | None = None) -> None:
    root = ws.require_root(project_id)
    # Persist the receipt and notes in the same atomic replacement. Ordinary content
    # updates must retain receipts, including across a restart or project reopen.
    records = _read_operations(project_id) if operations is None else operations
    data = {"version": NOTES_VERSION, "notes": notes}
    if records:
        data["operations"] = records
    ws.write_json(ws.meta_root(root) / NOTES_FILE, data)


def _require_root(project_id: str) -> None:
    ws.require_root(project_id)


def _find(notes: list[dict[str, Any]], note_id: str) -> dict[str, Any]:
    for note in notes:
        if note["id"] == note_id:
            return note
    from fastapi import HTTPException

    raise HTTPException(404, detail={"code": "NOT_FOUND", "message": "笔记不存在"})


def _effective(note: dict[str, Any]) -> str:
    """作者参考优先；作者没写时用自动提炼结果。"""
    distill = note["distill"]
    return distill["reference"] if distill["reference"].strip() else distill["auto_reference"]


def _sorted_chapter_notes(notes: list[dict[str, Any]], chapter_no: int) -> list[dict[str, Any]]:
    return sorted(
        (n for n in notes if n["chapter_no"] == chapter_no),
        key=lambda n: (n["created_at"], n["id"]),
    )


# ---- 笔记 CRUD ----


def list_notes(project_id: str) -> list[dict[str, Any]]:
    _require_root(project_id)
    with _lock(project_id):
        notes = _read(project_id)
    return _ordered(notes)


def _ordered(notes: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return sorted(notes, key=lambda n: (not n["pinned"], n["sort_order"], n["id"]))


def _group_head(notes: list[dict[str, Any]], pinned: bool) -> int:
    return min((n["sort_order"] for n in notes if n["pinned"] == pinned), default=0) - 1


def pin_note(project_id: str, note_id: str, pinned: bool) -> list[dict[str, Any]]:
    _require_root(project_id)
    with _lock(project_id):
        notes = _read(project_id)
        note = _find(notes, note_id)
        if note["pinned"] != pinned:
            note["sort_order"] = _group_head(notes, pinned)
            note["pinned"] = pinned
            _write(project_id, notes)
        return _ordered(notes)


def move_note(project_id: str, note_id: str, direction: str, operation_id: str | None = None) -> list[dict[str, Any]]:
    _require_root(project_id)
    if direction not in ("up", "down"):
        from fastapi import HTTPException
        raise HTTPException(400, detail={"code": "NOTE_DIRECTION", "message": "方向须为 up 或 down"})
    with _lock(project_id):
        notes = _read(project_id)
        operations = _read_operations(project_id)
        request = {"kind": "move", "note_id": note_id, "direction": direction}
        if _previous_operation(operations, operation_id, request) is not None:
            return _ordered(notes)
        note = _find(notes, note_id)
        group = [n for n in _ordered(notes) if n["pinned"] == note["pinned"]]
        index = group.index(note)
        target = index + (-1 if direction == "up" else 1)
        if 0 <= target < len(group):
            group[index], group[target] = group[target], group[index]
            for index, item in enumerate(group):
                item["sort_order"] = index
        if operation_id is not None:
            operations[operation_id] = {"request": request}
        if 0 <= target < len(group) or operation_id is not None:
            _write(project_id, notes, operations)
        return _ordered(notes)


def copy_note(project_id: str, note_id: str, title: str | None = None, operation_id: str | None = None) -> dict[str, Any]:
    _require_root(project_id)
    with _lock(project_id):
        notes = _read(project_id)
        operations = _read_operations(project_id)
        request = {"kind": "copy", "note_id": note_id, "title": title}
        previous = _previous_operation(operations, operation_id, request)
        if previous is not None:
            return _find(notes, previous["result_id"])
        source = _find(notes, note_id)
        now = _now()
        note = {"id": new_id(), "chapter_no": 0, "folder": "default",
                "title": title if title is not None else (source["title"] or "未命名") + " 副本",
                "body": source["body"], "content_version": 1, "created_at": now,
                "updated_at": now, "distill": _empty_distill(now), "pinned": False,
                "sort_order": _group_head(notes, False)}
        notes.append(note)
        if operation_id is not None:
            operations[operation_id] = {"request": request, "result_id": note["id"]}
        _write(project_id, notes, operations)
        return note


def list_folders(project_id: str) -> list[dict[str, str]]:
    root = ws.require_root(project_id)
    with _lock(project_id):
        path = root / ws.META_DIR / "note-folders.json"
        try:
            saved = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError, UnicodeError):
            saved = []
        folders = {key: {"id": key, "title": ""} for key in ("default", "foreshadow", "memo")}
        for item in saved if isinstance(saved, list) else []:
            if isinstance(item, dict) and isinstance(item.get("id"), str) and item["id"]:
                folders[item["id"]] = {"id": item["id"], "title": str(item.get("title") or "")}
        for note in _read(project_id):
            key = note["folder"]
            folders.setdefault(key, {"id": key, "title": key})
        return list(folders.values())


def save_folder(project_id: str, title: str, folder_id: str | None = None) -> dict[str, str]:
    from fastapi import HTTPException

    title = title.strip()
    if not title or len(title) > 40:
        raise HTTPException(400, detail={"code": "NOTE_FOLDER_TITLE", "message": "分类名称须为 1 至 40 个字符"})
    with _lock(project_id):
        folders = list_folders(project_id)
        folder_id = folder_id or new_id()
        match = next((f for f in folders if f["id"] == folder_id), None)
        if match is None:
            match = {"id": folder_id, "title": title}
            folders.append(match)
        else:
            match["title"] = title
        ws.write_json(ws.meta_root(ws.require_root(project_id)) / "note-folders.json", folders)
        return match


def _validate_folder(project_id: str, folder: str) -> None:
    if not any(f["id"] == folder for f in list_folders(project_id)):
        from fastapi import HTTPException
        raise HTTPException(400, detail={"code": "NOTE_FOLDER_MISSING", "message": "笔记分类不存在"})


def create_note(project_id: str, chapter_no: int = 0, title: str = "", body: str = "", folder: str = "default") -> dict[str, Any]:
    _require_root(project_id)
    _validate_folder(project_id, folder or "default")
    now = _now()
    note = {
        "id": new_id(),
        "chapter_no": int(chapter_no),
        "folder": folder or "default",
        "title": title or "",
        "body": body or "",
        "content_version": 1,
        "created_at": now,
        "updated_at": now,
        "pinned": False,
        "sort_order": 0,
        "distill": _empty_distill(now),
    }
    with _lock(project_id):
        notes = _read(project_id)
        note["sort_order"] = _group_head(notes, False)
        notes.append(note)
        _write(project_id, notes)
    return note


def update_note(project_id: str, note_id: str, title: str, body: str, content_version: int, folder: str | None = None) -> dict[str, Any]:
    _require_root(project_id)
    if folder is not None:
        _validate_folder(project_id, folder or "default")
    with _lock(project_id):
        notes = _read(project_id)
        note = _find(notes, note_id)
        if int(content_version) != note["content_version"]:
            raise ws.conflict("笔记已在别处修改，请刷新后重试", "NOTE_VERSION")
        body_changed = note["body"] != (body or "") or note["title"] != (title or "") or (folder is not None and folder != note["folder"])
        note["title"] = title or ""
        note["body"] = body or ""
        if folder is not None:
            note["folder"] = folder or "default"
        now = _now()
        note["updated_at"] = now
        if body_changed:
            note["content_version"] += 1
            distill = note["distill"]
            # 已有结果（或正在提炼）时，旧参考标记为过期但保留文本
            if distill["auto_reference"] or distill["status"] in (STATUS_READY, STATUS_STALE, STATUS_RUNNING):
                if distill["status"] != STATUS_RUNNING:
                    distill["status"] = STATUS_STALE
                distill["updated_at"] = now
        _write(project_id, notes)
    return note


def delete_note(project_id: str, note_id: str) -> dict[str, bool]:
    _require_root(project_id)
    with _lock(project_id):
        notes = _read(project_id)
        note = _find(notes, note_id)
        notes.remove(note)
        _write(project_id, notes)
    return {"ok": True}


def set_reference(project_id: str, note_id: str, reference: str) -> dict[str, Any]:
    """作者手写参考：不动正文版本，也不动自动提炼结果。"""
    _require_root(project_id)
    with _lock(project_id):
        notes = _read(project_id)
        note = _find(notes, note_id)
        note["distill"]["reference"] = reference or ""
        note["distill"]["updated_at"] = _now()
        _write(project_id, notes)
    return note


# ---- 提炼 ----


def _error_text(exc: Exception) -> str:
    detail = getattr(exc, "detail", None)
    if isinstance(detail, dict):
        message = detail.get("message") or detail.get("code")
        if message:
            return str(message)
    if isinstance(detail, str) and detail:
        return detail
    text = str(exc).strip()
    return text.splitlines()[0][:300] if text else type(exc).__name__


def _call_model(project: Any, note: dict[str, Any]) -> str:
    from .providers import model_for, resolve_for_project
    from .repository import add_usage

    provider, hint = resolve_for_project(project)
    model = model_for("note_distill", hint)  # STRONG_PURPOSES 不含该用途 → 自动走快速模型
    messages = [
        {"role": "system", "content": DISTILL_SYSTEM_PROMPT},
        {
            "role": "user",
            "content": (
                f"第{note['chapter_no']}章笔记\n"
                f"标题：{note['title'] or '（无）'}\n"
                f"正文：\n{note['body']}"
            ),
        },
    ]
    req = {
        "model": model,
        "messages": messages,  # type: ignore[typeddict-item]
        "temperature": 0.2,
        "top_p": 0.9,
        "json_mode": True,
        "thinking": False,
        "purpose": "note_distill",
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
        "note_distill",
    )
    data = loads_lenient(resp.get("content") or "")
    reference = data.get("reference") if isinstance(data, dict) else None
    if not isinstance(reference, str) or not reference.strip():
        raise ValueError("模型没有返回有效的参考（需要 JSON 字段 reference）")
    return reference.strip()


def _distill_result(note: dict[str, Any], error: str = "") -> dict[str, Any]:
    distill = note["distill"]
    return {
        "note_id": note["id"],
        "status": distill["status"],
        "auto_reference": distill["auto_reference"],
        "content_version": note["content_version"],
        "error": error or distill["error"],
    }


def distill_note(project_id: str, note_id: str, content_version: int, chapter_no: int = 0) -> dict[str, Any]:
    """同步提炼一篇笔记；模型不可用时返回 failed 而不是抛 500。"""
    _require_root(project_id)
    key = _running_key(project_id, note_id)
    with _distill_lock(project_id):
        with _lock(project_id):
            notes = _read(project_id)
            note = _find(notes, note_id)
            if int(content_version) != note["content_version"]:
                raise ws.conflict("笔记已在别处修改，请刷新后重试", "NOTE_VERSION")
            now = _now()
            note["distill"]["status"] = STATUS_RUNNING
            note["distill"]["error"] = ""
            note["distill"]["updated_at"] = now
            _write(project_id, notes)
            _running.add(key)
            from .repository import get_project

            project = get_project(project_id)
        try:
            reference = _call_model(project, note)
        except Exception as exc:  # noqa: BLE001 — 模型不可用/返回坏格式都按 failed 返回
            with _lock(project_id):
                notes = _read(project_id)
                latest = _find(notes, note_id)
                latest["distill"]["status"] = STATUS_FAILED
                latest["distill"]["error"] = _error_text(exc)
                latest["distill"]["updated_at"] = _now()
                _write(project_id, notes)
                _running.discard(key)
                return _distill_result(latest)
        with _lock(project_id):
            notes = _read(project_id)
            latest = _find(notes, note_id)
            _running.discard(key)
            if latest["content_version"] != int(content_version):
                # 提炼期间笔记被改：结果作废，保留旧 auto_reference
                latest["distill"]["status"] = STATUS_STALE
                latest["distill"]["updated_at"] = _now()
                _write(project_id, notes)
                raise ws.conflict("笔记已修改，本次提炼结果未写入", "NOTE_VERSION")
            latest["distill"]["status"] = STATUS_READY
            latest["distill"]["auto_reference"] = reference
            latest["distill"]["source_version"] = int(content_version)
            latest["distill"]["error"] = ""
            latest["distill"]["updated_at"] = _now()
            _write(project_id, notes)
            return _distill_result(latest)


# ---- 参考汇总 ----


def reference_view(project_id: str, chapter_no: int) -> dict[str, Any]:
    _require_root(project_id)
    with _lock(project_id):
        notes = _read(project_id)
    chapter_notes = _sorted_chapter_notes(notes, int(chapter_no))
    entries = [
        {
            "note_id": note["id"],
            "title": note["title"],
            "status": note["distill"]["status"],
            "reference": _effective(note),
        }
        for note in chapter_notes
    ]
    status = STATUS_NONE
    if chapter_notes:
        status = max(
            (note["distill"]["status"] for note in chapter_notes),
            key=lambda s: _STATUS_RANK.get(s, 1),
        )
    error = next(
        (
            note["distill"]["error"]
            for note in chapter_notes
            if note["distill"]["status"] == STATUS_FAILED and note["distill"]["error"]
        ),
        "",
    )
    return {
        "chapter_no": int(chapter_no),
        "status": status,
        "reference": "\n\n".join(e for e in (entry["reference"] for entry in entries) if e.strip()),
        "error": error,
        "notes": entries,
    }


def chapter_reference(project_id: str, chapter_no: int) -> str:
    """生成/规划用：该章所有笔记的有效参考按笔记顺序拼接；取不到时返回空串。"""
    try:
        with _lock(project_id):
            notes = _read(project_id)
    except Exception:  # noqa: BLE001 — 参考只是增强信息，不能挡住生成
        return ""
    chapter_notes = _sorted_chapter_notes(notes, int(chapter_no))
    return "\n\n".join(
        ref for ref in (_effective(note) for note in chapter_notes) if ref.strip()
    )


# ---- 章节号平移（供 repository.delete_chapter / insert_chapter 调用） ----


def shift(project_id: str, from_no: int, delta: int) -> None:
    """笔记现为作品级独立资料，章节调整不再移动或删除笔记。"""
    return
