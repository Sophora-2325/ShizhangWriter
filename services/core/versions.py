"""章节历史版本。

存储：``<作品文件夹>/.novel-gm/versions/<no:04d>/<version_id>.json``，每章一个目录，
版本 id 是「时间戳 + 序号」（如 ``20261018-193012-001``），按 id 排序即时间顺序。

只保存正文（``body_text`` + ``body_rich``）；图片仍引用作品资源，不复制。
同时兼容旧版纯文本备份 ``<作品文件夹>/.novel-gm/history/<sha256(文件名)[:16]>/<sha256(正文)>.md``，
以只读的 ``legacy-text`` 版本返回（``format_available=false``），不写盘。
"""
from __future__ import annotations

import hashlib
import shutil
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

from fastapi import HTTPException

from . import workspace as ws
from .db import connect, utcnow
from .models import ChapterStatus

VERSIONS_DIR = "versions"
HISTORY_DIR = "history"
MAX_VERSIONS = 100
AUTOSAVE_WINDOW_MINUTES = 5
SOURCES = ("original", "autosave", "ai", "finalize", "restore-backup", "manual", "legacy-text")
LEGACY_SOURCE = "legacy-text"


def version_hash(body_text: str, body_rich: str) -> str:
    return hashlib.sha256((body_text + "\0" + body_rich).encode("utf-8")).hexdigest()


def _root_or_none(project_id: str) -> Optional[Path]:
    """版本是作品文件夹里的数据；旧作品/文件夹丢失时安静地当作没有版本。"""
    root = ws.root_of(project_id)
    if root is None or not root.is_dir():
        return None
    return root


def _versions_root(root: Path) -> Path:
    return root / ws.META_DIR / VERSIONS_DIR


def _chapter_dir(root: Path, no: int) -> Path:
    return _versions_root(root) / f"{no:04d}"


def _parse_ts(value: Any) -> Optional[datetime]:
    try:
        ts = datetime.fromisoformat(str(value))
    except (TypeError, ValueError):
        return None
    if ts.tzinfo is None:
        ts = ts.replace(tzinfo=timezone.utc)
    return ts


def _normalize(data: dict[str, Any], no: int) -> dict[str, Any]:
    chapter_no = int(data.get("chapter_no") or no)
    body_text = str(data.get("body_text") or "")
    body_rich = str(data.get("body_rich") or "")
    source = str(data.get("source") or "manual")
    if source not in SOURCES:
        source = "manual"
    try:
        word_count = int(data.get("word_count") or len(body_text))
    except (TypeError, ValueError):
        word_count = len(body_text)
    return {
        "id": str(data.get("id") or ""),
        "chapter_no": chapter_no,
        "title_at_save": str(data.get("title_at_save") or f"第{chapter_no}章"),
        "body_text": body_text,
        "body_rich": body_rich,
        "saved_at": str(data.get("saved_at") or ""),
        "source": source,
        "word_count": word_count,
        "hash": str(data.get("hash") or version_hash(body_text, body_rich)),
        "format_available": bool(data.get("format_available", True)),
    }


def _read_file(path: Path, no: int) -> Optional[dict[str, Any]]:
    try:
        raw = ws.read_json(path)
    except (OSError, ValueError, UnicodeError):
        return None
    if not isinstance(raw, dict) or (not str(raw.get("body_text", "")).strip() and not raw.get("body_rich")):
        return None
    item = _normalize(raw, no)
    if not item["id"]:
        item["id"] = path.stem
    return item


def _version_files(root: Path, no: int) -> list[Path]:
    d = _chapter_dir(root, no)
    if not d.is_dir():
        return []
    # id 是「时间戳+序号」，按文件名倒序即最新在前
    return sorted((p for p in d.glob("*.json") if p.is_file()), key=lambda p: p.stem, reverse=True)


def _read_new_versions(root: Path, no: int) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for path in _version_files(root, no):
        item = _read_file(path, no)
        if item is not None:
            out.append(item)
    return out


def _chapter_meta(project_id: str, no: int) -> tuple[str, str]:
    """(标题, 当前正文文件名)；查不到时返回空串。"""
    with connect() as conn:
        row = conn.execute(
            "SELECT title, body_path FROM chapters WHERE project_id=? AND no=?", (project_id, no)
        ).fetchone()
    if not row:
        return "", ""
    return row["title"] or "", row["body_path"] or ""


def _legacy_entries(root: Path, project_id: str, no: int) -> list[dict[str, Any]]:
    """旧版纯文本备份（只读）。

    归属只认「当前正文文件名」的 sha256 前 16 位；改过名的章节没有曾用名记录，
    旧名字的 history 目录无法确认归属，因此不展示（宁可少列，不能挂错章）。
    收敛规则（避免部署后新写的 history 混进版本列表）：
    - 与任意新版本的 body_text 完全相同 → 跳过（纯文本去重，不只比 hash）。
    - 该章已有新版本时，只保留 mtime 早于「最早一条新版本 saved_at」的旧文件。
    - 该章还没有任何新版本时全部展示（按 mtime 倒序，最多 10 条，与旧机制保留数一致）。
    """
    title, body_path = _chapter_meta(project_id, no)
    if not body_path:
        return []
    own = hashlib.sha256(Path(body_path).name.encode()).hexdigest()[:16]
    d = root / ws.META_DIR / HISTORY_DIR / own
    if not d.is_dir():
        return []
    new_versions = _read_new_versions(root, no)
    new_texts = {item["body_text"] for item in new_versions}
    known_hashes = {item["hash"] for item in new_versions}
    cutoff: Optional[datetime] = None
    if new_versions:
        stamps = [ts for ts in (_parse_ts(item["saved_at"]) for item in new_versions) if ts is not None]
        if not stamps:
            return []  # 有新版本却读不到时间：保守不展示旧备份
        cutoff = min(stamps)

    entries: list[dict[str, Any]] = []
    for path in d.glob("*.md"):
        try:
            text = path.read_text(encoding="utf-8")
            mtime = path.stat().st_mtime
        except (OSError, UnicodeError):
            continue
        if not text.strip():
            continue  # 空备份没有恢复价值，也不该在列表里显示「仅文本 0 字」
        if text in new_texts:
            continue  # 与某个新版本正文完全相同 → 纯文本去重
        if cutoff is not None and mtime >= cutoff.timestamp():
            continue  # 新版本出现之后写的 history：属于新机制，不再当旧备份展示
        digest = version_hash(text, "")
        if digest in known_hashes:
            continue
        try:
            saved_at = datetime.fromtimestamp(mtime, timezone.utc).isoformat()
        except (OSError, OverflowError, ValueError):
            saved_at = ""
        entries.append(
            {
                "id": f"legacy-{path.stem[:16]}",
                "chapter_no": no,
                "title_at_save": title or f"第{no}章",
                "body_text": text,
                "body_rich": "",
                "saved_at": saved_at,
                "source": LEGACY_SOURCE,
                "word_count": len(text),
                "hash": digest,
                "format_available": False,
            }
        )
    entries.sort(key=lambda item: (item["saved_at"], item["id"]), reverse=True)
    if cutoff is None:
        entries = entries[:10]  # 没有新版本时最多展示 10 条旧备份
    return entries


def _all_versions(root: Path, project_id: str, no: int) -> list[dict[str, Any]]:
    versions = _read_new_versions(root, no) + _legacy_entries(root, project_id, no)
    versions.sort(key=lambda v: (v["saved_at"], v["id"]), reverse=True)
    return versions


# ---- 对外查询 ----


def list_versions(project_id: str, no: int) -> list[dict[str, Any]]:
    root = _root_or_none(project_id)
    if root is None:
        return []
    return [
        {
            "id": v["id"],
            "chapter_no": v["chapter_no"],
            "title_at_save": v["title_at_save"],
            "saved_at": v["saved_at"],
            "source": v["source"],
            "word_count": v["word_count"],
            "format_available": v["format_available"],
        }
        for v in _all_versions(root, project_id, no)
    ]


def get_version(project_id: str, no: int, version_id: str) -> dict[str, Any]:
    root = _root_or_none(project_id)
    if root is None:
        raise HTTPException(404, detail={"code": "NOT_FOUND", "message": "版本不存在"})
    path = _chapter_dir(root, no) / f"{version_id}.json"
    if path.is_file():
        item = _read_file(path, no)
        if item is not None:
            item["id"] = item["id"] or version_id
            return item
    for item in _legacy_entries(root, project_id, no):
        if item["id"] == version_id:
            return item
    raise HTTPException(404, detail={"code": "NOT_FOUND", "message": "版本不存在"})


# ---- 检查点 ----


def _latest_new_version(root: Path, no: int) -> Optional[dict[str, Any]]:
    for path in _version_files(root, no):
        item = _read_file(path, no)
        if item is not None:
            return item
    return None


def _next_id(directory: Path, now: datetime) -> str:
    """同一秒内用序号保证 id 唯一且单调（裁剪不回收旧序号，避免 id 回绕）。"""
    base = now.strftime("%Y%m%d-%H%M%S")
    max_seq = 0
    for path in directory.glob(f"{base}-*.json"):
        try:
            max_seq = max(max_seq, int(path.stem.rsplit("-", 1)[1]))
        except (IndexError, ValueError):
            continue
    return f"{base}-{max_seq + 1:03d}"


def _trim(directory: Path) -> None:
    files = sorted((p for p in directory.glob("*.json") if p.is_file()), key=lambda p: p.stem, reverse=True)
    for expired in files[MAX_VERSIONS:]:
        expired.unlink(missing_ok=True)


def checkpoint(
    project_id: str,
    no: int,
    body_text: str,
    body_rich: str,
    source: str = "manual",
) -> Optional[dict[str, Any]]:
    """在正文真正被覆盖前记录一版；hash 相同或命中自动保存窗口时跳过。"""
    root = _root_or_none(project_id)
    if root is None:
        return None
    body_text = body_text or ""
    body_rich = body_rich or ""
    if not body_text.strip() and not body_rich.strip():
        return None  # 空稿没有保存价值（首次生成、清空后的空章）
    if source not in SOURCES or source == LEGACY_SOURCE:
        source = "manual"
    digest = version_hash(body_text, body_rich)
    latest = _latest_new_version(root, no)
    if latest is not None and latest["hash"] == digest:
        return None
    if source == "autosave" and latest is not None and latest["source"] == "autosave":
        last_saved = _parse_ts(latest["saved_at"])
        if last_saved is not None:
            minutes = (datetime.now(timezone.utc) - last_saved).total_seconds() / 60.0
            if minutes < AUTOSAVE_WINDOW_MINUTES:
                return None  # 连续自动保存只留 5 分钟一版
    title, _body_path = _chapter_meta(project_id, no)
    now = utcnow()
    directory = _chapter_dir(root, no)
    directory.mkdir(parents=True, exist_ok=True)
    version_id = _next_id(directory, now)
    data = {
        "id": version_id,
        "chapter_no": no,
        "title_at_save": title or f"第{no}章",
        "body_text": body_text,
        "body_rich": body_rich,
        "saved_at": now.isoformat(),
        "source": source,
        "word_count": len(body_text),
        "hash": digest,
        "format_available": True,
    }
    ws.write_json(directory / f"{version_id}.json", data)
    _trim(directory)
    return data


def checkpoint_save(project_id: str, no: int, body_text: str, body_rich: str) -> Optional[dict[str, Any]]:
    """``api_save_body`` 用：首次改动记 original 保留原稿，之后按 autosave 节流。"""
    root = _root_or_none(project_id)
    if root is None:
        return None
    latest = _latest_new_version(root, no)
    source = "original" if latest is None or latest["source"] not in ("original", "autosave") else "autosave"
    return checkpoint(project_id, no, body_text, body_rich, source)


# ---- 恢复 ----


def restore(project_id: str, no: int, version_id: str) -> Any:
    """恢复到指定版本：先备份当前稿，再走正常 ``upsert_chapter`` 链路（派生结果自动失效）。"""
    from .repository import get_chapter, upsert_chapter

    chapter = get_chapter(project_id, no)
    if chapter.status == ChapterStatus.superseded:
        raise ws.conflict("已作废章节不可修改", "SUPERSEDED")
    version = get_version(project_id, no, version_id)
    checkpoint(project_id, no, chapter.body_text, chapter.body_rich, "restore-backup")
    chapter.body_text = version.get("body_text") or ""
    chapter.body_rich = (version.get("body_rich") or "") if version.get("format_available", True) else ""
    chapter.word_count = version.get("word_count") or len(chapter.body_text)
    # 标题/状态/计划都不动；正文变化由 upsert_chapter 的 freshness 逻辑失效派生结果
    upsert_chapter(project_id, chapter)
    return get_chapter(project_id, no)


# ---- 章节号平移（供 repository.delete_chapter / insert_chapter 调用） ----


def shift(project_id: str, from_no: int, delta: int) -> None:
    """整目录平移版本。

    delta < 0：删除第 from_no 章——移除该章版本目录，后面的目录依次前移。
    delta > 0：在 from_no 之前插入——章节号 >= from_no 的目录整体后移 delta。
    """
    if delta == 0:
        return
    root = _root_or_none(project_id)
    if root is None:
        return
    base = _versions_root(root)
    if not base.is_dir():
        return
    dirs = sorted(
        (int(p.name), p) for p in base.iterdir() if p.is_dir() and p.name.isdigit()
    )
    if delta < 0:
        removed = base / f"{from_no:04d}"
        if removed.is_dir():
            shutil.rmtree(removed)
        for number, path in ((n, p) for n, p in dirs if n > from_no):
            path.rename(base / f"{number - 1:04d}")
    else:
        for number, path in reversed([(n, p) for n, p in dirs if n >= from_no]):
            path.rename(base / f"{number + delta:04d}")
