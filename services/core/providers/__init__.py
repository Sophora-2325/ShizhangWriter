from __future__ import annotations

from .. import secret_store
from ..db import connect, get_setting
from .anthropic_provider import AnthropicProvider
from .base import (
    PRICE_TABLE,
    ChatMessage,
    ChatRequest,
    ChatResponse,
    LLMProvider,
    ModelCapabilities,
    estimate_cost,
    lookup_price,
)
from .gemini_provider import GeminiProvider
from .mock import MockProvider
from .openai_compat import OpenAICompatibleProvider
from .registry import (
    CATALOG,
    INHERIT,
    MOCK,
    PRESET_IDS,
    SPECS,
    build,
    build_draft,
    create_instance,
    default_provider,
    delete_instance,
    effective_provider,
    first_local,
    get_global_provider,
    get_instance,
    get_key,
    get_spec,
    instance_spec,
    is_known,
    key_name,
    list_instances,
    list_presets,
    list_provider_views,
    mask_key,
    model_for,
    pick_model,
    provider_config,
    provider_view,
    resolve_for_project,
    resolve_provider,
    save_provider_config,
    set_key,
    thinking_for,
)

__all__ = [
    "CATALOG",
    "INHERIT",
    "MOCK",
    "PRESET_IDS",
    "PRICE_TABLE",
    "SPECS",
    "AnthropicProvider",
    "ChatMessage",
    "ChatRequest",
    "ChatResponse",
    "GeminiProvider",
    "LLMProvider",
    "MockProvider",
    "ModelCapabilities",
    "OpenAICompatibleProvider",
    "api_key_hint",
    "build",
    "build_draft",
    "create_instance",
    "default_provider",
    "delete_instance",
    "effective_provider",
    "estimate_cost",
    "first_local",
    "get_api_key",
    "get_global_provider",
    "get_instance",
    "get_key",
    "get_local_config",
    "get_spec",
    "instance_spec",
    "is_known",
    "key_name",
    "list_instances",
    "list_presets",
    "list_provider_views",
    "lookup_price",
    "mask_key",
    "model_for",
    "pick_model",
    "provider_config",
    "provider_view",
    "resolve_for_project",
    "resolve_provider",
    "save_provider_config",
    "set_api_key",
    "set_key",
    "thinking_for",
]


def get_api_key() -> str:
    return secret_store.get_secret(secret_store.DEEPSEEK)


def set_api_key(key: str) -> None:
    secret_store.set_secret(key, secret_store.DEEPSEEK)


def api_key_hint(key: str | None = None) -> str:
    return mask_key(key if key is not None else get_api_key())


def get_local_config() -> tuple[str, str]:
    with connect() as conn:
        base = get_setting(conn, "local_base_url", "http://127.0.0.1:8080/v1")
        model = get_setting(conn, "local_model", "local")
    return base, model
