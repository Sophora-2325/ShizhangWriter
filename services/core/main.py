from __future__ import annotations

import logging
from datetime import datetime
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, Optional

from fastapi import FastAPI, File, Form, HTTPException, Query, Request, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from starlette.exceptions import HTTPException as StarletteHTTPException

from . import notes as notes_service
from . import plugins, secret_store, skill_sources, skills
from . import search as search_service
from . import versions as versions_service
from . import stt as stt_service
from . import workspace as ws
from .audit import run_audit, run_plan_check
from .db import (
    configure_paths,
    connect,
    get_setting,
    get_storage,
    init_db,
    path_info,
    set_setting,
    utcnow,
)
from .export import (
    export_bible_appendix,
    export_docx,
    export_epub,
    export_markdown,
    export_txt,
)
from .extraction import run_extract
from .freshness import body_hash, stamp, summary_valid
from .generation import run_generate, run_plan
from .models import StackedConstraints
from .stacked_constraints import associate
from .i18n import language_from_header, translate_detail
from .models import (
    BibleEdit,
    BodyUpdate,
    Chapter,
    ChapterCreate,
    ChapterLore,
    ChapterPlan,
    ChapterStatus,
    Character,
    CharacterUpsert,
    GateConfirmRequest,
    GenerateRequest,
    Phase,
    PlanRequest,
    Project,
    ProjectCreate,
    ProjectInfoUpdate,
    ReadingGate,
    Refinement,
    ReviewDraft,
    SettingsUpdate,
    StyleProfile,
)
from .providers import (
    INHERIT,
    MOCK,
    api_key_hint,
    build,
    build_draft,
    create_instance,
    delete_instance,
    effective_provider,
    first_local,
    get_api_key,
    get_global_provider,
    get_instance,
    get_local_config,
    instance_spec,
    is_known,
    list_presets,
    list_provider_views,
    model_for,
    pick_model,
    provider_view,
    save_provider_config,
    set_api_key,
    set_key,
)
from .publish import platform_table
from .publish import prepare as prepare_publish
from .repository import (
    _compile_style,
    append_chapters,
    append_event,
    chapter_file,
    chapter_lock,
    clear_chapter,
    context_revision,
    create_project,
    delete_chapter,
    delete_character,
    delete_project,
    get_chapter,
    get_project,
    import_folder,
    inherit_lore,
    insert_chapter,
    list_chapters,
    list_events,
    list_projects,
    lore_view,
    migrate_project,
    project_lore,
    recover_pending,
    rename_chapter,
    rename_project,
    save_analysis,
    save_lore,
    save_project,
    set_phase,
    supersede_after,
    upsert_chapter,
    upsert_character,
    usage_summary,
)
from .repository import (
    connect as db_connect,
)
from .statemachine import assert_phase
from .style import style_check, update_baseline_from_chapter


@asynccontextmanager
async def lifespan(_app: FastAPI):
    configure_paths()  # honor NOVEL_GM_ROOT / NOVEL_GM_DATA
    init_db()
    for project in list_projects():
        recover_pending(project.id)
        for chapter in list_chapters(project.id):
            if chapter.summary_status == "extracting":
                chapter.summary_status = "failed"
                chapter.summary_error = "摘要提取被中断，请重试"
                upsert_chapter(project.id, chapter)
    secret_store.migrate_legacy_secrets()
    from .proposals import recover_requests
    from .body_revisions import recover_requests as recover_body_revision_requests
    recover_body_revision_requests()
    recover_requests()
    yield


app = FastAPI(title="novel-gm", version="0.3.21", lifespan=lifespan)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.exception_handler(StarletteHTTPException)
async def _translated_http_error(request: Request, exc: StarletteHTTPException) -> JSONResponse:
    lang = language_from_header(request.headers.get("accept-language"))
    return JSONResponse(
        status_code=exc.status_code,
        content={"detail": translate_detail(exc.detail, lang)},
        headers=getattr(exc, "headers", None),
    )


@app.get("/health")
def health() -> dict[str, Any]:
    return {"status": "ok", "paths": path_info()}


# ---- speech-to-text (SenseVoice) ----


@app.get("/stt/status")
def stt_status() -> dict[str, Any]:
    return stt_service.status()


@app.post("/stt/transcribe")
async def stt_transcribe(
    file: UploadFile = File(...),
    lang: str = Form(stt_service.DEFAULT_LANG),
) -> dict[str, Any]:
    data = await file.read()
    if not data:
        raise HTTPException(400, detail={"code": "STT_EMPTY_AUDIO", "message": "音频为空"})
    info = stt_service.status()
    if not info["available"]:
        raise HTTPException(
            503,
            detail={"code": "STT_MODEL_MISSING", "message": info["message"]},
        )
    try:
        text, used = stt_service.transcribe_bytes(data, lang)
    except FileNotFoundError:
        raise HTTPException(
            503,
            detail={"code": "STT_MODEL_MISSING", "message": info["message"]},
        ) from None
    except ValueError as exc:
        raise HTTPException(
            400,
            detail={"code": "STT_BAD_AUDIO", "message": str(exc)},
        ) from None
    except Exception as exc:  # noqa: BLE001 — 暴露给客户端的识别失败
        raise HTTPException(
            500,
            detail={"code": "STT_FAILED", "message": f"语音识别失败：{exc}"},
        ) from None
    return {"text": text, "lang": used}


# ---- settings / cost ----

def _bad_provider() -> HTTPException:
    return HTTPException(400, detail={"code": "BAD_PROVIDER", "message": "未知的生成方式"})


def _keyring_unavailable() -> HTTPException:
    return HTTPException(
        503,
        detail={"code": "KEYRING_UNAVAILABLE", "message": "无法访问系统凭据管理器，密钥未保存"},
    )


@app.get("/settings")
def read_settings() -> dict[str, Any]:
    key = get_api_key()
    base, model = get_local_config()
    provider, offline = get_global_provider()
    with connect() as conn:
        gate_mode = get_setting(conn, "gate_mode", "behavior")
        last_parent_dir = get_setting(conn, "last_parent_dir", "")
    return {
        "has_deepseek_key": bool(key),
        "key_hint": api_key_hint(key),
        "provider": provider,
        "offline_mode": offline,
        "local_base_url": base,
        "local_model": model,
        "gate_mode": gate_mode,
        "last_parent_dir": last_parent_dir,
    }


@app.post("/settings")
def update_settings(body: SettingsUpdate) -> dict[str, Any]:
    if body.provider is not None and not is_known(body.provider):
        raise _bad_provider()
    with connect() as conn:
        if body.provider is not None:
            set_setting(conn, "provider", body.provider)
        if body.offline_mode is not None:
            set_setting(conn, "offline_mode", "1" if body.offline_mode else "0")
        if body.local_base_url is not None:
            set_setting(conn, "local_base_url", body.local_base_url)
        if body.local_model is not None:
            set_setting(conn, "local_model", body.local_model)
        if body.gate_mode is not None:
            set_setting(conn, "gate_mode", body.gate_mode)
        if body.last_parent_dir is not None:
            set_setting(conn, "last_parent_dir", body.last_parent_dir)
    if body.deepseek_api_key is not None:
        # empty string clears; non-empty saves
        try:
            set_api_key(body.deepseek_api_key)
        except secret_store.SecretStoreUnavailable:
            raise _keyring_unavailable()
        if body.deepseek_api_key.strip() and get_instance("deepseek") is None:
            create_instance("deepseek", {})
    return read_settings()


@app.get("/cost")
def cost(project_id: Optional[str] = None) -> dict[str, Any]:
    return usage_summary(project_id)


@app.get("/usage/session")
def usage_session(since: datetime) -> dict[str, Any]:
    from .usage import session_usage
    return session_usage(since)


# ---- model providers ----


class ProviderUpdate(BaseModel):
    name: Optional[str] = None
    api_key: Optional[str] = None  # None 不改；"" 清除
    base_url: Optional[str] = None  # "" 恢复默认
    model_strong: Optional[str] = None
    model_fast: Optional[str] = None
    thinking: Optional[bool] = None
    price: Optional[dict[str, float]] = None
    clear_price: bool = False


class ProviderCreate(ProviderUpdate):
    preset: str


class ProviderDraft(BaseModel):
    """Unsaved editor values; id means editing an existing config (blank key = saved key)."""
    id: str = ""
    preset: str = ""
    name: str = ""
    api_key: str = ""
    base_url: str = ""
    model_strong: str = ""
    model_fast: str = ""


def _store_key(provider_id: str, key: Optional[str]) -> None:
    spec = instance_spec(provider_id)
    if key is None or spec is None or spec.kind in ("local", "mock"):
        return
    try:
        set_key(provider_id, key)
    except secret_store.SecretStoreUnavailable:
        raise _keyring_unavailable()


def _changes(body: ProviderUpdate) -> dict[str, Any]:
    changes = body.model_dump(include={"name", "base_url", "model_strong", "model_fast", "thinking", "price"},
                              exclude_none=True)
    if body.clear_price:
        changes["price"] = None
    return changes


@app.get("/providers")
def api_list_providers() -> list[dict[str, Any]]:
    return list_provider_views()


@app.get("/providers/presets")
def api_list_presets() -> list[dict[str, Any]]:
    return list_presets()


@app.post("/providers")
def api_create_provider(body: ProviderCreate) -> dict[str, Any]:
    provider_id = create_instance(body.preset, _changes(body))
    try:
        _store_key(provider_id, body.api_key)
    except HTTPException:
        delete_instance(provider_id)
        raise
    return provider_view(provider_id)


@app.put("/providers/{provider_id}")
def api_update_provider(provider_id: str, body: ProviderUpdate) -> dict[str, Any]:
    save_provider_config(provider_id, _changes(body))
    _store_key(provider_id, body.api_key)
    return provider_view(provider_id)


@app.delete("/providers/{provider_id}")
def api_delete_provider(provider_id: str) -> dict[str, bool]:
    delete_instance(provider_id)
    return {"ok": True}


@app.delete("/providers/{provider_id}/key")
def api_delete_provider_key(provider_id: str) -> dict[str, Any]:
    if get_instance(provider_id) is None:
        raise _bad_provider()
    try:
        set_key(provider_id, "")
    except secret_store.SecretStoreUnavailable:
        raise _keyring_unavailable()
    return provider_view(provider_id)


def _ping(provider: Any, model: str) -> dict[str, Any]:
    resp = provider.chat({
        "model": model,
        "messages": [{"role": "user", "content": "请只回复两个字：收到"}],
        "temperature": 0.0,
        "max_tokens": 32,
        "thinking": False,
        "purpose": "test",
    })
    return {
        "ok": True,
        "model": model,
        "reply": (resp.get("content") or "").strip()[:100],
        "latency_ms": round(resp.get("latency_ms", 0)),
    }


@app.post("/providers/test")
def api_test_draft(body: ProviderDraft) -> dict[str, Any]:
    provider, cfg, display = build_draft(body.model_dump())
    return _ping(provider, pick_model(cfg, "generate", display))


@app.post("/providers/models")
def api_draft_models(body: ProviderDraft) -> dict[str, Any]:
    return {"models": build_draft(body.model_dump())[0].list_models()}


@app.get("/providers/{provider_id}/models")
def api_provider_models(provider_id: str) -> dict[str, Any]:
    return {"models": build(provider_id).list_models()}


@app.post("/providers/{provider_id}/test")
def api_test_provider(provider_id: str) -> dict[str, Any]:
    provider = build(provider_id)
    return _ping(provider, model_for("generate", provider_id))


# ---- skills / plugins ----


class EnabledPatch(BaseModel):
    enabled: bool


class ImportRequest(BaseModel):
    path: str = ""


@app.get("/skills")
def api_list_skills() -> list[skills.Skill]:
    return skills.list_skills()


@app.post("/skills")
def api_create_skill(body: skills.SkillInput) -> skills.Skill:
    return skills.create_skill(body)


@app.post("/skills/import")
def api_import_skill(body: ImportRequest) -> skills.Skill:
    return skills.import_skill(body.path)


class UrlRequest(BaseModel):
    url: str = ""


class DistillRequest(BaseModel):
    url: str = ""
    text: str = ""
    hint: str = ""


class TokenRequest(BaseModel):
    token: str = ""


@app.get("/skills/sources")
def api_skill_sources() -> dict[str, Any]:
    return {"has_github_token": skill_sources.has_github_token(), "items": skill_sources.RECOMMENDED}


@app.put("/skills/github-token")
def api_set_github_token(body: TokenRequest) -> dict[str, bool]:
    try:
        skill_sources.set_github_token(body.token.strip())
    except secret_store.SecretStoreUnavailable:
        raise _keyring_unavailable()
    return {"has_github_token": skill_sources.has_github_token()}


@app.get("/skills/github/search")
def api_github_search(q: str = "", page: int = 1) -> dict[str, Any]:
    return skill_sources.search_github(q, page)


@app.get("/skills/github/repo")
def api_github_repo(repo: str) -> dict[str, Any]:
    return skill_sources.list_repo_skills(repo)


@app.post("/skills/preview-url")
def api_preview_skill_url(body: UrlRequest) -> dict[str, Any]:
    return skill_sources.preview(body.url)


@app.post("/skills/import-url")
def api_import_skill_url(body: UrlRequest) -> skills.Skill:
    return skill_sources.import_url(body.url)


@app.post("/skills/distill")
def api_distill_skill(body: DistillRequest) -> dict[str, Any]:
    return skill_sources.distill(body.url, body.text, body.hint)


@app.put("/skills/{skill_id}")
def api_update_skill(skill_id: str, body: skills.SkillInput) -> skills.Skill:
    return skills.update_skill(skill_id, body)


@app.patch("/skills/{skill_id}")
def api_toggle_skill(skill_id: str, body: EnabledPatch) -> skills.Skill:
    return skills.set_enabled(skill_id, body.enabled)


@app.post("/skills/{skill_id}/reset")
def api_reset_skill(skill_id: str) -> skills.Skill:
    return skills.reset_skill(skill_id)


@app.delete("/skills/{skill_id}")
def api_delete_skill(skill_id: str) -> dict[str, bool]:
    skills.delete_skill(skill_id)
    return {"ok": True}


@app.get("/plugins")
def api_list_plugins() -> list[plugins.Plugin]:
    return plugins.list_plugins()


@app.post("/plugins/import")
def api_import_plugin(body: ImportRequest) -> plugins.Plugin:
    return plugins.import_plugin(body.path)


@app.patch("/plugins/{plugin_id}")
def api_toggle_plugin(plugin_id: str, body: EnabledPatch) -> plugins.Plugin:
    return plugins.set_enabled(plugin_id, body.enabled)


@app.delete("/plugins/{plugin_id}")
def api_delete_plugin(plugin_id: str) -> dict[str, bool]:
    plugins.delete_plugin(plugin_id)
    return {"ok": True}


# ---- projects ----


@app.get("/projects")
def api_list_projects() -> list[Project]:
    return list_projects()


@app.post("/project")
def api_create_project(body: ProjectCreate) -> Project:
    # default banned words from AI-cavity doc if empty
    if not body.style.banned_words:
        from .context import DEFAULT_BANNED

        body.style.banned_words = list(DEFAULT_BANNED)
    if not body.parent_dir.strip():
        raise HTTPException(400, detail={"code": "PARENT_REQUIRED", "message": "请选择作品保存位置"})
    proj = create_project(body)
    with connect() as conn:
        set_setting(conn, "last_parent_dir", body.parent_dir.strip())
    return proj


class FolderRequest(BaseModel):
    path: str = ""


class ParentDirRequest(BaseModel):
    parent_dir: str = ""


class TitleRequest(BaseModel):
    title: str = ""


class PortraitRequest(BaseModel):
    character_id: str
    source_path: str


@app.post("/projects/open")
def api_open_project(body: FolderRequest) -> Project:
    return import_folder(body.path)


@app.get("/project/{project_id}")
def api_get_project(project_id: str) -> Project:
    return get_project(project_id)


@app.patch("/project/{project_id}")
def api_rename_project(project_id: str, body: TitleRequest) -> Project:
    return rename_project(project_id, body.title)


@app.patch("/project/{project_id}/info")
def api_project_info(project_id: str, body: ProjectInfoUpdate) -> Project:
    # Patch only supplied fields; another editor's style or lore is never resubmitted.
    with chapter_lock(project_id):
        proj = get_project(project_id)
        if body.cover is not None:
            cover = body.cover.model_copy()
            if bool(cover.preview) != bool(cover.original):
                raise ws.bad_request("封面需要同时提供预览与原图引用", "BAD_COVER")
            if cover.preview:
                root = ws.require_root(project_id).resolve()
                for attr in ("preview", "original"):
                    relative = getattr(cover, attr).replace("\\", "/")
                    path = Path(relative)
                    target = (root / path).resolve()
                    if (path.is_absolute() or ":" in relative or ".." in path.parts
                            or not target.is_relative_to(root)
                            or target.suffix.lower() not in ws.IMAGE_EXTS or not target.is_file()):
                        raise ws.bad_request("封面图片必须位于作品文件夹内", "BAD_COVER")
                    setattr(cover, attr, relative)
            proj.cover = cover
        if body.genre is not None:
            proj.genre = list(dict.fromkeys(v.strip() for v in body.genre if v.strip()))
        if body.tone is not None:
            proj.tone = body.tone.strip()
        if body.audience is not None:
            proj.audience = body.audience.strip()
        return save_project(proj)


@app.delete("/project/{project_id}")
def api_delete_project(project_id: str) -> dict[str, bool]:
    delete_project(project_id)
    return {"deleted": True}


@app.post("/project/{project_id}/migrate")
def api_migrate_project(project_id: str, body: ParentDirRequest) -> Project:
    proj = migrate_project(project_id, body.parent_dir)
    with connect() as conn:
        set_setting(conn, "last_parent_dir", body.parent_dir.strip())
    return proj


@app.put("/project/{project_id}/lore")
def api_save_lore(project_id: str, body: ChapterLore, chapter: Optional[int] = None) -> Project:
    return save_lore(project_id, body, chapter)


@app.post("/project/{project_id}/portrait")
def api_portrait(project_id: str, body: PortraitRequest) -> dict[str, str]:
    root = ws.require_root(project_id)
    return {"portrait": ws.copy_portrait(root, body.character_id, body.source_path)}


@app.patch("/project/{project_id}/settings")
def api_project_settings(project_id: str, body: SettingsUpdate) -> Project:
    proj = get_project(project_id)
    if body.provider is not None:
        if body.provider != INHERIT and not is_known(body.provider):
            raise _bad_provider()
        _, offline = effective_provider(proj.provider, proj.offline_mode)
        spec = instance_spec(body.provider)
        if offline and body.provider != MOCK and spec is not None and spec.cloud:
            raise HTTPException(400, detail={"code": "OFFLINE", "message": "离线模式禁用云端"})
        proj.provider = body.provider
    if body.offline_mode is not None:
        proj.offline_mode = body.offline_mode
        if body.offline_mode:
            proj.provider = first_local() or proj.provider
    return save_project(proj)


class BiblePatch(BaseModel):
    edits: list[BibleEdit] = Field(default_factory=list)
    preview_only: bool = False
    reference_samples: list[str] | None = None


def _clean_samples(items: list[str]) -> list[str]:
    """丢掉纯空白片段，保留片段内部的换行、空行和标点。"""
    cleaned: list[str] = []
    for item in items:
        text = (item or "").replace("\r\n", "\n").replace("\r", "\n")
        if text.strip():
            cleaned.append(text)
    return cleaned


def _apply_reference_samples(proj: Project, samples: list[str]) -> None:
    data = proj.style.model_dump()
    data["reference_samples"] = samples
    style = StyleProfile(**data)
    style.compiled_system_constraints = _compile_style(style)
    proj.style = style


def _apply_bible_request(proj: Project, body: BiblePatch) -> dict[str, Any]:
    """把一次 bible 请求应用到内存中的作品。冲突时不改任何字段。不负责落盘。"""
    if body.reference_samples is not None and any(e.path == "style.reference_samples" for e in body.edits):
        raise HTTPException(
            400,
            detail={"code": "SAMPLE_CONFLICT", "message": "不能同时用新旧两种方式修改范文"},
        )
    preview = []
    for e in body.edits:
        preview.append({"path": e.path, "old": e.old, "new": e.new, "reason": e.reason})
        if not body.preview_only:
            _apply_bible_path(proj, e.path, e.new)
    samples_preview = None
    cleaned = None
    if body.reference_samples is not None:
        cleaned = _clean_samples(body.reference_samples)
        samples_preview = {"old": list(proj.style.reference_samples), "new": cleaned}
        if not body.preview_only:
            _apply_reference_samples(proj, cleaned)
    conflicts = []
    for e in body.edits:
        if e.new == "" and e.path.endswith("name"):
            conflicts.append({"path": e.path, "message": "角色名不能为空"})
    event = {"edits": [e.model_dump() for e in body.edits]}
    if cleaned is not None:
        event["reference_samples"] = cleaned
    return {
        "preview": preview,
        "conflicts": conflicts,
        "applied": False,
        "reference_samples": samples_preview,
        "event": event,
    }


def _split_list(raw: str) -> list[str]:
    """一行一条；也接受逗号分隔（沿用 forbidden 的历史写法）。"""
    out: list[str] = []
    for line in (raw or "").replace("\r\n", "\n").split("\n"):
        for piece in line.split(","):
            item = piece.strip()
            if item:
                out.append(item)
    return out


def _split_lines(raw: str) -> list[str]:
    """只按换行切。范文里可能带逗号，绝不能被当成分隔符。"""
    return [ln.strip() for ln in (raw or "").replace("\r\n", "\n").split("\n") if ln.strip()]


def _split_arrows(raw: str) -> dict[str, str]:
    """逐行 A→B / A->B / A=>B / A:B，解析成替换对照表。"""
    pairs: dict[str, str] = {}
    for line in _split_lines(raw):
        for sep in ("→", "->", "=>", ":"):
            if sep in line:
                k, _, v = line.partition(sep)
                k, v = k.strip(), v.strip()
                if k:
                    pairs[k] = v
                break
    return pairs


def _split_pairs(raw: str) -> dict[str, Any]:
    """逐行 key=value，用于标点策略与人物状态这类字典字段。"""
    out: dict[str, Any] = {}
    for line in _split_lines(raw):
        k, sep, v = line.partition("=")
        if sep and k.strip():
            out[k.strip()] = v.strip()
    return out


def _apply_style_path(proj: Project, attr: str, new: str) -> None:
    data = proj.style.model_dump()
    if attr in ("banned_words", "diction"):
        data[attr] = _split_list(new)
    elif attr == "reference_samples":
        data[attr] = _split_lines(new)
    elif attr == "preferred_replacements":
        data[attr] = _split_arrows(new)
    elif attr == "punctuation_policy":
        data[attr] = _split_pairs(new)
    elif attr in ("pov", "tense", "sentence_length", "compiled_system_constraints"):
        data[attr] = new.strip()
    elif attr == "metrics":
        raise HTTPException(
            400, detail={"code": "READONLY_PATH", "message": "metrics 由定稿自动写入，不接受外部修改"}
        )
    else:
        raise HTTPException(400, detail={"code": "BAD_PATH", "message": f"unknown style field {attr}"})

    style = StyleProfile(**data)
    # compiled_system_constraints 只在建作品时生成过一次（repository.create_project）。
    # 用户改了风格字段就必须重编译，否则 L0 会继续用旧文本（context 优先取 compiled），改动等于没生效。
    if attr != "compiled_system_constraints":
        style.compiled_system_constraints = _compile_style(style)
    proj.style = style


def _apply_bible_path(proj: Project, path: str, new: str) -> None:
    # path examples: characters.0.secret, entries.0.content, threads.0.user_directive,
    #                forbidden, style.pov, style.banned_words, genre, tone, audience, npc_auto_act
    parts = path.split(".")

    def _at(items: list, idx: int) -> int:
        if idx < 0 or idx >= len(items):
            raise HTTPException(
                400, detail={"code": "BAD_PATH", "message": f"index out of range: {path}"}
            )
        return idx

    if parts[0] == "entries" and len(parts) >= 3:
        idx = _at(proj.entries, int(parts[1]))
        attr = parts[2]
        entries = list(proj.entries)
        data = entries[idx].model_dump()
        if attr == "hard":
            data["hard"] = new.strip().lower() in ("1", "true", "yes")
        else:
            data[attr] = new
        from .models import LoreEntry

        entries[idx] = LoreEntry(**data)
        proj.entries = entries
        return
    if parts[0] == "characters" and len(parts) >= 3:
        idx = _at(proj.characters, int(parts[1]))
        attr = parts[2]
        chars = list(proj.characters)
        data = chars[idx].model_dump()
        if attr in ("known_facts", "goals"):
            data[attr] = _split_list(new)
        elif attr == "status":
            data[attr] = _split_pairs(new)
        else:
            data[attr] = new
        from .models import Character

        chars[idx] = Character(**data)
        proj.characters = chars
        return
    if parts[0] == "threads" and len(parts) >= 3:
        idx = _at(proj.threads, int(parts[1]))
        attr = parts[2]
        threads = list(proj.threads)
        data = threads[idx].model_dump()
        if attr == "next_milestone":
            text = (new or "").strip()
            nodes = data["milestones"]
            pending = next((i for i, node in enumerate(nodes) if not node["completed"]), None)
            if text:
                if pending is None:
                    nodes.append({"name": text})
                else:
                    nodes[pending]["name"] = text
            elif pending is not None:
                # Clearing the legacy field removes its current node; subsequent
                # planned nodes and completed history remain intact.
                nodes.pop(pending)
        elif attr == "priority":
            try:
                data[attr] = int((new or "").strip() or "1")
            except ValueError:
                raise HTTPException(
                    400, detail={"code": "BAD_VALUE", "message": f"priority 需要整数，收到 {new!r}"}
                )
        else:
            data[attr] = new
        from .models import PlotThread

        threads[idx] = PlotThread(**data)
        proj.threads = threads
        return
    if parts[0] == "style" and len(parts) == 2:
        _apply_style_path(proj, parts[1], new)
        return
    if parts[0] == "forbidden":
        proj.forbidden = _split_list(new)
        return
    if parts[0] == "genre":
        proj.genre = _split_list(new)
        return
    if parts[0] == "tone":
        proj.tone = new.strip()
        return
    if parts[0] == "audience":
        proj.audience = new.strip()
        return
    if parts[0] == "npc_auto_act":
        proj.npc_auto_act = new.strip().lower() in ("1", "true", "yes", "on")
        return
    raise HTTPException(400, detail={"code": "BAD_PATH", "message": f"unknown path {path}"})


@app.post("/project/{project_id}/bible")
def api_bible_edit(project_id: str, body: BiblePatch) -> dict[str, Any]:
    proj = get_project(project_id)
    outcome = _apply_bible_request(proj, body)
    event = outcome.pop("event")
    if body.preview_only or outcome["conflicts"]:
        return outcome
    save_project(proj)
    with db_connect() as conn:
        append_event(conn, project_id, "BibleEdited", event)
    outcome["applied"] = True
    outcome["project"] = proj
    return outcome


@app.get("/project/{project_id}/characters")
def api_list_characters(project_id: str) -> list[Character]:
    return get_project(project_id).characters


@app.get("/project/{project_id}/characters/{char_id}")
def api_get_character(project_id: str, char_id: str) -> Character:
    proj = get_project(project_id)
    for c in proj.characters:
        if c.id == char_id:
            return c
    raise HTTPException(404, detail={"code": "NOT_FOUND", "message": "人物不存在"})


@app.post("/project/{project_id}/characters")
def api_upsert_character(project_id: str, body: CharacterUpsert) -> Character:
    return upsert_character(project_id, body)


@app.delete("/project/{project_id}/characters/{char_id}")
def api_delete_character(
    project_id: str, char_id: str, chapter_no: Optional[int] = None
) -> Project:
    # chapter_no 仅为兼容新客户端而接受；删除人物始终是作品级操作。
    return delete_character(project_id, char_id)


@app.get("/project/{project_id}/threads")
def api_threads(project_id: str) -> list[dict[str, Any]]:
    proj = get_project(project_id)
    return [t.model_dump() for t in proj.threads]


@app.get("/project/{project_id}/events")
def api_events(project_id: str) -> list[dict[str, Any]]:
    return list_events(project_id)


@app.get("/project/{project_id}/chapters")
def api_chapters(project_id: str) -> list[Chapter]:
    return list_chapters(project_id)


@app.get("/project/{project_id}/chapter/{n}")
def api_chapter(project_id: str, n: int) -> Chapter:
    return get_chapter(project_id, n)


@app.patch("/project/{project_id}/chapter/{n}")
def api_rename_chapter(project_id: str, n: int, body: TitleRequest) -> Chapter:
    return rename_chapter(project_id, n, body.title)


@app.post("/project/{project_id}/chapter/{n}/clear")
def api_clear_chapter(project_id: str, n: int) -> Chapter:
    return clear_chapter(project_id, n)


@app.delete("/project/{project_id}/chapter/{n}")
def api_delete_chapter(project_id: str, n: int) -> Project:
    return delete_chapter(project_id, n)


@app.post("/project/{project_id}/chapter/{n}/insert-after")
def api_insert_chapter(project_id: str, n: int) -> Chapter:
    return insert_chapter(project_id, n)


@app.get("/project/{project_id}/chapter/{n}/file")
def api_chapter_file(project_id: str, n: int) -> dict[str, str]:
    return {"path": chapter_file(project_id, n)}


class ImportChapter(BaseModel):
    title: str = ""
    body_text: str = ""


class ChapterImportRequest(BaseModel):
    chapters: list[ImportChapter] = Field(default_factory=list)


@app.post("/project/{project_id}/import")
def api_import_chapters(project_id: str, body: ChapterImportRequest) -> list[Chapter]:
    return append_chapters(project_id, [(c.title, c.body_text) for c in body.chapters])


@app.post("/project/{project_id}/backup")
def api_backup(project_id: str) -> dict[str, str]:
    proj = get_project(project_id)
    return {"path": str(ws.backup_zip(ws.require_root(project_id), proj.title))}


# ---- notes：章节笔记 + 本章写作参考 ----

class NoteCreateBody(BaseModel):
    chapter_no: int = 0
    title: str = ""
    body: str = ""
    folder: str = "default"


class NoteUpdateBody(BaseModel):
    title: str = ""
    body: str = ""
    content_version: int
    folder: str | None = None


class NoteDistillBody(BaseModel):
    content_version: int
    chapter_no: int = 0


class NotePinBody(BaseModel):
    pinned: bool


class NoteMoveBody(BaseModel):
    direction: str
    operation_id: str | None = Field(default=None, min_length=1, max_length=128)


class NoteCopyBody(BaseModel):
    title: str | None = None
    operation_id: str | None = Field(default=None, min_length=1, max_length=128)


class NoteReferenceBody(BaseModel):
    reference: str = ""


class NoteFolderBody(BaseModel):
    title: str


@app.get("/project/{project_id}/notes/folders")
def api_note_folders(project_id: str) -> dict[str, Any]:
    return {"folders": notes_service.list_folders(project_id)}


@app.post("/project/{project_id}/notes/folders")
def api_create_note_folder(project_id: str, body: NoteFolderBody) -> dict[str, Any]:
    return notes_service.save_folder(project_id, body.title)


@app.put("/project/{project_id}/notes/folders/{folder_id}")
def api_rename_note_folder(project_id: str, folder_id: str, body: NoteFolderBody) -> dict[str, Any]:
    return notes_service.save_folder(project_id, body.title, folder_id)


@app.get("/project/{project_id}/notes")
def api_list_notes(project_id: str) -> dict[str, Any]:
    return {"notes": notes_service.list_notes(project_id)}


@app.post("/project/{project_id}/notes")
def api_create_note(project_id: str, body: NoteCreateBody) -> dict[str, Any]:
    return notes_service.create_note(project_id, body.chapter_no, body.title, body.body, body.folder)


@app.get("/project/{project_id}/notes/reference")
def api_note_reference(project_id: str, chapter_no: int = Query(...)) -> dict[str, Any]:
    return {"chapter_no": chapter_no, "status": "none", "reference": "", "notes": [], "error": ""}


@app.put("/project/{project_id}/notes/{note_id}")
def api_update_note(project_id: str, note_id: str, body: NoteUpdateBody) -> dict[str, Any]:
    return notes_service.update_note(project_id, note_id, body.title, body.body, body.content_version, body.folder)


@app.delete("/project/{project_id}/notes/{note_id}")
def api_delete_note(project_id: str, note_id: str) -> dict[str, bool]:
    return notes_service.delete_note(project_id, note_id)


@app.patch("/project/{project_id}/notes/{note_id}/pin")
def api_pin_note(project_id: str, note_id: str, body: NotePinBody) -> dict[str, Any]:
    return {"notes": notes_service.pin_note(project_id, note_id, body.pinned)}


@app.post("/project/{project_id}/notes/{note_id}/move")
def api_move_note(project_id: str, note_id: str, body: NoteMoveBody) -> dict[str, Any]:
    return {"notes": notes_service.move_note(project_id, note_id, body.direction, body.operation_id)}


@app.post("/project/{project_id}/notes/{note_id}/copy")
def api_copy_note(project_id: str, note_id: str, body: NoteCopyBody) -> dict[str, Any]:
    return notes_service.copy_note(project_id, note_id, body.title, body.operation_id)


@app.post("/project/{project_id}/notes/{note_id}/distill")
def api_distill_note(project_id: str, note_id: str, body: NoteDistillBody) -> dict[str, Any]:
    raise HTTPException(410, detail={"code": "NOTES_STANDALONE", "message": "笔记仅供个人整理，不再参与 AI 创作"})


@app.put("/project/{project_id}/notes/{note_id}/reference")
def api_set_note_reference(project_id: str, note_id: str, body: NoteReferenceBody) -> dict[str, Any]:
    raise HTTPException(410, detail={"code": "NOTES_STANDALONE", "message": "笔记写作参考已停用"})


# ---- versions：章节历史版本 ----

@app.get("/project/{project_id}/chapter/{n}/versions")
def api_list_versions(project_id: str, n: int) -> dict[str, Any]:
    return {"versions": versions_service.list_versions(project_id, n)}


@app.get("/project/{project_id}/chapter/{n}/versions/{version_id}")
def api_get_version(project_id: str, n: int, version_id: str) -> dict[str, Any]:
    return versions_service.get_version(project_id, n, version_id)


@app.post("/project/{project_id}/chapter/{n}/versions/{version_id}/restore")
def api_restore_version(project_id: str, n: int, version_id: str) -> Chapter:
    return versions_service.restore(project_id, n, version_id)


# ---- search：全书 / 本章查找 ----

class SearchBody(BaseModel):
    query: str = ""
    case_sensitive: bool = False
    scope: str = "book"
    chapter_no: Optional[int] = None
    draft_text: Optional[str] = None  # null=没有正在编辑的章；""=当前章草稿为空，覆盖库正文
    page: int = 1
    page_size: int = 50


@app.post("/project/{project_id}/search")
def api_search(project_id: str, body: SearchBody) -> dict[str, Any]:
    return search_service.search(
        project_id,
        body.query,
        case_sensitive=body.case_sensitive,
        scope=body.scope,
        chapter_no=body.chapter_no,
        draft_text=body.draft_text,
        page=body.page,
        page_size=body.page_size,
    )


def _note_reference(project_id: str, chapter_no: int) -> str:
    """兼容旧调用；个人笔记和历史提炼内容均不进入 AI 上下文。"""
    return ""


def _require_rhythm_alignment(project: Project, chapter: Chapter, token: str,
                              *, plan: ChapterPlan | None = None, notes: str = "") -> None:
    from .rhythm_alignment import check_alignment
    issues = check_alignment(project, chapter, plan=plan, notes=notes)
    if context_revision(project.id, chapter.no) != token:
        raise ws.conflict("正文或上下文已修改，请重试", "BODY_CHANGED")
    if issues:
        raise HTTPException(409, detail={"code": "PLAN_RHYTHM_MISMATCH",
            "message": "本章规划与叠条节奏有明确差异，请调整后重试",
            "project_id": project.id, "chapter_no": chapter.no, "issues": issues})


# ---- proposal workspace (legacy plan routes retain their direct-write semantics) ----

from .models import ProposalCommand
from . import proposals as proposal_service
from . import body_revisions as revision_service
from .models import BodyRevisionCommand


@app.get("/project/{project_id}/chapter/{n}/body-revision")
def api_body_revision(project_id: str, n: int):
    return revision_service.view(project_id, n)


@app.put("/project/{project_id}/chapter/{n}/body-revision")
def api_body_revision_save(project_id: str, n: int, body: BodyRevisionCommand):
    return revision_service.execute(project_id, n, body, "save")


@app.post("/project/{project_id}/chapter/{n}/body-revision/preview")
def api_body_revision_preview(project_id: str, n: int, body: BodyRevisionCommand):
    return revision_service.execute(project_id, n, body, "preview")


@app.post("/project/{project_id}/chapter/{n}/body-revision/apply")
def api_body_revision_apply(project_id: str, n: int, body: BodyRevisionCommand):
    return revision_service.execute(project_id, n, body, "apply")


@app.get("/project/{project_id}/chapter/{n}/proposal")
def api_proposal(project_id: str, n: int):
    return proposal_service.view(project_id, n)


@app.post("/project/{project_id}/chapter/{n}/proposal")
def api_proposal_generate(project_id: str, n: int, body: ProposalCommand):
    return proposal_service.generate(project_id, n, body)


@app.put("/project/{project_id}/chapter/{n}/proposal/answers")
def api_proposal_answers(project_id: str, n: int, body: ProposalCommand):
    return proposal_service.save_answers(project_id, n, body)


@app.post("/project/{project_id}/chapter/{n}/proposal/confirm")
def api_proposal_confirm(project_id: str, n: int, body: ProposalCommand):
    return proposal_service.confirm(project_id, n, body)


@app.get("/project/{project_id}/chapter/{n}/plans")
def api_proposal_history(project_id: str, n: int):
    return proposal_service.history(project_id, n)


@app.get("/project/{project_id}/chapter/{n}/plans/{record_id}")
def api_proposal_record(project_id: str, n: int, record_id: str):
    return proposal_service.history_record(project_id, n, record_id)


@app.post("/project/{project_id}/chapter/{n}/plans/{record_id}/restore")
def api_proposal_restore(project_id: str, n: int, record_id: str, body: ProposalCommand):
    return proposal_service.restore(project_id, n, record_id, body)


@app.delete("/project/{project_id}/chapter/{n}/plans/{record_id}")
def api_proposal_delete(project_id: str, n: int, record_id: str, body: ProposalCommand):
    return proposal_service.delete_record(project_id, n, record_id, body)


# ---- plan / generate / finalize / gate / refine ----


@app.post("/project/{project_id}/plan")
def api_plan(project_id: str, body: PlanRequest) -> Chapter:
    proj = get_project(project_id)
    assert_phase(proj.phase, {Phase.PLAN, Phase.SETUP, Phase.REPLAN, Phase.GATE, Phase.REFINE}, "plan")
    if proj.phase != Phase.PLAN:
        proj.phase = Phase.PLAN
        save_project(proj)
        proj = get_project(project_id)

    chapters = list_chapters(project_id)
    next_no = max([c.no for c in chapters if c.status != ChapterStatus.superseded], default=0) + 1
    # if current chapter exists as planned without body, reuse
    existing_planned = [
        c for c in chapters
        if c.status == ChapterStatus.planned and not c.body_text
    ]
    if existing_planned and proj.current_chapter == existing_planned[-1].no:
        next_no = existing_planned[-1].no

    refinement = None
    prev = [c for c in chapters if c.no == next_no - 1]
    if prev and prev[0].refinement:
        refinement = prev[0].refinement

    existing = next(
        (c for c in chapters if c.no == next_no and c.status != ChapterStatus.superseded), None
    )
    planning_token = context_revision(project_id, next_no)
    # 新章节继承作品级设定（= 上一章设定 + 细修时的脉络/设定修改）
    lore = (existing.lore if existing else None) or project_lore(proj)

    if existing:
        _require_rhythm_alignment(lore_view(proj, lore), existing, planning_token,
                                  plan=body.overrides, notes=body.user_notes)

    if body.overrides:
        plan = body.overrides
    else:
        author_notes = body.user_notes
        if existing and existing.plan:
            author_notes = "\n\n".join(s for s in [author_notes, existing.plan.free_text, existing.plan.misc] if s.strip())
        plan, _ = run_plan(
            lore_view(proj, lore), chapters, author_notes, refinement,
            target_chapter=next_no, note_reference=_note_reference(project_id, next_no),
        )
    # 复用已规划章节时保留用户在规划区自由书写的内容
    if existing and existing.plan and not body.overrides:
        plan.free_text = existing.plan.free_text
        plan.misc = existing.plan.misc
    if existing:
        associate(plan, existing.stacked_constraints)
        _require_rhythm_alignment(lore_view(proj, lore), existing, planning_token,
                                  plan=plan, notes=body.user_notes)

    ch = Chapter(
        no=next_no,
        **({"chapter_uid": existing.chapter_uid} if existing else {}),
        title=existing.title if existing and existing.title else f"第{next_no}章",
        status=ChapterStatus.planned,
        plan=plan,
        created_at=utcnow(),
        gate=ReadingGate(chapter_no=next_no),
        lore=lore,
        lore_from=existing.lore_from if existing else (next_no - 1 if next_no > 1 else None),
        # 复用已规划章节时保留作者手写的本章行动；新章节默认空。
        character_actions=existing.character_actions if existing else {},
        stacked_constraints=existing.stacked_constraints if existing else StackedConstraints(),
    )
    upsert_chapter(project_id, ch, expected_context=planning_token)
    proj.phase = Phase.PLAN
    proj.current_chapter = next_no
    save_project(proj)
    with db_connect() as conn:
        append_event(conn, project_id, "ChapterPlanned", plan.model_dump(), next_no)
    return ch


@app.post("/project/{project_id}/chapter")
def api_generate(project_id: str, body: GenerateRequest) -> Chapter:
    proj = get_project(project_id)
    chapters = list_chapters(project_id)

    # 上一章（已生成/已定稿）须先提交「创作下一章」（评注 + 下一章意图）
    priors = [
        c for c in chapters
        if c.status in (ChapterStatus.final, ChapterStatus.drafted)
        and c.no < proj.current_chapter
    ]
    if priors:
        last = sorted(priors, key=lambda c: c.no)[-1]
        if last.refinement is None:
            raise HTTPException(
                409,
                detail={
                    "code": "REFINE_REQUIRED",
                    "message": f"请先在第 {last.no} 章的评注区点「下一步」，再继续",
                    "chapter_no": last.no,
                },
            )

    ch = next(
        (c for c in chapters if c.no == proj.current_chapter and c.status != ChapterStatus.superseded), None
    )
    if ch is None:
        raise HTTPException(400, detail={"code": "NO_CHAPTER", "message": "先规划或写下本章内容"})

    if body.memory_policy not in ("extract", "raw"):
        raise HTTPException(400, detail={"code": "MEMORY_POLICY", "message": "请选择补齐摘要或使用原文"})
    if body.memory_policy == "extract":
        # Work chronologically: an old upstream edit invalidates dependent memory.
        for prior in sorted(priors, key=lambda c: c.no):
            if not summary_valid(get_chapter(project_id, prior.no)):
                try:
                    api_extract(project_id, prior.no)
                except Exception as exc:
                    raise HTTPException(409, detail={"code": "MEMORY_NOT_READY", "chapter_no": prior.no,
                        "message": f"第 {prior.no} 章摘要尚未更新，请重试提取，或明确选择使用原文继续"}) from exc
        chapters = list_chapters(project_id)

    with chapter_lock(project_id):
        # Capture the actual generation inputs and their token together, after memory
        # extraction and before either automatic planning or text generation can call AI.
        proj = get_project(project_id)
        chapters = list_chapters(project_id)
        ch = get_chapter(project_id, ch.no)
        token = context_revision(project_id, ch.no)
        revision = body_hash(ch.body_text)
        view = lore_view(proj, ch.lore)
    from .proposal_inputs import selected_adopted, effective_inputs, digest
    adopted = selected_adopted(proj, ch, chapters, raw_history=body.memory_policy == "raw")
    used_adopted_identity = digest(ch.proposal.adopted)
    compiled_layers = None
    if adopted is not None:
        ch.plan = adopted.final_plan.model_copy(deep=True)
        compiled_layers = effective_inputs(proj, ch, chapters, raw_history=body.memory_policy == "raw")["layers"]
    else:
        _require_rhythm_alignment(view, ch, token)
        if _plan_form_empty(ch.plan):
            ch.plan = _ai_plan(view, chapters, ch)
            _require_rhythm_alignment(view, ch, token)

    # 正文已有内容（手写或之前生成）时接着往下写
    prefix = body.prefix or ch.body_text
    written_text = ch.body_text
    written_rich = ch.body_rich
    generation_input_hash = effective_inputs(proj, ch, chapters, raw_history=body.memory_policy == "raw")["hash"]
    text, _ = run_generate(
        view, ch.plan, chapters, prefix=prefix, target_chapter=ch.no,
        raw_history=body.memory_policy == "raw",
        note_reference=_note_reference(project_id, ch.no),
        adopted_proposal=adopted, compiled_layers=compiled_layers,
    )
    # 覆盖前留一版（source=ai）；空稿/最近一版同 hash 时 checkpoint 自己会跳过
    with chapter_lock(project_id):
        latest = get_chapter(project_id, ch.no)
        if latest.chapter_uid != ch.chapter_uid or digest(latest.proposal.adopted) != used_adopted_identity:
            raise ws.conflict("正文使用的采用稿或章节身份已改变，请重新生成", "PROPOSAL_CHANGED")
        comparison = latest.model_copy(deep=True)
        # Automatic planning may have supplied an in-memory plan. The existing
        # context token guards author plan changes; compare its actual dependencies
        # separately so edits to active skills/retrieved memory cannot slip through.
        comparison.plan = ch.plan
        if effective_inputs(get_project(project_id), comparison, list_chapters(project_id),
                            raw_history=body.memory_policy == "raw")["hash"] != generation_input_hash:
            raise ws.conflict("正文或上下文已修改，请重试", "BODY_CHANGED")
        versions_service.checkpoint(project_id, ch.no, written_text, written_rich, "ai")
        ch.body_text = text
        ch.body_rich = ""
        ch.word_count = len(text)
        ch.status = ChapterStatus.drafted
        ch.created_at = ch.created_at or utcnow()
        upsert_chapter(project_id, ch, expected_hash=revision, expected_context=token)
    set_phase(project_id, Phase.EXTRACT, ch.no)
    with db_connect() as conn:
        append_event(conn, project_id, "ChapterGenerated", {"word_count": ch.word_count}, ch.no)
    return get_chapter(project_id, ch.no)


def _plan_form_empty(plan: ChapterPlan | None) -> bool:
    """表单字段都没填（自由规划可能有内容）时视为还没有计划。"""
    if plan is None:
        return True
    return not any(
        [plan.scene_goal.strip(), plan.conflict.strip(), plan.stakes.strip(), plan.must_include,
         plan.must_not, plan.open_decision_for_user]
    )


def _ai_plan(view: Project, chapters: list[Chapter], ch: Chapter) -> ChapterPlan:
    """让模型填写计划表单；用户的自由规划作为提示传入并原样保留。"""
    free = ch.plan.free_text if ch.plan else ""
    misc = ch.plan.misc if ch.plan else ""
    prev = next((c for c in chapters if c.no == ch.no - 1), None)
    plan, _ = run_plan(
        view, chapters, "\n\n".join(s for s in [free, misc] if s.strip()), prev.refinement if prev else None,
        target_chapter=ch.no, note_reference=_note_reference(view.id, ch.no),
    )
    plan.free_text = free
    plan.misc = misc
    return associate(plan, ch.stacked_constraints)


@app.put("/project/{project_id}/chapter/{n}/stacked-constraints")
def api_save_stacked_constraints(project_id: str, n: int, body: StackedConstraints) -> Chapter:
    with chapter_lock(project_id):
        ch = get_chapter(project_id, n)
        if ch.status == ChapterStatus.superseded:
            raise HTTPException(400, detail={"code": "SUPERSEDED", "message": "已作废章节不可修改"})
        proj = get_project(project_id)
        lore = ch.lore or project_lore(proj)
        catalogs = {"character": {c.id: c.name for c in [*lore.characters, *proj.characters]},
                    "thread": {t.id: t.name for t in [*proj.threads, *lore.threads]},
                    "scene": dict(zip(skills.SCENES, ("打斗", "感情", "悬疑", "日常", "悲情", "对峙")))}
        previous = {(t.domain, t.element_id): t for t in ch.stacked_constraints.tracks}
        for track in body.tracks:
            name = catalogs[track.domain].get(track.element_id)
            if name is None and (track.domain, track.element_id) not in previous:
                raise HTTPException(422, detail={"code": "CONSTRAINT_ELEMENT", "message": "请选择已有的叠条元素"})
            track.name = (name or track.element_id) if name is not None else previous[(track.domain, track.element_id)].name
        ch.stacked_constraints = body
        if body.tracks:
            ch.plan = associate(ch.plan or ChapterPlan(), body)
            known_chars = {c.id for c in lore.characters}
            known_threads = {t.id for t in lore.threads}
            lore.characters.extend(c.model_copy(deep=True) for c in proj.characters
                                   if c.id in ch.plan.present_character_ids and c.id not in known_chars)
            lore.threads.extend(t.model_copy(deep=True) for t in proj.threads
                                if t.id in ch.plan.required_threads and t.id not in known_threads)
            ch.lore = lore
        upsert_chapter(project_id, ch)
        return get_chapter(project_id, n)


@app.post("/project/{project_id}/chapter/{n}/stacked-constraints/migration/ack")
def api_ack_stacked_weight_migration(project_id: str, n: int) -> Chapter:
    with chapter_lock(project_id):
        ch = get_chapter(project_id, n)
        ch.stacked_weight_migration_pending = False
        upsert_chapter(project_id, ch)
        return get_chapter(project_id, n)


@app.put("/project/{project_id}/chapter/{n}/plan")
def api_save_plan(project_id: str, n: int, body: ChapterPlan) -> Chapter:
    # 覆盖整章之前必须把读取也纳入锁，避免并发正文保存被旧快照回滚。
    with chapter_lock(project_id):
        ch = get_chapter(project_id, n)
        if ch.status == ChapterStatus.superseded:
            raise HTTPException(400, detail={"code": "SUPERSEDED", "message": "已作废章节不可修改"})
        body.free_text = body.free_text.replace("\r\n", "\n")
        body.misc = body.misc.replace("\r\n", "\n")
        for track in ch.stacked_constraints.tracks:
            selected = {"thread": body.required_threads, "character": body.present_character_ids,
                        "scene": body.scene_types}[track.domain]
            if track.element_id not in selected:
                raise HTTPException(409, detail={"code": "STACKED_ELEMENT_IN_USE",
                    "message": "该元素仍有叠条约束，请先移除对应叠条"})
        ch.plan = body
        # 加入规划的主档资料同时进入该章快照，供 lore_view / AI 使用；移出规划不删除设定。
        proj = get_project(project_id)
        lore = ch.lore or project_lore(proj)
        known_characters = {c.id for c in lore.characters}
        known_threads = {t.id for t in lore.threads}
        lore.characters.extend(c.model_copy(deep=True) for c in proj.characters
                               if c.id in body.present_character_ids and c.id not in known_characters)
        lore.threads.extend(t.model_copy(deep=True) for t in proj.threads
                            if t.id in body.required_threads and t.id not in known_threads)
        ch.lore = lore
        ch.plan_upstream_changed = []
        upsert_chapter(project_id, ch)
        return get_chapter(project_id, n)


@app.post("/project/{project_id}/chapter/{n}/replan")
def api_replan(project_id: str, n: int) -> Chapter:
    """规划区「AI 规划」：重新生成第 n 章的计划表单，保留自由规划。"""
    token = context_revision(project_id, n)
    proj = get_project(project_id)
    chapters = list_chapters(project_id)
    ch = next((c for c in chapters if c.no == n), None)
    if ch is None:
        raise HTTPException(404, detail="chapter not found")
    if ch.status == ChapterStatus.superseded:
        raise HTTPException(400, detail={"code": "SUPERSEDED", "message": "已作废章节不可修改"})
    view = lore_view(proj, ch.lore)
    _require_rhythm_alignment(view, ch, token)
    ch.plan = _ai_plan(view, chapters, ch)
    _require_rhythm_alignment(view, ch, token)
    ch.plan_upstream_changed = []
    upsert_chapter(project_id, ch, expected_context=token)
    with db_connect() as conn:
        append_event(conn, project_id, "ChapterPlanned", ch.plan.model_dump(), n)
    return get_chapter(project_id, n)


@app.put("/project/{project_id}/chapter/{n}/review")
def api_save_review(project_id: str, n: int, body: ReviewDraft) -> Chapter:
    ch = get_chapter(project_id, n)
    ch.review = body
    upsert_chapter(project_id, ch)
    return get_chapter(project_id, n)


@app.post("/project/{project_id}/chapter/{n}/lore/inherit")
def api_inherit_lore(project_id: str, n: int, from_no: int = Query(..., alias="from")) -> Chapter:
    return inherit_lore(project_id, n, from_no)


@app.post("/project/{project_id}/chapters")
def api_create_chapter(project_id: str, body: ChapterCreate) -> Chapter:
    """直接在空白页（正文或规划）书写时新建下一章。"""
    proj = get_project(project_id)
    chapters = list_chapters(project_id)
    no = max([c.no for c in chapters if c.status != ChapterStatus.superseded], default=0) + 1
    text = (body.body_text or "").replace("\r\n", "\n")
    ch = Chapter(
        no=no,
        title=f"第{no}章",
        status=ChapterStatus.drafted if text.strip() else ChapterStatus.planned,
        plan=body.plan,
        body_text=text,
        body_rich=body.body_rich or "",
        word_count=len(text),
        created_at=utcnow(),
        gate=ReadingGate(chapter_no=no),
        lore=project_lore(proj),
        lore_from=no - 1 if no > 1 else None,
    )
    upsert_chapter(project_id, ch)
    proj = get_project(project_id)
    proj.current_chapter = no
    proj.phase = Phase.PLAN
    save_project(proj)
    with db_connect() as conn:
        append_event(conn, project_id, "ChapterCreated", {"word_count": ch.word_count}, no)
    return get_chapter(project_id, no)


@app.put("/project/{project_id}/chapter/{n}/body")
def api_save_body(project_id: str, n: int, body: BodyUpdate) -> Chapter:
    with chapter_lock(project_id):
        ch = get_chapter(project_id, n)
        text = (body.body_text or "").replace("\r\n", "\n")
        rich = body.body_rich or ""
        if text != ch.body_text or rich != ch.body_rich:
            # 覆盖前留一版：首次改动 source=original 保留原稿，之后按 autosave 的 5 分钟窗口节流
            versions_service.checkpoint_save(project_id, n, ch.body_text, ch.body_rich)
        ch.body_text = text
        ch.body_rich = rich
        ch.word_count = len(text)
        if text and ch.status == ChapterStatus.planned:
            ch.status = ChapterStatus.drafted
        upsert_chapter(project_id, ch)
        return get_chapter(project_id, n)


def _require_body(ch: Chapter, message: str) -> None:
    if not ch.body_text.strip():
        raise HTTPException(400, detail={"code": "EMPTY", "message": message})


@app.post("/project/{project_id}/chapter/{n}/finalize")
def api_finalize(project_id: str, n: int) -> Chapter:
    proj = get_project(project_id)
    ch = get_chapter(project_id, n)
    _require_body(ch, "无正文可定稿")

    revision = body_hash(ch.body_text)
    ch.style_report = style_check(ch.body_text, proj.style)
    stamp(ch, "style_report")
    ch.status = ChapterStatus.final
    if n == 1 or not proj.style.metrics:
        proj.style = update_baseline_from_chapter(proj.style, ch.body_text)
        save_project(proj)

    # 定稿也留一版（最近一版同 hash 时 checkpoint 自己会跳过）
    versions_service.checkpoint(project_id, n, ch.body_text, ch.body_rich, "finalize")
    upsert_chapter(project_id, ch, expected_hash=revision)
    set_phase(project_id, Phase.REFINE, n)
    with db_connect() as conn:
        append_event(conn, project_id, "ChapterFinalized", {}, n)
    if not summary_valid(ch):
        try:
            api_extract(project_id, n)
        except Exception as exc:
            # Finalization is already durable. Extraction has its own visible failure/retry state.
            logging.getLogger(__name__).warning("Automatic summary failed for chapter %s: %s", n, type(exc).__name__)
    return get_chapter(project_id, n)


@app.post("/project/{project_id}/chapter/{n}/unfinalize")
def api_unfinalize(project_id: str, n: int) -> Chapter:
    """回到已生成状态以便继续改；文风基线不动。"""
    proj = get_project(project_id)
    ch = get_chapter(project_id, n)
    if ch.status != ChapterStatus.final:
        raise HTTPException(409, detail={"code": "NOT_FINAL", "message": "本章还没有定稿"})
    ch.status = ChapterStatus.drafted
    ch.style_report = None
    upsert_chapter(project_id, ch)
    if proj.current_chapter == n and proj.phase == Phase.REFINE:
        set_phase(project_id, Phase.GENERATE, n)
    with db_connect() as conn:
        append_event(conn, project_id, "ChapterUnfinalized", {}, n)
    return get_chapter(project_id, n)


@app.post("/project/{project_id}/chapter/{n}/extract")
def api_extract(project_id: str, n: int) -> Chapter:
    proj = get_project(project_id)
    ch = get_chapter(project_id, n)
    _require_body(ch, "无正文可提取摘要")

    revision = body_hash(ch.body_text)
    ch.summary_status = "extracting"
    ch.summary_error = ""
    upsert_chapter(project_id, ch, expected_hash=revision)
    chapters = list_chapters(project_id)
    token = context_revision(project_id, n)
    view = lore_view(proj, ch.lore)
    try:
        summary, delta, _ = run_extract(view, ch, ch.plan, chapters)
    except Exception:
        with chapter_lock(project_id):
            latest = get_chapter(project_id, n)
            if body_hash(latest.body_text) == revision:
                latest.summary_status = "failed"
                latest.summary_error = "摘要提取失败，请检查模型连接后重试"
                upsert_chapter(project_id, latest, expected_hash=revision)
        raise
    # 这里**不写** consistency_report：它是「语义审计」的产物，只有 api_audit 该写这一列。
    # 以前这里顺带跑一次纯规则审计并覆盖同一列，规则层通常 0 命中 → 语义审计的结果被静默抹成空报告，
    # 界面上看起来就像「本章没有问题」。规则/状态检查属于审计动作，不属于提取摘要。
    with chapter_lock(project_id):
        latest = get_chapter(project_id, n)
        if context_revision(project_id, n) != token:
            if latest.summary_status == "extracting":
                latest.summary_status = "stale"
                upsert_chapter(project_id, latest)
            raise ws.conflict("正文或设定已修改，请重新提取摘要", "BODY_CHANGED")
        latest.summary = summary
        latest.state_delta = delta
        # A successful re-extraction explicitly acknowledges updated upstream context.
        latest.upstream_changed = []
        stamp(latest, "summary", "state_delta")
        upsert_chapter(project_id, latest, expected_hash=revision,
                       facts=[fact.model_dump() for fact in summary.new_facts], expected_context=token)
    with db_connect() as conn:
        append_event(conn, project_id, "SummaryExtracted", summary.model_dump(), n)
    return get_chapter(project_id, n)


@app.post("/project/{project_id}/chapter/{n}/gate")
def api_gate(project_id: str, n: int, body: GateConfirmRequest) -> ReadingGate:
    proj = get_project(project_id)
    ch = get_chapter(project_id, n)
    with connect() as conn:
        gate_mode = get_setting(conn, "gate_mode", "behavior")

    satisfied = False
    if gate_mode == "soft":
        satisfied = True
    elif gate_mode == "behavior":
        # dwell + scroll thresholds
        satisfied = body.dwell_seconds >= 8.0 and body.scroll_ratio >= 0.6
        if body.method == "manual" and body.dwell_seconds >= 3 and body.scroll_ratio >= 0.4:
            satisfied = True
    else:  # quiz not fully implemented — treat manual confirm after read metrics
        satisfied = body.dwell_seconds >= 5 and body.scroll_ratio >= 0.5

    if not satisfied:
        raise HTTPException(
            status_code=409,
            detail={
                "code": "GATE_NOT_SATISFIED",
                "message": "阅读时长或滚动覆盖不足，请继续阅读后确认",
                "dwell_seconds": body.dwell_seconds,
                "scroll_ratio": body.scroll_ratio,
            },
        )

    gate = ReadingGate(
        chapter_no=n,
        opened_at=ch.gate.opened_at if ch.gate else utcnow(),
        read_confirmed_at=utcnow(),
        dwell_seconds=body.dwell_seconds,
        scroll_ratio=body.scroll_ratio,
        method=body.method,
        satisfied=True,
    )
    ch.gate = gate
    upsert_chapter(project_id, ch)
    set_phase(project_id, Phase.REFINE, n)
    with db_connect() as conn:
        append_event(conn, project_id, "GateConfirmed", gate.model_dump(mode="json"), n)
    return gate


@app.post("/project/{project_id}/chapter/{n}/refine")
def api_refine(project_id: str, n: int, body: Refinement) -> Project:
    proj = get_project(project_id)
    ch = get_chapter(project_id, n)
    body.chapter_no = n

    # apply thread updates
    threads = {t.id: t for t in proj.threads}
    for tu in body.thread_updates:
        if tu.thread_id in threads:
            t = threads[tu.thread_id]
            data = t.model_dump()
            if tu.new_directive:
                data["user_directive"] = tu.new_directive
            if tu.new_state:
                data["state"] = tu.new_state
            from .models import PlotThread

            threads[tu.thread_id] = PlotThread(**data)
    proj.threads = list(threads.values())

    # bible edits with conflict options — apply non-empty
    for e in body.bible_edits:
        try:
            _apply_bible_path(proj, e.path, e.new)
        except HTTPException:
            pass

    ch.refinement = body
    ch.review = None
    upsert_chapter(project_id, ch)
    save_project(proj)
    # 细修较早章节时（例如下一章已先规划好）不能把当前章拨回去
    current = max(proj.current_chapter, n)
    set_phase(project_id, Phase.REPLAN, current)
    # move to PLAN for next
    set_phase(project_id, Phase.PLAN, current)
    with db_connect() as conn:
        append_event(conn, project_id, "RefinementSubmitted", body.model_dump(), n)
    return get_project(project_id)


@app.post("/project/{project_id}/rollback/{n}")
def api_rollback(project_id: str, n: int) -> Project:
    get_chapter(project_id, n)  # ensure exists
    supersede_after(project_id, n)
    # mark chapter n as final retained; later superseded
    ch = get_chapter(project_id, n)
    # reopen gate requirement for regenerating n+1 from here
    proj = get_project(project_id)
    proj.current_chapter = n
    proj.phase = Phase.PLAN
    save_project(proj)
    # clear planned chapters after n that are empty? supersede handles status
    return proj


@app.post("/project/{project_id}/chapter/{n}/style-rewrite")
def api_style_rewrite(project_id: str, n: int) -> Chapter:
    """Rewrite once using chapter 1 as reference — no recursive rewrite."""
    proj = get_project(project_id)
    ch = get_chapter(project_id, n)
    if not ch.plan:
        raise HTTPException(400, detail={"code": "NO_PLAN"})
    chapters = list_chapters(project_id)
    ref = ""
    ch1 = next((c for c in chapters if c.no == 1 and c.body_text), None)
    if ch1:
        proj.style.reference_samples = [ch1.body_text[:1500]]
    prefix = ch.body_text[:80] if ch.body_text else ""
    revision = body_hash(ch.body_text)
    token = context_revision(project_id, n)
    written_text = ch.body_text
    written_rich = ch.body_rich
    text, _ = run_generate(
        lore_view(proj, ch.lore), ch.plan, chapters, prefix=prefix, target_chapter=n,
        note_reference=_note_reference(project_id, n),
    )
    versions_service.checkpoint(project_id, n, written_text, written_rich, "ai")
    ch.body_text = text
    ch.body_rich = ""
    ch.word_count = len(text)
    ch.status = ChapterStatus.drafted
    upsert_chapter(project_id, ch, expected_hash=revision, expected_context=token)
    return save_analysis(project_id, ch, context_revision(project_id, n), style_report=style_check(text, proj.style))


@app.get("/project/{project_id}/export")
def api_export(project_id: str, fmt: str = "markdown", font: str = "") -> dict[str, str]:
    proj = get_project(project_id)
    root = ws.check_root(ws.root_of(project_id))
    out_dir = root / ws.EXPORT_DIR if root else get_storage() / "projects" / project_id / "export"
    out_dir.mkdir(parents=True, exist_ok=True)
    name = ws.sanitize_name(proj.title, "未命名作品")
    if fmt == "epub":
        # font 是写作面当前主题的正文字体，写进 EPUB 的样式表，让电子书和界面同款排印
        path = export_epub(proj, out_dir / f"{name}.epub", font=font or None)
    elif fmt == "docx":
        path = export_docx(proj, out_dir / f"{name}.docx", font=font or None)
    elif fmt == "txt":
        path = export_txt(proj, out_dir / f"{name}.txt")
    elif fmt == "bible":
        path = export_bible_appendix(proj, out_dir / "bible.md")
    else:
        path = export_markdown(proj, out_dir / f"{name}.md")
    return {"path": str(path)}


class PublishRequest(BaseModel):
    """发布包请求：platforms 为空会被拒；indent / with_head 留空表示用平台默认。"""

    platforms: list[str] = Field(default_factory=list)
    scope: str = "chapter"
    chapter_no: int = 0
    author: str = ""
    synopsis: str = ""
    indent: str = ""
    with_head: Optional[bool] = None


@app.get("/publish/platforms")
def api_publish_platforms() -> dict[str, Any]:
    """平台表（含投稿入口与要求速查）；客户端不硬编码任何平台。"""
    return platform_table()


@app.post("/project/{project_id}/publish/prepare")
def api_publish_prepare(project_id: str, req: PublishRequest) -> dict[str, Any]:
    """生成各平台发布包到「导出/发布/<平台>/」，并返回剪贴板负载。"""
    proj = get_project(project_id)
    return prepare_publish(
        proj,
        platforms=req.platforms,
        scope=req.scope,
        chapter_no=req.chapter_no,
        author=req.author,
        synopsis=req.synopsis,
        indent=req.indent,
        with_head=req.with_head,
    )


@app.post("/project/{project_id}/audit/{n}")
def api_audit(project_id: str, n: int, semantic: bool = False) -> Chapter:
    proj = get_project(project_id)
    ch = get_chapter(project_id, n)
    chapters = [c for c in list_chapters(project_id) if c.no < n and c.status != ChapterStatus.superseded]
    token = context_revision(project_id, n)
    ch.consistency_report = run_audit(lore_view(proj, ch.lore), ch, chapters, semantic=semantic)
    ch.style_report = style_check(ch.body_text, proj.style)
    return save_analysis(project_id, ch, token, consistency_report=ch.consistency_report, style_report=ch.style_report)


@app.post("/project/{project_id}/chapter/{n}/plan-check")
def api_plan_check(project_id: str, n: int, semantic: bool = True) -> Chapter:
    """「本章校验」：核对计划里的约束有没有被正文照做。结果写 Chapter.plan_check，不碰审计报告。"""
    token = context_revision(project_id, n)
    proj = get_project(project_id)
    ch = get_chapter(project_id, n)
    chapters = [c for c in list_chapters(project_id) if c.no < n and c.status != ChapterStatus.superseded]
    ch.plan_check = run_plan_check(lore_view(proj, ch.lore), ch, chapters, semantic=semantic)
    save_analysis(project_id, ch, token, plan_check=ch.plan_check)
    return get_chapter(project_id, n)


@app.post("/project/{project_id}/memory/reindex")
def api_reindex_memory(project_id: str) -> dict[str, Any]:
    get_project(project_id)
    from .retrieval import reindex
    return reindex(project_id)
