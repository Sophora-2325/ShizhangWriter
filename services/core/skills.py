"""写作 Skills：storage/skills/<id>/SKILL.md，启用后按用途加进规划 / 生成正文的提示词。

SKILL.md = frontmatter（name、description、applies_to、scenes、source）+ 正文（写作指令）。
scenes 为空的 Skill 始终生效；不为空时只在本章戏份与之有交集时生效。
启用状态存在 app_settings.skills_enabled（JSON 数组），对所有作品生效。

内置 Skill 放进用户目录时记下内容哈希（skills_builtin_hashes）。内置版本更新后：
用户没改过的副本自动换成新版；改过的保留原样，标记「有新版本」，可手动恢复为内置版本。
"""
from __future__ import annotations

import hashlib
import json
import logging
import re
import shutil
from pathlib import Path
from typing import Iterable, Literal, Optional

from fastapi import HTTPException
from pydantic import BaseModel, Field

from . import db

log = logging.getLogger("uvicorn.error")

SKILL_FILE = "SKILL.md"
APPLIES = ("plan", "generate", "both")
Applies = Literal["plan", "generate", "both"]

# 戏份类型；客户端 Strings 里每个都要有 Scene.<key>
SCENES = ("action", "romance", "suspense", "daily", "sorrow", "confrontation")
_SCENE_ALIASES = {
    "打斗": "action", "战斗": "action", "动作": "action", "武戏": "action",
    "感情": "romance", "爱情": "romance", "情感": "romance",
    "悬疑": "suspense", "推理": "suspense", "解谜": "suspense",
    "日常": "daily", "轻松": "daily",
    "悲情": "sorrow", "悲伤": "sorrow", "虐": "sorrow",
    "对峙": "confrontation", "谈判": "confrontation", "争吵": "confrontation", "权谋": "confrontation",
}

BUILTIN_DIR = Path(__file__).resolve().parent / "builtin_skills"
_LEGACY_EXAMPLES = ("dialogue-natural", "chapter-hook")

_ENABLED_KEY = "skills_enabled"
_SEEDED_KEY = "skills_seeded"
_OFFERED_KEY = "skills_builtin_offered"
_HASHES_KEY = "skills_builtin_hashes"

# 第一版内置 Skill 的内容哈希。那时还不记哈希，老用户的副本靠它判断有没有改过。
_V1_HASHES = {
    "chapter-frame": "6e84d6262be912c6b3ef554a1ce6207962bb52edfd532e915edde54bdd83904b",
    "chapter-hook": "44b6769c74f5bcea69ddd6426ae94a181c83deb218f9f0cd6580024ad5d971a4",
    "de-ai": "1fc5c92fb0a94f9d3d87340623d0818c2936f57929606aed2a104a862a21ba54",
    "dialogue-natural": "7ee85d0f280e40bf170c6c5706878b377aabf686ec81dbcc092ef69541b114c8",
    "narrative-order": "6e50783455a788500bb60d65d13fb5f067fd9af7a5c069bd154fed9692e8e35a",
    "style-action": "bcb1bab794aee675ac60e9fb83e5836a01c531654e4428cf8b329409bea85beb",
    "style-confrontation": "30a68d6b64dcf2ac9bc926e1f3ee934a0eb72b20c7be65c975b662b9ffaf2b5c",
    "style-daily": "5d473b12e70ec01be6ab8fde9e718a4e9fa2bde49b20cb0ba14ad5af6646bfe0",
    "style-romance": "056ef940884822822d22393daabba89bc354d0eeefd8b8e9f156da39b9872d4c",
    "style-sorrow": "9d2fb2ce78a50750dc17a15376690164eeea9ac4699a92bc7ec0c38905c726ef",
    "style-suspense": "f760e1352da58c55dac4d55cb1e732638ee500f4ca58d202174f624f0197db26",
}


def normalize_scenes(values: Iterable[str] | None) -> list[str]:
    """宽松整理戏份：认中文别名，去重保序，丢掉认不出的（用于 AI 规划的输出）。"""
    out: list[str] = []
    for v in values or []:
        key = str(v).strip()
        key = _SCENE_ALIASES.get(key, key.lower())
        if key in SCENES and key not in out:
            out.append(key)
    return out


class SkillInput(BaseModel):
    name: str
    description: str = ""
    applies_to: str = "both"
    scenes: list[str] = Field(default_factory=list)
    instructions: str
    source: str = ""  # 出处：网址或书名，多个用 ; 分隔


class Skill(SkillInput):
    id: str
    enabled: bool = False
    builtin: bool = False
    update_available: bool = False


def skills_dir() -> Path:
    return db.STORAGE / "skills"


def _bad(message: str, code: str = "BAD_SKILL") -> HTTPException:
    return HTTPException(400, detail={"code": code, "message": message})


def _not_found() -> HTTPException:
    return HTTPException(404, detail={"code": "NOT_FOUND", "message": "Skill 不存在"})


# ---- SKILL.md ----

def parse_skill_md(text: str) -> tuple[dict[str, str], str]:
    """拆出 frontmatter 和正文。

    顶层一行一个 `key: value`，# 之后是注释；也接受网上 SKILL.md 常见的 `key: |` / `key: >` 多行值。
    缩进的嵌套键（如 metadata 下的 version）忽略。
    """
    text = text.replace("\r\n", "\n").lstrip("\ufeff")
    meta: dict[str, str] = {}
    m = re.match(r"---\n(.*?)\n---[ \t]*(?:\n|$)", text, re.DOTALL)
    if not m:
        return meta, text.strip()
    block_key: Optional[str] = None
    block: list[str] = []
    for line in m.group(1).splitlines():
        if block_key is not None:
            if line.startswith((" ", "\t")) or not line.strip():
                block.append(line.strip())
                continue
            meta[block_key] = " ".join(s for s in block if s)
            block_key = None
        if line.startswith((" ", "\t")):
            continue
        key, sep, value = line.partition(":")
        if not sep:
            continue
        value = re.sub(r"\s+#.*$", "", value).strip()
        if value in ("|", ">", "|-", ">-"):
            block_key, block = key.strip(), []
            continue
        meta[key.strip()] = value.strip("\"'")
    if block_key is not None:
        meta[block_key] = " ".join(s for s in block if s)
    return meta, text[m.end():].strip()


def render_skill_md(data: SkillInput) -> str:
    def one_line(s: str) -> str:
        return " ".join(s.split())

    scenes = f"scenes: {', '.join(data.scenes)}\n" if data.scenes else ""
    source = f"source: {one_line(data.source)}\n" if data.source.strip() else ""
    return (
        "---\n"
        f"name: {one_line(data.name)}\n"
        f"description: {one_line(data.description)}\n"
        f"applies_to: {data.applies_to}\n"
        f"{scenes}"
        f"{source}"
        "---\n\n"
        f"{data.instructions.strip()}\n"
    )


def _validate(data: SkillInput) -> SkillInput:
    data.name = data.name.strip()
    data.description = data.description.strip()
    # frontmatter 里 " #" 之后会被当作注释
    data.source = re.sub(r"\s+#", "#", " ".join(data.source.split()))
    data.instructions = data.instructions.replace("\r\n", "\n").strip()
    data.applies_to = (data.applies_to or "both").strip()
    if not data.name:
        raise _bad("Skill 名称不能为空", "REQUIRED")
    if not data.instructions:
        raise _bad("Skill 指令不能为空", "REQUIRED")
    if data.applies_to not in APPLIES:
        raise _bad("适用范围只能是 plan、generate 或 both")
    scenes: list[str] = []
    for s in data.scenes:
        key = s.strip().lower()
        if key not in SCENES:
            raise _bad(f"未知的戏份类型：{s.strip()}", "BAD_SCENE")
        if key not in scenes:
            scenes.append(key)
    data.scenes = scenes
    return data


def _from_md(text: str, fallback_name: str) -> SkillInput:
    meta, body = parse_skill_md(text)
    return _validate(SkillInput(
        name=meta.get("name") or fallback_name,
        description=meta.get("description", ""),
        applies_to=meta.get("applies_to") or "both",
        scenes=[s for s in re.split(r"[,，、\s]+", meta.get("scenes", "")) if s],
        instructions=body,
        source=meta.get("source", ""),
    ))


def _new_id(name: str) -> str:
    base = re.sub(r"[^a-z0-9_-]+", "-", name.lower()).strip("-")[:40] or f"skill-{db.new_id()[:6]}"
    sid, n = base, 2
    while (skills_dir() / sid).exists():
        sid, n = f"{base}-{n}", n + 1
    return sid


def _dir_of(skill_id: str) -> Path:
    if not re.fullmatch(r"[A-Za-z0-9_.-]+", skill_id) or skill_id in (".", ".."):
        raise _not_found()
    d = skills_dir() / skill_id
    if not (d / SKILL_FILE).is_file():
        raise _not_found()
    return d


# ---- enabled set ----

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


# ---- CRUD ----

def _load(skill_id: str, enabled: set[str], updates: dict[str, bool]) -> Optional[Skill]:
    path = skills_dir() / skill_id / SKILL_FILE
    try:
        data = _from_md(path.read_text(encoding="utf-8"), skill_id)
    except (OSError, UnicodeDecodeError, HTTPException):
        return None
    return Skill(
        id=skill_id,
        enabled=skill_id in enabled,
        builtin=skill_id in updates,
        update_available=updates.get(skill_id, False),
        **data.model_dump(),
    )


def list_skills() -> list[Skill]:
    updates = seed_builtins()
    root = skills_dir()
    if not root.is_dir():
        return []
    enabled = set(_enabled())
    skills = [s for d in sorted(root.iterdir()) if d.is_dir() and (s := _load(d.name, enabled, updates))]
    return sorted(skills, key=lambda s: s.name.lower())


def get_skill(skill_id: str) -> Skill:
    _dir_of(skill_id)
    skill = _load(skill_id, set(_enabled()), _builtin_updates())
    if skill is None:
        raise _not_found()
    return skill


def create_skill(data: SkillInput) -> Skill:
    data = _validate(data)
    sid = _new_id(data.name)
    d = skills_dir() / sid
    d.mkdir(parents=True)
    (d / SKILL_FILE).write_text(render_skill_md(data), encoding="utf-8")
    return get_skill(sid)


def update_skill(skill_id: str, data: SkillInput) -> Skill:
    d = _dir_of(skill_id)
    (d / SKILL_FILE).write_text(render_skill_md(_validate(data)), encoding="utf-8")
    return get_skill(skill_id)


def delete_skill(skill_id: str) -> None:
    shutil.rmtree(_dir_of(skill_id))
    _save_enabled([i for i in _enabled() if i != skill_id])


def set_enabled(skill_id: str, enabled: bool) -> Skill:
    _dir_of(skill_id)
    ids = [i for i in _enabled() if i != skill_id]
    if enabled:
        ids.append(skill_id)
    _save_enabled(ids)
    return get_skill(skill_id)


def import_skill(path_s: str) -> Skill:
    """导入一个 .md 文件，或一个含 SKILL.md 的文件夹（连同文件夹里的其他文件一起复制）。"""
    src = Path((path_s or "").strip())
    if not path_s.strip() or not src.exists():
        raise _bad(f"找不到要导入的文件或文件夹：{path_s}", "NOT_FOUND")
    if src.is_dir():
        md = src / SKILL_FILE
        if not md.is_file():
            raise _bad("文件夹里没有 SKILL.md", "NO_SKILL_FILE")
        data = _from_md(md.read_text(encoding="utf-8"), src.name)
        sid = _new_id(data.name)
        shutil.copytree(src, skills_dir() / sid)
        (skills_dir() / sid / SKILL_FILE).write_text(render_skill_md(data), encoding="utf-8")
        return get_skill(sid)
    if src.suffix.lower() != ".md":
        raise _bad("只能导入 .md 文件或含 SKILL.md 的文件夹")
    return create_skill(_from_md(src.read_text(encoding="utf-8"), src.stem))


# ---- prompt ----

def skills_prompt(purpose: str, scenes: Iterable[str] | None = None) -> str:
    """已启用、适用于 purpose（plan / generate）且与本章戏份匹配的 Skill 拼成的提示词一节；没有时返回空串。"""
    chapter = set(normalize_scenes(scenes))
    skills = [
        s for s in list_skills()
        if s.enabled and s.applies_to in (purpose, "both") and (not s.scenes or chapter & set(s.scenes))
    ]
    if not skills:
        return ""
    log.info("skills applied to %s: %s", purpose, ", ".join(s.name for s in skills))
    parts = [f"## {s.name}\n{s.instructions}" for s in skills]
    return "写作技能（以下是作者启用的写作指令，请在本次任务中遵循）：\n\n" + "\n\n".join(parts)


# ---- built-in skills ----

def builtin_ids() -> list[str]:
    if not BUILTIN_DIR.is_dir():
        return []
    return sorted(d.name for d in BUILTIN_DIR.iterdir() if (d / SKILL_FILE).is_file())


def content_hash(text: str) -> str:
    norm = text.replace("\r\n", "\n").lstrip("\ufeff").strip()
    return hashlib.sha256(norm.encode("utf-8")).hexdigest()


def _file_hash(path: Path) -> Optional[str]:
    try:
        return content_hash(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError):
        return None


def _json_setting(key: str) -> tuple[str, object]:
    with db.connect() as conn:
        raw = db.get_setting(conn, key, "")
    try:
        return raw, json.loads(raw) if raw else None
    except ValueError:
        return raw, None


def _recorded_hashes() -> dict[str, str]:
    _, data = _json_setting(_HASHES_KEY)
    return {k: v for k, v in data.items() if isinstance(v, str)} if isinstance(data, dict) else {}


def _save_hashes(hashes: dict[str, str]) -> None:
    with db.connect() as conn:
        db.set_setting(conn, _HASHES_KEY, json.dumps(hashes, sort_keys=True))


def _install_builtin(sid: str) -> None:
    shutil.copytree(BUILTIN_DIR / sid, skills_dir() / sid, dirs_exist_ok=True)


def _builtin_updates(hashes: Optional[dict[str, str]] = None) -> dict[str, bool]:
    """{内置 id: 是否有用户还没采用的新版本}，只含用户目录里还在的内置 Skill。"""
    hashes = _recorded_hashes() if hashes is None else hashes
    out: dict[str, bool] = {}
    for sid in builtin_ids():
        user_hash = _file_hash(skills_dir() / sid / SKILL_FILE)
        if user_hash is None:
            continue
        latest = _file_hash(BUILTIN_DIR / sid / SKILL_FILE)
        seeded = hashes.get(sid) or _V1_HASHES.get(sid)
        out[sid] = user_hash != latest and seeded != latest
    return out


def seed_builtins() -> dict[str, bool]:
    """放入还没放过的内置 Skill（默认不启用，用户删掉后不再出现），并同步内置版本的更新。

    返回 _builtin_updates() 的结果。
    """
    raw, data = _json_setting(_OFFERED_KEY)
    offered = {i for i in data if isinstance(i, str)} if isinstance(data, list) else set()
    if not raw:
        with db.connect() as conn:
            if db.get_setting(conn, _SEEDED_KEY, "") == "1":
                offered |= set(_LEGACY_EXAMPLES)
    hashes = _recorded_hashes()
    offered_changed = not raw
    hashes_changed = False

    for sid in builtin_ids():
        latest = _file_hash(BUILTIN_DIR / sid / SKILL_FILE)
        dest = skills_dir() / sid
        if sid not in offered:
            if not dest.exists():
                shutil.copytree(BUILTIN_DIR / sid, dest)
                if latest:
                    hashes[sid] = latest
                    hashes_changed = True
            offered.add(sid)
            offered_changed = True
            continue
        user_hash = _file_hash(dest / SKILL_FILE)
        if user_hash is None or latest is None:
            continue
        if user_hash == latest:
            if hashes.get(sid) != latest:
                hashes[sid] = latest
                hashes_changed = True
        elif user_hash == (hashes.get(sid) or _V1_HASHES.get(sid)):
            _install_builtin(sid)
            hashes[sid] = latest
            hashes_changed = True
            log.info("builtin skill updated: %s", sid)

    if offered_changed:
        with db.connect() as conn:
            db.set_setting(conn, _OFFERED_KEY, json.dumps(sorted(offered), ensure_ascii=False))
    if hashes_changed:
        _save_hashes(hashes)
    return _builtin_updates(hashes)


def reset_skill(skill_id: str) -> Skill:
    """把内置 Skill 恢复为当前内置版本（覆盖用户的修改，启用状态不变）。"""
    if skill_id not in builtin_ids():
        raise _bad("只有内置 Skill 可以恢复为内置版本", "NOT_BUILTIN")
    _install_builtin(skill_id)
    latest = _file_hash(BUILTIN_DIR / skill_id / SKILL_FILE)
    if latest:
        hashes = _recorded_hashes()
        hashes[skill_id] = latest
        _save_hashes(hashes)
    return get_skill(skill_id)
