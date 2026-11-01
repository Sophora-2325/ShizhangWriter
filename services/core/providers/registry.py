"""模型预设目录、用户添加的模型配置（实例）和密钥，以及按作品设置解析出要用的实例。

用户添加的配置存在 app_settings.provider_instances（JSON 列表：
[{id, preset, name, base_url, model_strong, model_fast, thinking, price}]），同一预设可以添加多个；
留空的字段沿用预设默认值。密钥存系统凭据管理器，键名 <实例 id>_api_key。
规划 / 审计用 model_strong，生成 / 摘要用 model_fast。
"""
from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from typing import Any, Optional

from fastapi import HTTPException

from .. import secret_store
from ..db import connect, get_setting, set_setting
from .anthropic_provider import AnthropicProvider
from .base import FREE, Price, lookup_price, valid_price
from .gemini_provider import GeminiProvider
from .mock import MockProvider
from .openai_compat import OpenAICompatibleProvider

INHERIT = "inherit"
MOCK = "mock"
LEGACY_CONFIG_KEY = "provider_configs"
INSTANCES_KEY = "provider_instances"
STRONG_PURPOSES = ("plan", "audit", "skill_distill")
FIELDS = ("base_url", "model_strong", "model_fast", "thinking", "price")
MAX_NAME = 60


@dataclass(frozen=True)
class ProviderSpec:
    id: str
    name: str
    kind: str  # openai | anthropic | gemini | compat | local | custom | mock
    group: str  # intl | cn | local
    base_url: str = ""
    model_strong: str = ""
    model_fast: str = ""
    env: tuple[str, ...] = ()
    key_url: str = ""
    thinking_style: Optional[str] = None
    prefix_style: Optional[str] = None
    max_tokens_param: str = "max_tokens"
    needs_key: bool = True
    cloud: bool = True
    hidden: bool = False
    max_context: int = 128_000
    max_output: int = 8_192
    models: tuple[str, ...] = field(default_factory=tuple)


CATALOG: tuple[ProviderSpec, ...] = (
    ProviderSpec(
        "openai", "OpenAI", "openai", "intl", "https://api.openai.com/v1", "gpt-6.1-sol", "gpt-6-luna",
        env=("OPENAI_API_KEY",), key_url="https://platform.openai.com/api-keys",
        thinking_style="reasoning_effort", max_tokens_param="max_completion_tokens",
        max_context=400_000, max_output=128_000,
    ),
    ProviderSpec(
        "anthropic", "Claude (Anthropic)", "anthropic", "intl", "", "claude-opus-5-5", "claude-sonnet-5-5",
        env=("ANTHROPIC_API_KEY",), key_url="https://console.anthropic.com/settings/keys",
        thinking_style="anthropic", prefix_style="prefill", max_context=200_000, max_output=64_000,
    ),
    ProviderSpec(
        "gemini", "Google Gemini", "gemini", "intl", "", "gemini-3.1-pro-preview", "gemini-3.8-flash",
        env=("GEMINI_API_KEY", "GOOGLE_API_KEY"), key_url="https://aistudio.google.com/apikey",
        thinking_style="gemini", max_context=1_000_000, max_output=65_536,
    ),
    ProviderSpec(
        "xai", "xAI Grok", "compat", "intl", "https://api.x.ai/v1", "grok-4.7", "grok-4.7",
        env=("XAI_API_KEY",), key_url="https://console.x.ai", max_context=256_000, max_output=64_000,
    ),
    ProviderSpec(
        "mistral", "Mistral", "compat", "intl", "https://api.mistral.ai/v1",
        "mistral-large-latest", "mistral-medium-latest",
        env=("MISTRAL_API_KEY",), key_url="https://console.mistral.ai/api-keys",
    ),
    ProviderSpec(
        "groq", "Groq", "compat", "intl", "https://api.groq.com/openai/v1",
        "openai/gpt-oss-120b", "openai/gpt-oss-120b",
        env=("GROQ_API_KEY",), key_url="https://console.groq.com/keys", thinking_style="reasoning_effort",
        max_output=32_768,
    ),
    ProviderSpec(
        "openrouter", "OpenRouter", "compat", "intl", "https://openrouter.ai/api/v1",
        "anthropic/claude-opus-5.5", "google/gemini-3.8-flash",
        env=("OPENROUTER_API_KEY",), key_url="https://openrouter.ai/keys",
    ),
    ProviderSpec(
        "deepseek", "DeepSeek", "compat", "cn", "https://api.deepseek.com", "deepseek-v4-pro", "deepseek-flash",
        env=("NOVEL_GM_DEEPSEEK_API_KEY", "DEEPSEEK_API_KEY"), key_url="https://platform.deepseek.com/api_keys",
        thinking_style="type_extra", prefix_style="deepseek_beta", max_context=1_000_000, max_output=384_000,
    ),
    ProviderSpec(
        "moonshot", "Kimi (Moonshot)", "compat", "cn", "https://api.moonshot.cn/v1", "kimi-k3", "kimi-k3",
        env=("MOONSHOT_API_KEY",), key_url="https://platform.moonshot.cn/console/api-keys",
        thinking_style="reasoning_effort", max_context=256_000, max_output=32_768,
    ),
    ProviderSpec(
        "qwen", "通义千问 (DashScope)", "compat", "cn", "https://dashscope.aliyuncs.com/compatible-mode/v1",
        "qwen3-max", "qwen3.7-plus",
        env=("DASHSCOPE_API_KEY",), key_url="https://bailian.console.aliyun.com/?apiKey=1",
        max_context=256_000, max_output=32_768,
    ),
    ProviderSpec(
        "zhipu", "智谱 GLM", "compat", "cn", "https://open.bigmodel.cn/api/paas/v4", "glm-5.1", "glm-5",
        env=("ZHIPU_API_KEY",), key_url="https://open.bigmodel.cn/usercenter/apikeys",
        thinking_style="type_extra", max_context=200_000, max_output=128_000,
    ),
    ProviderSpec(
        "doubao", "豆包 (火山方舟)", "compat", "cn", "https://ark.cn-beijing.volces.com/api/v3",
        "doubao-seed-2-1-pro-260628", "doubao-seed-2-1-turbo-260628",
        env=("ARK_API_KEY",), key_url="https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey",
        thinking_style="type_extra", max_context=256_000, max_output=32_768,
    ),
    ProviderSpec(
        "siliconflow", "硅基流动 SiliconFlow", "compat", "cn", "https://api.siliconflow.cn/v1",
        "deepseek-ai/DeepSeek-V4-Pro", "deepseek-ai/DeepSeek-V4-Flash",
        env=("SILICONFLOW_API_KEY",), key_url="https://cloud.siliconflow.cn/account/ak",
    ),
    ProviderSpec(
        "local", "本地模型", "local", "local", "http://127.0.0.1:8080/v1", "local", "local",
        needs_key=False, cloud=False,
    ),
    ProviderSpec("custom", "自定义接口", "custom", "local", "", "", "", needs_key=False),
    ProviderSpec(MOCK, "Mock", "mock", "local", needs_key=False, cloud=False, hidden=True),
)

SPECS: dict[str, ProviderSpec] = {s.id: s for s in CATALOG}
PRESET_IDS: tuple[str, ...] = tuple(s.id for s in CATALOG if not s.hidden)
_ID_RE = re.compile(r"^[a-z0-9][a-z0-9_-]{0,40}$")


def key_name(provider_id: str) -> str:
    return secret_store.DEEPSEEK if provider_id == "deepseek" else f"{provider_id}_api_key"


# 环境变量只对 id 与预设相同的那条配置生效
for _s in CATALOG:
    if _s.env:
        secret_store.ENV_VARS[key_name(_s.id)] = _s.env


def _bad_provider() -> HTTPException:
    return HTTPException(400, detail={"code": "BAD_PROVIDER", "message": "未知的生成方式"})


def get_spec(preset_id: str) -> ProviderSpec:
    spec = SPECS.get(preset_id)
    if spec is None:
        raise _bad_provider()
    return spec


# ---- 实例存储 ----

def _clean(data: Any) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    seen: set[str] = set()
    for item in data if isinstance(data, list) else []:
        if not isinstance(item, dict):
            continue
        iid, preset = str(item.get("id") or ""), str(item.get("preset") or "")
        spec = SPECS.get(preset)
        if iid in seen or iid in (MOCK, INHERIT) or not _ID_RE.match(iid) or spec is None or spec.hidden:
            continue
        seen.add(iid)
        out.append({**item, "name": str(item.get("name") or spec.name)})
    return out


def _legacy_configs() -> dict[str, dict[str, Any]]:
    with connect() as conn:
        raw = get_setting(conn, LEGACY_CONFIG_KEY, "{}")
    try:
        data = json.loads(raw or "{}")
    except ValueError:
        return {}
    return {k: v for k, v in data.items() if isinstance(v, dict)} if isinstance(data, dict) else {}


def _migrate() -> list[dict[str, Any]]:
    """Old versions kept one config per vendor; turn every vendor that was in use into an instance with the same id."""
    legacy = _legacy_configs()
    with connect() as conn:
        default = get_setting(conn, "provider", "")
        local_used = get_setting(conn, "offline_mode", "0") == "1" or any(
            get_setting(conn, k, "") for k in ("local_base_url", "local_model")
        )
    items = []
    for spec in CATALOG:
        if spec.hidden:
            continue
        used = spec.id in legacy or spec.id == default or (spec.kind == "local" and local_used)
        if not used and spec.kind != "local":
            used = bool(get_key(spec.id))
        if used:
            item: dict[str, Any] = {"id": spec.id, "preset": spec.id, "name": spec.name}
            item.update({k: v for k, v in legacy.get(spec.id, {}).items() if k in FIELDS})
            items.append(item)
    return items


def _write(items: list[dict[str, Any]]) -> None:
    with connect() as conn:
        set_setting(conn, INSTANCES_KEY, json.dumps(items, ensure_ascii=False))


def list_instances() -> list[dict[str, Any]]:
    with connect() as conn:
        raw = get_setting(conn, INSTANCES_KEY, "")
    if not raw:
        items = _migrate()
        _write(items)
        return items
    try:
        return _clean(json.loads(raw))
    except ValueError:
        return []


def get_instance(provider_id: str) -> Optional[dict[str, Any]]:
    return next((i for i in list_instances() if i["id"] == provider_id), None)


def _require(provider_id: str) -> dict[str, Any]:
    inst = get_instance(provider_id)
    if inst is None:
        raise _bad_provider()
    return inst


def is_known(provider_id: str) -> bool:
    return provider_id == MOCK or get_instance(provider_id) is not None


def instance_spec(provider_id: str) -> Optional[ProviderSpec]:
    if provider_id == MOCK:
        return SPECS[MOCK]
    inst = get_instance(provider_id)
    return SPECS[inst["preset"]] if inst else None


def first_local() -> str:
    return next((i["id"] for i in list_instances() if SPECS[i["preset"]].kind == "local"), "")


# ---- 用户配置 ----

def _effective(spec: ProviderSpec, user: dict[str, Any], legacy_local: bool = False) -> dict[str, Any]:
    """User values on top of preset defaults."""
    base_url = str(user.get("base_url") or "").strip().rstrip("/")
    strong = str(user.get("model_strong") or "").strip()
    fast = str(user.get("model_fast") or "").strip()
    if legacy_local:
        # id 为 local 的本地模型沿用旧设置项，/settings 和 /providers 改的是同一处
        with connect() as conn:
            base_url = (get_setting(conn, "local_base_url", "") or "").strip()
            fast = (get_setting(conn, "local_model", "") or "").strip()
        strong = strong or fast
    strong = strong or spec.model_strong
    fast = fast or spec.model_fast
    thinking = user.get("thinking")
    return {
        "base_url": base_url or spec.base_url,
        "model_strong": strong or fast,
        "model_fast": fast or strong,
        "thinking": bool(thinking) if isinstance(thinking, bool) else spec.thinking_style is not None,
        "price": valid_price(user.get("price")),
    }


def _legacy_local(provider_id: str, spec: ProviderSpec) -> bool:
    return provider_id == "local" and spec.kind == "local"


def provider_config(provider_id: str) -> dict[str, Any]:
    inst = _require(provider_id)
    spec = SPECS[inst["preset"]]
    return _effective(spec, inst, _legacy_local(provider_id, spec))


def _apply(inst: dict[str, Any], changes: dict[str, Any], items: list[dict[str, Any]]) -> None:
    """Merge changes into one instance. Empty strings reset a field to the preset default."""
    spec = SPECS[inst["preset"]]
    name = changes.get("name")
    if name is not None and str(name).strip():
        name = str(name).strip()[:MAX_NAME]
        if any(i is not inst and i["name"].casefold() == name.casefold() for i in items):
            raise HTTPException(400, detail={"code": "DUPLICATE_NAME", "message": f"已有同名的模型配置：{name}"})
        inst["name"] = name
    legacy = {"base_url": "local_base_url", "model_fast": "local_model"} if _legacy_local(inst["id"], spec) else {}
    for k in ("base_url", "model_strong", "model_fast"):
        if k not in changes or changes[k] is None:
            continue
        v = str(changes[k]).strip()
        if k == "base_url":
            v = v.rstrip("/")
        if k in legacy:
            with connect() as conn:
                set_setting(conn, legacy[k], v or getattr(spec, k))
            continue
        if v:
            inst[k] = v
        else:
            inst.pop(k, None)
    if isinstance(changes.get("thinking"), bool):
        inst["thinking"] = changes["thinking"]
    if "price" in changes:
        price = valid_price(changes["price"])
        if price:
            inst["price"] = price
        else:
            inst.pop("price", None)


def save_provider_config(provider_id: str, changes: dict[str, Any]) -> None:
    items = list_instances()
    inst = next((i for i in items if i["id"] == provider_id), None)
    if inst is None:
        raise _bad_provider()
    _apply(inst, changes, items)
    _write(items)


def _new_id(preset: str, items: list[dict[str, Any]]) -> str:
    used = {i["id"] for i in items}
    if preset not in used:
        return preset
    n = 2
    while f"{preset}-{n}" in used:
        n += 1
    return f"{preset}-{n}"


def _auto_name(base: str, items: list[dict[str, Any]]) -> str:
    used = {i["name"].casefold() for i in items}
    if base.casefold() not in used:
        return base
    n = 2
    while f"{base} {n}".casefold() in used:
        n += 1
    return f"{base} {n}"


def create_instance(preset: str, changes: dict[str, Any]) -> str:
    """Add a model config based on a preset and return its id."""
    spec = get_spec(preset)
    if spec.hidden:
        raise _bad_provider()
    items = list_instances()
    inst: dict[str, Any] = {"id": _new_id(preset, items), "preset": preset, "name": spec.name}
    name = str(changes.get("name") or "").strip()
    items.append(inst)
    _apply(inst, {**changes, "name": name or _auto_name(spec.name, items[:-1])}, items)
    _write(items)
    return inst["id"]


def delete_instance(provider_id: str) -> None:
    """Remove a config and its key; a deleted default falls back to the first remaining config."""
    items = list_instances()
    if not any(i["id"] == provider_id for i in items):
        raise _bad_provider()
    items = [i for i in items if i["id"] != provider_id]
    _write(items)
    try:
        set_key(provider_id, "")
    except secret_store.SecretStoreUnavailable:
        pass
    with connect() as conn:
        if get_setting(conn, "provider", "") == provider_id:
            set_setting(conn, "provider", items[0]["id"] if items else "")


# ---- 密钥 ----

def get_key(provider_id: str) -> str:
    return secret_store.get_secret(key_name(provider_id))


def set_key(provider_id: str, key: str) -> None:
    secret_store.set_secret(key, key_name(provider_id))


def _env_key(spec: ProviderSpec) -> str:
    return next((v for var in spec.env if (v := os.environ.get(var, "").strip())), "")


def mask_key(key: str) -> str:
    if not key:
        return ""
    if len(key) <= 4:
        return "****"
    return "…" + key[-4:]


# ---- 构造 ----

def build_from(
    spec: ProviderSpec, cfg: dict[str, Any], key: str, *, name: str, display: str, require_key: bool = True
) -> Any:
    """Instantiate the adapter for a preset with the given config and key."""
    if spec.kind == "mock":
        return MockProvider()
    if spec.needs_key and not key and require_key:
        raise HTTPException(400, detail={
            "code": "NO_API_KEY",
            "message": f"{display} 尚未配置 API 密钥，请在「设置 → 模型」中填写",
        })
    if spec.kind == "custom" and not cfg["base_url"]:
        raise HTTPException(400, detail={
            "code": "NO_BASE_URL",
            "message": f"{display} 尚未填写接口地址，请在「设置 → 模型」中填写",
        })
    price: Optional[Price] = cfg["price"]
    if spec.kind == "anthropic":
        return AnthropicProvider(key, name, base_url=cfg["base_url"], display=display, price=price)
    if spec.kind == "gemini":
        return GeminiProvider(key, name, base_url=cfg["base_url"], display=display, price=price)
    return OpenAICompatibleProvider(
        cfg["base_url"],
        key or ("local" if spec.kind == "local" else ""),
        name=name,
        display=display,
        thinking_style=spec.thinking_style,
        prefix_style=spec.prefix_style if cfg["base_url"] == spec.base_url else None,
        max_tokens_param=spec.max_tokens_param,
        price=price,
        free=spec.kind == "local",
        max_context=spec.max_context,
        max_output=spec.max_output,
    )


def build(provider_id: str, *, require_key: bool = True) -> Any:
    """Instantiate the adapter for a saved config."""
    if provider_id == MOCK:
        return MockProvider()
    inst = _require(provider_id)
    spec = SPECS[inst["preset"]]
    key = get_key(provider_id) if spec.kind != "local" else ""
    return build_from(spec, provider_config(provider_id), key, name=provider_id, display=inst["name"],
                      require_key=require_key)


def build_draft(draft: dict[str, Any]) -> tuple[Any, dict[str, Any], str]:
    """(adapter, config, display name) for unsaved editor values; nothing is written.

    A blank key falls back to the saved key when editing an existing config.
    """
    iid = str(draft.get("id") or "")
    inst = _require(iid) if iid else None
    spec = get_spec(inst["preset"] if inst else str(draft.get("preset") or ""))
    if spec.hidden:
        raise _bad_provider()
    cfg = _effective(spec, {k: draft.get(k) for k in ("base_url", "model_strong", "model_fast")})
    display = str(draft.get("name") or "").strip() or (inst["name"] if inst else spec.name)
    key = ""
    if spec.kind != "local":
        key = str(draft.get("api_key") or "").strip() or (get_key(iid) if inst else _env_key(spec))
    return build_from(spec, cfg, key, name=iid or spec.id, display=display), cfg, display


def _display(provider_id: str) -> str:
    inst = get_instance(provider_id)
    return inst["name"] if inst else provider_id


def pick_model(cfg: dict[str, Any], purpose: str, display: str) -> str:
    model = cfg["model_strong"] if purpose in STRONG_PURPOSES else cfg["model_fast"]
    if not model:
        raise HTTPException(400, detail={
            "code": "NO_MODEL",
            "message": f"{display} 尚未填写模型名，请在「设置 → 模型」中填写",
        })
    return model


def model_for(purpose: str, provider_hint: str) -> str:
    if provider_hint == MOCK:
        return MOCK
    return pick_model(provider_config(provider_hint), purpose, _display(provider_hint))


def thinking_for(provider_hint: str) -> Optional[bool]:
    """Whether plan / audit should think; None when the provider has no thinking switch."""
    spec = instance_spec(provider_hint)
    if spec is None or spec.thinking_style is None:
        return None
    return provider_config(provider_hint)["thinking"]


# ---- 全局默认与作品解析 ----

def get_global_provider() -> tuple[str, bool]:
    """Global default (provider, offline_mode) used by projects set to inherit."""
    with connect() as conn:
        provider = get_setting(conn, "provider", "")
        offline = get_setting(conn, "offline_mode", "0") == "1"
    if not is_known(provider):
        items = list_instances()
        provider = items[0]["id"] if items else ""
    return provider, offline


def _is_cloud(provider_id: str) -> bool:
    spec = instance_spec(provider_id)
    return spec is None or spec.cloud


def effective_provider(project_provider: str, project_offline: bool) -> tuple[str, bool]:
    """A project's own choice wins over the default; a deleted config counts as inherit."""
    provider, offline = get_global_provider()
    if project_provider and project_provider != INHERIT and is_known(project_provider):
        provider = project_provider
    offline = offline or project_offline
    if offline and _is_cloud(provider):
        provider = first_local()
    return provider, offline


def resolve_provider(provider_name: str, offline_mode: bool = False) -> tuple[Any, str]:
    """Return (provider, provider_id). Missing configs raise 400 instead of silently using mock."""
    pid = provider_name
    if offline_mode and _is_cloud(pid):
        pid = first_local()
        if not pid:
            raise HTTPException(400, detail={
                "code": "NO_LOCAL_MODEL",
                "message": "离线模式需要本地模型，请先在「设置 → 模型」中添加本地模型",
            })
    if not is_known(pid):
        raise HTTPException(400, detail={
            "code": "NO_PROVIDER",
            "message": "还没有添加模型，请先在「设置 → 模型」中添加",
        })
    return build(pid), pid


def resolve_for_project(project: Any) -> tuple[Any, str]:
    provider, offline = effective_provider(project.provider, project.offline_mode)
    return resolve_provider(provider, offline)


def default_provider() -> tuple[Any, str]:
    provider, offline = get_global_provider()
    return resolve_provider(provider, offline)


# ---- 视图 ----

def _list_price(spec: ProviderSpec, model: str) -> Optional[Price]:
    return FREE if spec.kind in ("local", "mock") else lookup_price(model)


def preset_view(spec: ProviderSpec) -> dict[str, Any]:
    price = _list_price(spec, spec.model_strong)
    return {
        "id": spec.id,
        "name": spec.name,
        "kind": spec.kind,
        "group": spec.group,
        "needs_key": spec.needs_key,
        "key_url": spec.key_url,
        "env_vars": list(spec.env),
        "base_url": spec.base_url,
        "base_url_required": spec.kind == "custom",
        "model_strong": spec.model_strong,
        "model_fast": spec.model_fast,
        "supports_thinking": spec.thinking_style is not None,
        "price": price,
        "price_known": price is not None,
    }


def list_presets() -> list[dict[str, Any]]:
    return [preset_view(s) for s in CATALOG if not s.hidden]


def provider_view(provider_id: str) -> dict[str, Any]:
    inst = _require(provider_id)
    spec = SPECS[inst["preset"]]
    cfg = _effective(spec, inst, _legacy_local(provider_id, spec))
    key = get_key(provider_id) if spec.needs_key or spec.kind == "custom" else ""
    price = cfg["price"] or _list_price(spec, cfg["model_strong"])
    has_key = bool(key)
    configured = (
        (has_key or not spec.needs_key)
        and bool(cfg["model_strong"])
        and (spec.kind != "custom" or bool(cfg["base_url"]))
    )
    return {
        "id": provider_id,
        "preset": spec.id,
        "preset_name": spec.name,
        "name": inst["name"],
        "kind": spec.kind,
        "group": spec.group,
        "cloud": spec.cloud,
        "needs_key": spec.needs_key,
        "has_key": has_key,
        "key_hint": mask_key(key),
        "key_url": spec.key_url,
        "env_vars": list(spec.env) if provider_id == spec.id else [],
        "base_url": cfg["base_url"],
        "default_base_url": spec.base_url,
        "model_strong": cfg["model_strong"],
        "model_fast": cfg["model_fast"],
        "default_model_strong": spec.model_strong,
        "default_model_fast": spec.model_fast,
        "supports_thinking": spec.thinking_style is not None,
        "thinking": cfg["thinking"],
        "custom_price": cfg["price"],
        "price": price,
        "price_known": price is not None,
        "configured": configured,
    }


def list_provider_views() -> list[dict[str, Any]]:
    return [provider_view(i["id"]) for i in list_instances()]
