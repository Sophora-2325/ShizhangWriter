from __future__ import annotations

import time
from decimal import Decimal
from typing import Any, Iterator, Optional

from openai import OpenAI

from .base import (
    ChatRequest,
    ChatResponse,
    ModelCapabilities,
    Price,
    call_failed,
    drop_rejected_params,
    ensure_json_hint,
    estimate_cost,
    inline_prefix,
    lookup_price,
    strip_prefix_flag,
    usage_int,
)

_RETRYABLE_PARAMS = ("temperature", "top_p", "reasoning_effort", "response_format")


class OpenAICompatibleProvider:
    """OpenAI Chat Completions, used for OpenAI itself and every compatible endpoint.

    thinking_style: "type_extra" sends extra_body.thinking.type (DeepSeek / 智谱 / 豆包);
    "reasoning_effort" sends the top-level reasoning_effort (OpenAI / Kimi / Grok).
    prefix_style "deepseek_beta" sends assistant prefix completion to the /beta endpoint.
    """

    def __init__(
        self,
        base_url: str,
        api_key: str,
        name: str = "openai_compatible",
        *,
        display: str = "",
        thinking_style: Optional[str] = None,
        prefix_style: Optional[str] = None,
        max_tokens_param: str = "max_tokens",
        price: Optional[Price] = None,
        free: bool = False,
        max_context: int = 128_000,
        max_output: int = 8_192,
        timeout: float = 600.0,
    ):
        self.name = name
        self.display = display or name
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.thinking_style = thinking_style
        self.prefix_style = prefix_style
        self.max_tokens_param = max_tokens_param
        self.price = price
        self.free = free
        self.max_context = max_context
        self.max_output = max_output
        self.timeout = timeout
        self.client = OpenAI(api_key=api_key or "no-key", base_url=self.base_url, timeout=timeout)
        self._beta: Optional[OpenAI] = None

    def capabilities(self, model: str) -> ModelCapabilities:
        return {
            "structured_output": "json_object",
            "thinking": self.thinking_style is not None,
            "streaming": True,
            "prefix_completion": self.prefix_style is not None,
            "vision": False,
            "max_context": self.max_context,
            "max_output": self.max_output,
        }

    def price_for(self, model: str) -> Optional[Price]:
        if self.free:
            return {"input_miss": 0.0, "input_cache_hit": 0.0, "output": 0.0}
        return self.price or lookup_price(model)

    def estimate_cost(
        self, model: str, input_tokens: int, output_tokens: int, cached_tokens: int = 0
    ) -> Decimal:
        price = self.price_for(model)
        return Decimal(str(estimate_cost(model, input_tokens, output_tokens, cached_tokens, price or {})))

    def _beta_client(self) -> OpenAI:
        if self._beta is None:
            self._beta = OpenAI(api_key=self.api_key or "no-key", base_url=self.base_url + "/beta", timeout=self.timeout)
        return self._beta

    def _kwargs(self, req: ChatRequest) -> tuple[dict[str, Any], OpenAI]:
        msgs = list(req["messages"])
        client = self.client
        if msgs and msgs[-1].get("prefix") and self.prefix_style == "deepseek_beta":
            messages = strip_prefix_flag(msgs[:-1]) + [dict(msgs[-1])]
            client = self._beta_client()
        else:
            messages = inline_prefix(msgs)
        if req.get("json_mode"):
            messages = ensure_json_hint(messages)
        kw: dict[str, Any] = {
            "model": req["model"],
            "messages": messages,
            "temperature": req.get("temperature", 0.85),
            "top_p": req.get("top_p", 0.9),
            self.max_tokens_param: req.get("max_tokens", 8192),
        }
        if req.get("json_mode"):
            kw["response_format"] = {"type": "json_object"}
        thinking = req.get("thinking")
        if thinking is not None:
            if self.thinking_style == "type_extra":
                kw["extra_body"] = {"thinking": {"type": "enabled" if thinking else "disabled"}}
            elif self.thinking_style == "reasoning_effort":
                kw["reasoning_effort"] = "high" if thinking else "low"
        return kw, client

    def _create(self, client: OpenAI, kw: dict[str, Any]) -> Any:
        for _ in range(5):
            try:
                return client.chat.completions.create(**kw)
            except Exception as e:
                msg = str(e)
                if "max_tokens" in msg and "max_completion_tokens" in msg and "max_tokens" in kw:
                    kw["max_completion_tokens"] = kw.pop("max_tokens")
                    continue
                if "extra_body" in kw and ("thinking" in msg or self.free):
                    kw.pop("extra_body")
                    continue
                if drop_rejected_params(kw, e, _RETRYABLE_PARAMS):
                    continue
                raise call_failed(self.display, e) from e
        raise call_failed(self.display, RuntimeError("too many retries"))

    def chat(self, req: ChatRequest) -> ChatResponse:
        t0 = time.perf_counter()
        kw, client = self._kwargs(req)
        resp = self._create(client, kw)
        content = resp.choices[0].message.content or ""
        usage = getattr(resp, "usage", None)
        input_tokens = usage_int(usage, "prompt_tokens")
        output_tokens = usage_int(usage, "completion_tokens")
        cached_tokens = usage_int(getattr(usage, "prompt_tokens_details", None), "cached_tokens")
        if not cached_tokens:
            cached_tokens = usage_int(usage, "prompt_cache_hit_tokens")
        price = self.price_for(req["model"])
        return {
            "content": content,
            "model": req["model"],
            "input_tokens": input_tokens,
            "output_tokens": output_tokens,
            "cached_tokens": cached_tokens,
            "latency_ms": (time.perf_counter() - t0) * 1000,
            "cost_usd": estimate_cost(req["model"], input_tokens, output_tokens, cached_tokens, price or {}),
            "price_known": price is not None,
            "raw": resp,
        }

    def stream(self, req: ChatRequest) -> Iterator[str]:
        kw, client = self._kwargs(req)
        kw["stream"] = True
        for chunk in self._create(client, kw):
            if not chunk.choices:
                continue
            delta = chunk.choices[0].delta
            if delta and delta.content:
                yield delta.content

    def list_models(self) -> list[str]:
        try:
            return sorted(m.id for m in self.client.models.list())
        except Exception as e:
            raise call_failed(self.display, e) from e
