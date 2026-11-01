"""作品文件夹读写。

目录结构（文件夹是数据来源，novel.db 只做索引与缓存）::

    <作品名>/
      作品.json                 基本信息与作品级设定
      <章节名>.md               纯正文
      人物画像/<角色id>.<ext>
      导出/
      .novel-gm/chapters/NNNN.json   章节元数据（计划、摘要、闸门、本章设定快照…）
      .novel-gm/facts.json
"""
from __future__ import annotations

import json
import os
import re
import sys
import tempfile
import hashlib
from pathlib import Path
from typing import Any, Iterable, Optional

from fastapi import HTTPException

from .db import connect, loads

PROJECT_FILE = "作品.json"
META_DIR = ".novel-gm"
PORTRAIT_DIR = "人物画像"
EXPORT_DIR = "导出"
FORMAT_VERSION = 1
IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"}

_ILLEGAL = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_RESERVED = {"CON", "PRN", "AUX", "NUL"} | {f"COM{i}" for i in range(1, 10)} | {f"LPT{i}" for i in range(1, 10)}


def sanitize_name(name: str, fallback: str = "未命名") -> str:
    s = _ILLEGAL.sub("_", (name or "").strip()).rstrip(" .")
    if not s:
        s = fallback
    if s.upper() in _RESERVED:
        s = f"_{s}"
    return s[:120]


def conflict(message: str, code: str = "CONFLICT") -> HTTPException:
    return HTTPException(409, detail={"code": code, "message": message})


def bad_request(message: str, code: str = "BAD_REQUEST") -> HTTPException:
    return HTTPException(400, detail={"code": code, "message": message})


# ---- roots ----


def root_of(project_id: str) -> Optional[Path]:
    with connect() as conn:
        row = conn.execute("SELECT root_path FROM projects WHERE id=?", (project_id,)).fetchone()
    if not row or not row["root_path"]:
        return None
    return Path(row["root_path"])


def check_root(root: Optional[Path]) -> Optional[Path]:
    """None 表示旧作品；文件夹丢失时报错，避免只写数据库导致两边不一致。"""
    if root is not None and not root.is_dir():
        raise conflict(f"作品文件夹缺失：{root}", "ROOT_MISSING")
    return root


def require_root(project_id: str) -> Path:
    root = root_of(project_id)
    if root is None:
        raise conflict("该作品尚未迁移到作品文件夹", "NOT_MIGRATED")
    return check_root(root)  # type: ignore[return-value]


def backup_zip(root: Path, title: str) -> Path:
    """把作品文件夹打成 zip 放进「导出」；「导出」本身不打包，免得备份越来越大。"""
    import zipfile
    from datetime import datetime

    out_dir = root / EXPORT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M")
    base = f"备份-{sanitize_name(title, '未命名作品')}-{stamp}"
    out = out_dir / f"{base}.zip"
    n = 2
    while out.exists():
        out = out_dir / f"{base}-{n}.zip"
        n += 1
    with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for path in sorted(root.rglob("*")):
            rel = path.relative_to(root)
            if rel.parts[0] == EXPORT_DIR or not path.is_file():
                continue
            z.write(path, Path(root.name) / rel)
    return out


def new_folder(parent_dir: str, title: str) -> Path:
    parent_s = (parent_dir or "").strip()
    if not parent_s or not Path(parent_s).is_absolute():
        raise bad_request("请选择作品保存位置", "PARENT_REQUIRED")
    parent = Path(parent_s)
    parent.mkdir(parents=True, exist_ok=True)
    root = parent / sanitize_name(title, "未命名作品")
    if root.exists() and any(root.iterdir()):
        raise conflict(f"文件夹已存在且不为空：{root}", "FOLDER_EXISTS")
    root.mkdir(parents=True, exist_ok=True)
    return root


# ---- json helpers ----


def write_json(path: Path, data: Any) -> None:
    atomic_text(path, json.dumps(data, ensure_ascii=False, indent=2, default=str))


def atomic_text(path: Path, text: str) -> None:
    """A failed replacement leaves the previous file intact; temp is on the same volume."""
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix="." + path.name + ".", suffix=".tmp", dir=path.parent)
    tmp = Path(name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="") as stream:
            stream.write(text)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(tmp, path)
    finally:
        tmp.unlink(missing_ok=True)


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _hide(path: Path) -> None:
    if sys.platform == "win32":
        try:
            import ctypes

            ctypes.windll.kernel32.SetFileAttributesW(str(path), 0x02)
        except Exception:
            pass


def meta_root(root: Path) -> Path:
    d = root / META_DIR
    if not d.exists():
        d.mkdir(parents=True, exist_ok=True)
        _hide(d)
    return d


def chapters_meta_dir(root: Path) -> Path:
    d = meta_root(root) / "chapters"
    d.mkdir(parents=True, exist_ok=True)
    return d


def meta_path(root: Path, no: int) -> Path:
    return chapters_meta_dir(root) / f"{no:04d}.json"


# ---- project file ----


def write_project_file(root: Path, project_data: dict[str, Any]) -> None:
    data = {k: v for k, v in project_data.items() if k not in ("root_path", "root_missing")}
    data["format"] = FORMAT_VERSION
    write_json(root / PROJECT_FILE, data)


def read_project_file(root: Path) -> dict[str, Any]:
    pf = root / PROJECT_FILE
    if not pf.is_file():
        raise bad_request(f"所选文件夹里没有 {PROJECT_FILE}，不是 novel-gm 作品文件夹", "NOT_A_WORK")
    data = read_json(pf)
    data.pop("format", None)
    return data


# ---- chapter files ----


def _file_matches(file_name: str, base: str) -> bool:
    stem = Path(file_name).stem
    return stem == base or re.fullmatch(re.escape(base) + r" \(\d+\)", stem) is not None


def chapter_file_name(title: str, taken: Iterable[str], current: Optional[str] = None) -> str:
    """按标题生成不与其他章节重名的 .md 文件名；已有文件名仍匹配标题时保持不变。"""
    base = sanitize_name(title, "未命名章节")
    taken_l = {t.lower() for t in taken if t}
    if current and _file_matches(current, base) and current.lower() not in taken_l:
        return current
    name = f"{base}.md"
    i = 2
    while name.lower() in taken_l:
        name = f"{base} ({i}).md"
        i += 1
    return name


def write_chapter_body(root: Path, file_name: str, body: str, old_name: Optional[str]) -> None:
    new_path = root / file_name
    old_path = root / (old_name or file_name)
    if old_path.is_file():
        previous = old_path.read_text(encoding="utf-8")
        if previous != (body or "") or old_name != file_name:
            history = meta_root(root) / "history" / hashlib.sha256(old_path.name.encode()).hexdigest()[:16]
            revision = hashlib.sha256(previous.encode()).hexdigest()
            atomic_text(history / (revision + ".md"), previous)
            for expired in sorted(history.glob("*.md"), key=lambda p: p.stat().st_mtime, reverse=True)[10:]:
                expired.unlink()
    atomic_text(new_path, body or "")


def finish_chapter_rename(root: Path, file_name: str, old_name: Optional[str]) -> None:
    # Called only after body, metadata and SQLite have committed.
    if old_name and old_name.casefold() != file_name.casefold():
        (root / old_name).unlink(missing_ok=True)


def read_chapter_body(root: Path, file_name: str) -> str:
    if not file_name:
        return ""
    p = root / file_name
    return p.read_text(encoding="utf-8") if p.is_file() else ""


def write_chapter_meta(root: Path, chapter_data: dict[str, Any], file_name: str) -> None:
    data = {k: v for k, v in chapter_data.items() if k != "body_text"}
    data["file"] = file_name
    write_json(meta_path(root, int(data["no"])), data)


def remove_chapter_files(root: Path, no: int, file_name: Optional[str]) -> None:
    mp = chapters_meta_dir(root) / f"{no:04d}.json"
    if mp.exists():
        mp.unlink()
    if file_name:
        body = root / file_name
        if body.exists():
            body.unlink()


def read_chapter_metas(root: Path) -> list[tuple[dict[str, Any], str]]:
    d = root / META_DIR / "chapters"
    if not d.is_dir():
        return []
    out = []
    for mp in sorted(d.glob("*.json")):
        meta = read_json(mp)
        file_name = meta.pop("file", "") or ""
        meta["body_text"] = read_chapter_body(root, file_name)
        out.append((meta, file_name))
    return out


# ---- facts ----


def write_facts_file(project_id: str) -> None:
    root = root_of(project_id)
    if root is None or not root.is_dir():
        return
    with connect() as conn:
        rows = conn.execute(
            "SELECT id, chapter_no, fact, known_by_json, source_hash FROM facts WHERE project_id=? ORDER BY rowid",
            (project_id,),
        ).fetchall()
    data = [
        {"id": r["id"], "chapter_no": r["chapter_no"], "fact": r["fact"], "known_by": loads(r["known_by_json"]) or [], "source_hash": r["source_hash"]}
        for r in rows
    ]
    write_json(meta_root(root) / "facts.json", data)


def read_facts_file(root: Path) -> list[dict[str, Any]]:
    p = root / META_DIR / "facts.json"
    return read_json(p) if p.is_file() else []


# ---- portraits ----


def copy_portrait(root: Path, character_id: str, source: str) -> str:
    src = Path((source or "").strip())
    if not src.is_file():
        raise bad_request("找不到所选图片", "NO_IMAGE")
    ext = src.suffix.lower()
    if ext not in IMAGE_EXTS:
        raise bad_request("只支持 png / jpg / gif / bmp / webp 图片", "BAD_IMAGE")
    d = root / PORTRAIT_DIR
    d.mkdir(parents=True, exist_ok=True)
    cid = sanitize_name(character_id, "portrait")
    for old in d.glob(f"{cid}.*"):
        if old.suffix.lower() != ext:
            old.unlink()
    dest = d / f"{cid}{ext}"
    if src.resolve() != dest.resolve():
        dest.write_bytes(src.read_bytes())
    return f"{PORTRAIT_DIR}/{dest.name}"
