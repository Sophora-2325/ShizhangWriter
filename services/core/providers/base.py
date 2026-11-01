from __future__ import annotations

from typing import Any, Iterator, Literal, Optional, Protocol, TypedDict

from fastapi import HTTPException


class ModelCapabilities(TypedDict):
    structured_output: Literal["json_schema", "json_object", "grammar", "none"]
    thinking: bool
    streaming: bool
    prefix_completion: bool
    vision: bool
    max_context: int
    max_output: int


class ChatMessage(TypedDict, total=False):
    role: str
    content: str
    prefix: bool


class ChatRequest(TypedDict, total=False):
    model: str
    messages: list[ChatMessage]
    temperature: float
    top_p: float
    max_tokens: int
    json_mode: bool
    thinking: bool
    stream: bool
    purpose: str


class ChatResponse(TypedDict, total=False):
    content: str
    model: str
    input_tokens: int
    output_tokens: int
    cached_tokens: int
    latency_ms: float
    cost_usd: float
    price_known: bool
    raw: Any


class LLMProvider(Protocol):
    name: str

    def capabilities(self, model: str) -> ModelCapabilities: ...
    def chat(self, req: ChatRequest) -> ChatResponse: ...
    def stream(self, req: ChatRequest) -> Iterator[str]: ...
    def list_models(self) -> list[str]: ...


Price = dict[str, float]

# USD / 1M tokens。缓存写入的额外费用不单列，按未命中计。
PRICE_TABLE: dict[str, Price] = {
    "deepseek-flash": {"input_miss": 0.15, "input_cache_hit": 0.003, "output": 0.6},
    "deepseek-v4-pro": {"input_miss": 0.66, "input_cache_hit": 0.022, "output": 1.98},
    "gpt-6-astra": {"input_miss": 10.0, "input_cache_hit": 1.0, "output": 50.0},
    "gpt-6.1-sol": {"input_miss": 2.0, "input_cache_hit": 0.1, "output": 10.0},
    "gpt-6-sol": {"input_miss": 2.0, "input_cache_hit": 0.2, "output": 10.0},
    "gpt-6-luna": {"input_miss": 0.1, "input_cache_hit": 0.01, "output": 0.5},
    "claude-fable-5-1": {"input_miss": 10.0, "input_cache_hit": 0.25, "output": 50.0},
    "claude-opus-5-5": {"input_miss": 4.0, "input_cache_hit": 0.2, "output": 20.0},
    "claude-sonnet-5-5": {"input_miss": 2.0, "input_cache_hit": 0.2, "output": 10.0},
    "claude-haiku-4-5": {"input_miss": 1.0, "input_cache_hit": 0.1, "output": 5.0},
    "gemini-3.1-pro": {"input_miss": 2.0, "input_cache_hit": 0.2, "output": 12.0},
    "gemini-3.8-flash": {"input_miss": 0.75, "input_cache_hit": 0.075, "output": 3.75},
    "grok-4.7": {"input_miss": 2.0, "input_cache_hit": 2.0, "output": 6.0},
    "openai/gpt-oss-120b": {"input_miss": 0.15, "input_cache_hit": 0.15, "output": 0.6},
    "local": {"input_miss": 0.0, "input_cache_hit": 0.0, "output": 0.0},
    "mock": {"input_miss": 0.0, "input_cache_hit": 0.0, "output": 0.0},
}

FREE: Price = {"input_miss": 0.0, "input_cache_hit": 0.0, "output": 0.0}


def lookup_price(model: str) -> Optional[Price]:
    """Exact match first, then the longest table key the model id starts with (dated snapshots)."""
    if model in PRICE_TABLE:
        return PRICE_TABLE[model]
    best = max((k for k in PRICE_TABLE if model.startswith(k)), key=len, default=None)
    return PRICE_TABLE[best] if best else None


def valid_price(p: Any) -> Optional[Price]:
    if not isinstance(p, dict):
        return None
    try:
        out = {k: float(p[k]) for k in ("input_miss", "input_cache_hit", "output")}
    except (KeyError, TypeError, ValueError):
        return None
    return out if all(v >= 0 for v in out.values()) else None


def estimate_cost(
    model: str,
    input_tokens: int,
    output_tokens: int,
    cached_tokens: int = 0,
    price: Optional[Price] = None,
) -> float:
    prices = price if price is not None else lookup_price(model)
    if not prices:
        return 0.0
    miss = max(0, input_tokens - cached_tokens)
    cost = (
        cached_tokens * prices["input_cache_hit"]
        + miss * prices["input_miss"]
        + output_tokens * prices["output"]
    ) / 1_000_000
    return round(cost, 6)


CONTINUE_INSTRUCTION = (
    "下面是本章已经写好的开头。请从它最后一个字之后无缝续写，"
    "保持人称、时态和文风一致；只输出续写的部分，不要重复已有内容。\n\n"
    "【已写开头】\n{prefix}"
)


def strip_prefix_flag(messages: list[ChatMessage]) -> list[dict[str, Any]]:
    return [{k: v for k, v in m.items() if k != "prefix"} for m in messages]


def inline_prefix(messages: list[ChatMessage]) -> list[dict[str, Any]]:
    """Fold a trailing assistant prefix into the user turn for APIs without prefix completion."""
    if not messages or not messages[-1].get("prefix"):
        return strip_prefix_flag(messages)
    instruction = CONTINUE_INSTRUCTION.format(prefix=messages[-1].get("content", ""))
    out = strip_prefix_flag(messages[:-1])
    if out and out[-1].get("role") == "user":
        out[-1] = {**out[-1], "content": f"{out[-1].get('content', '')}\n\n{instruction}"}
    else:
        out.append({"role": "user", "content": instruction})
    return out


def ensure_json_hint(messages: list[dict[str, Any]]) -> list[dict[str, Any]]:
    msgs = list(messages)
    if msgs and "json" not in str(msgs[-1].get("content", "")).lower():
        msgs[-1] = {**msgs[-1], "content": f"{msgs[-1].get('content', '')}\n请以 JSON 输出。"}
    return msgs


_REJECT_WORDS = ("unsupported", "not support", "invalid", "unrecognized", "unknown", "not allowed", "deprecated")


def drop_rejected_params(kwargs: dict[str, Any], err: Exception, names: tuple[str, ...]) -> bool:
    """When the API names a parameter it refuses, remove it so the call can be retried."""
    msg = str(err)
    low = msg.lower()
    if not any(w in low for w in _REJECT_WORDS):
        return False
    dropped = False
    for name in names:
        if name in msg and name in kwargs:
            kwargs.pop(name)
            dropped = True
    return dropped


def call_failed(display: str, err: Exception) -> HTTPException:
    if isinstance(err, HTTPException):
        return err
    status = getattr(err, "status_code", None) or getattr(err, "code", None)
    if status in (401, 403):
        return HTTPException(400, detail={"code": "BAD_API_KEY", "message": f"{display} 拒绝了 API 密钥，请检查后重试"})
    raw = str(err).strip()
    text = raw.splitlines()[0][:300] if raw else type(err).__name__
    if not isinstance(status, int) and any(w in type(err).__name__ for w in ("Connect", "Timeout")):
        return HTTPException(502, detail={"code": "PROVIDER_UNREACHABLE", "message": f"无法连接 {display}：{text}"})
    return HTTPException(502, detail={"code": "LLM_ERROR", "message": f"{display} 调用失败：{text}"})


def usage_int(obj: Any, *names: str) -> int:
    for n in names:
        v = getattr(obj, n, None) if obj is not None else None
        if isinstance(v, int):
            return v
    return 0
