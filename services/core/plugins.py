"""插件管理：storage/plugins/<id>/plugin.json。只登记、启用、停用、删除，不执行任何插件代码。

启用状态存在 app_settings.plugins_enabled（JSON 数组）。
"""
from __future__ import annotations

import json
import re
import shutil
from pathlib import Path
from typing import Any, Optional

from fastapi import HTTPException
from pydantic import BaseModel, Field

from . import db

MANIFEST = "plugin.json"
REQUIRED = ("id", "name", "version")
_ID_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]*")
_ENABLED_KEY = "plugins_enabled"


class Plugin(BaseModel):
    id: str
    name: str
    version: str
    description: str = ""
    author: str = ""
    hooks: list[str] = Field(default_factory=list)
    enabled: bool = False


def plugins_dir() -> Path:
    return db.STORAGE / "plugins"


def _bad(message: str, code: str = "BAD_PLUGIN") -> HTTPException:
    return HTTPException(400, detail={"code": code, "message": message})


def _not_found() -> HTTPException:
    return HTTPException(404, detail={"code": "NOT_FOUND", "message": "插件不存在"})


def parse_manifest(text: str) -> Plugin:
    try:
        raw: Any = json.loads(text.lstrip("\ufeff"))
    except ValueError as e:
        raise _bad(f"plugin.json 不是有效的 JSON：{e}", "BAD_JSON")
    if not isinstance(raw, dict):
        raise _bad("plugin.json 不是有效的 JSON：not an object", "BAD_JSON")
    missing = [k for k in REQUIRED if not str(raw.get(k) or "").strip()]
    if missing:
        raise _bad(f"plugin.json 缺少必填字段：{', '.join(missing)}", "REQUIRED")
    pid = str(raw["id"]).strip()
    if not _ID_RE.fullmatch(pid):
        raise _bad("插件 id 只能包含字母、数字、点、下划线和连字符", "BAD_ID")
    hooks = raw.get("hooks") or []
    return Plugin(
        id=pid,
        name=str(raw["name"]).strip(),
        version=str(raw["version"]).strip(),
        description=str(raw.get("description") or "").strip(),
        author=str(raw.get("author") or "").strip(),
        hooks=[str(h) for h in hooks] if isinstance(hooks, list) else [],
    )


def _enabled() -> list[str]:
    with db.connect() as conn:
        raw = db.get_setting(conn, _ENABLED_KEY, "[]")
    try:
        ids = json.loads(raw)
    except ValueError:
        return []
    return [i for i in ids if isinstance(i, str)]


def _save_enabled(ids: list[str]) -> None:
    with db.connect() as conn:
        db.set_setting(conn, _ENABLED_KEY, json.dumps(sorted(set(ids)), ensure_ascii=False))


def _load(folder: Path, enabled: set[str]) -> Optional[Plugin]:
    try:
        plugin = parse_manifest((folder / MANIFEST).read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, HTTPException):
        return None
    plugin.enabled = plugin.id in enabled
    return plugin


def _dir_of(plugin_id: str) -> Path:
    if not _ID_RE.fullmatch(plugin_id):
        raise _not_found()
    d = plugins_dir() / plugin_id
    if not (d / MANIFEST).is_file():
        raise _not_found()
    return d


def list_plugins() -> list[Plugin]:
    root = plugins_dir()
    if not root.is_dir():
        return []
    enabled = set(_enabled())
    found = [p for d in sorted(root.iterdir()) if d.is_dir() and (p := _load(d, enabled))]
    return sorted(found, key=lambda p: p.name.lower())


def get_plugin(plugin_id: str) -> Plugin:
    plugin = _load(_dir_of(plugin_id), set(_enabled()))
    if plugin is None:
        raise _not_found()
    return plugin


def import_plugin(path_s: str) -> Plugin:
    """导入含 plugin.json 的文件夹（也接受直接选中的 plugin.json），整个文件夹复制到 plugins/<id>。"""
    src = Path((path_s or "").strip())
    if not path_s.strip() or not src.exists():
        raise _bad(f"找不到要导入的文件或文件夹：{path_s}", "NOT_FOUND")
    if src.is_file() and src.name.lower() == MANIFEST:
        src = src.parent
    if not (src.is_dir() and (src / MANIFEST).is_file()):
        raise _bad("文件夹里没有 plugin.json", "NO_MANIFEST")
    plugin = parse_manifest((src / MANIFEST).read_text(encoding="utf-8"))
    dest = plugins_dir() / plugin.id
    if dest.exists():
        raise _bad(f"已存在同 id 的插件：{plugin.id}", "DUPLICATE")
    plugins_dir().mkdir(parents=True, exist_ok=True)
    shutil.copytree(src, dest)
    return get_plugin(plugin.id)


def set_enabled(plugin_id: str, enabled: bool) -> Plugin:
    _dir_of(plugin_id)
    ids = [i for i in _enabled() if i != plugin_id]
    if enabled:
        ids.append(plugin_id)
    _save_enabled(ids)
    return get_plugin(plugin_id)


def delete_plugin(plugin_id: str) -> None:
    shutil.rmtree(_dir_of(plugin_id))
    _save_enabled([i for i in _enabled() if i != plugin_id])
