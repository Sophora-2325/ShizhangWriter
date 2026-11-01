from __future__ import annotations

import time
from typing import Any, Iterator, Optional

from .base import (
    ChatRequest,
    ChatResponse,
    ModelCapabilities,
    Price,
    call_failed,
    estimate_cost,
    inline_prefix,
    lookup_price,
    strip_prefix_flag,
    usage_int,
)

JSON_INSTRUCTION = "只输出一个 JSON 对象，不要代码块，也不要任何其他文字。"
THINKING_BUDGET = 4096


class AnthropicProvider:
    """Claude Messages API: separate system prompt (cached), adaptive thinking, assistant prefill."""

    def __init__(
        self,
        api_key: str,
        name: str = "anthropic",
        *,
        base_url: str = "",
        display: str = "Claude",
        price: Optional[Price] = None,
        timeout: float = 600.0,
    ):
        import anthropic

        self.name = name
        self.display = display
        self.price = price
        kwargs: dict[str, Any] = {"api_key": api_key, "timeout": timeout}
        if base_url:
            kwargs["base_url"] = base_url.rstrip("/")
        self.client = anthropic.Anthropic(**kwargs)

    def capabilities(self, model: str) -> ModelCapabilities:
        return {
            "structured_output": "none",
            "thinking": True,
            "streaming": True,
            "prefix_completion": True,
            "vision": True,
            "max_context": 200_000,
            "max_output": 64_000,
        }

    def price_for(self, model: str) -> Optional[Price]:
        return self.price or lookup_price(model)

    def _kwargs(self, req: ChatRequest, prefill: bool = True) -> dict[str, Any]:
        msgs = list(req["messages"])
        system = "\n\n".join(m.get("content", "") for m in msgs if m.get("role") == "system")
        convo_src = [m for m in msgs if m.get("role") != "system"]
        has_prefix = bool(convo_src and convo_src[-1].get("prefix"))
        if has_prefix and prefill:
            convo = strip_prefix_flag(convo_src[:-1])
            # API 不接受以空白结尾的预填内容
            convo.append({"role": "assistant", "content": convo_src[-1].get("content", "").rstrip()})
        else:
            convo = inline_prefix(convo_src)
        if req.get("json_mode") and convo:
            last = next((i for i in range(len(convo) - 1, -1, -1) if convo[i]["role"] == "user"), None)
            if last is not None:
                convo[last] = {**convo[last], "content": f"{convo[last]['content']}\n\n{JSON_INSTRUCTION}"}
        if not convo or convo[0]["role"] != "user":
            convo.insert(0, {"role": "user", "content": "请开始。"})
        max_tokens = int(req.get("max_tokens", 8192))
        kw: dict[str, Any] = {"model": req["model"], "messages": convo, "max_tokens": max_tokens}
        if system:
            kw["system"] = [{"type": "text", "text": system, "cache_control": {"type": "ephemeral"}}]
        if req.get("thinking"):
            kw["thinking"] = {"type": "adaptive"}
            kw["output_config"] = {"effort": "high"}
        elif req.get("thinking") is False:
            kw["output_config"] = {"effort": "low"}
        return kw

    def _adjust(self, req: ChatRequest, kw: dict[str, Any], err: Exception) -> Optional[dict[str, Any]]:
        """Return revised kwargs for a retry after a 400, or None when the error isn't recoverable."""
        if getattr(err, "status_code", None) not in (400, 422):
            return None
        msg = str(err).lower()
        if ("prefill" in msg or ("assistant" in msg and "final" in msg)) and kw["messages"][-1]["role"] == "assistant":
            new = self._kwargs(req, prefill=False)
            for k in ("thinking", "output_config", "max_tokens"):
                if k in kw:
                    new[k] = kw[k]
                else:
                    new.pop(k, None)
            return new
        if "adaptive" in msg and kw.get("thinking", {}).get("type") == "adaptive":
            return {
                **kw,
                "thinking": {"type": "enabled", "budget_tokens": THINKING_BUDGET},
                "max_tokens": max(kw["max_tokens"], THINKING_BUDGET + 4096),
            }
        if "thinking" in msg and "thinking" in kw:
            return {k: v for k, v in kw.items() if k != "thinking"}
        if ("effort" in msg or "output_config" in msg) and "output_config" in kw:
            return {k: v for k, v in kw.items() if k != "output_config"}
        return None

    def _create(self, req: ChatRequest) -> Any:
        kw = self._kwargs(req)
        for _ in range(5):
            try:
                return self.client.messages.create(**kw)
            except Exception as e:
                new = self._adjust(req, kw, e)
                if new is None:
                    raise call_failed(self.display, e) from e
                kw = new
        raise call_failed(self.display, RuntimeError("too many retries"))

    def chat(self, req: ChatRequest) -> ChatResponse:
        t0 = time.perf_counter()
        resp = self._create(req)
        content = "".join(getattr(b, "text", "") for b in resp.content if getattr(b, "type", "") == "text")
        usage = getattr(resp, "usage", None)
        cache_read = usage_int(usage, "cache_read_input_tokens")
        input_tokens = (
            usage_int(usage, "input_tokens") + cache_read + usage_int(usage, "cache_creation_input_tokens")
        )
        output_tokens = usage_int(usage, "output_tokens")
        price = self.price_for(req["model"])
        return {
            "content": content,
            "model": req["model"],
            "input_tokens": input_tokens,
            "output_tokens": output_tokens,
            "cached_tokens": cache_read,
            "latency_ms": (time.perf_counter() - t0) * 1000,
            "cost_usd": estimate_cost(req["model"], input_tokens, output_tokens, cache_read, price or {}),
            "price_known": price is not None,
            "raw": resp,
        }

    def stream(self, req: ChatRequest) -> Iterator[str]:
        kw = self._kwargs(req)
        for _ in range(5):
            started = False
            try:
                with self.client.messages.stream(**kw) as s:
                    for text in s.text_stream:
                        started = True
                        yield text
                return
            except Exception as e:
                new = None if started else self._adjust(req, kw, e)
                if new is None:
                    raise call_failed(self.display, e) from e
                kw = new
        raise call_failed(self.display, RuntimeError("too many retries"))

    def list_models(self) -> list[str]:
        try:
            return sorted(m.id for m in self.client.models.list(limit=100))
        except Exception as e:
            raise call_failed(self.display, e) from e
