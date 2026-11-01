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
    usage_int,
)

_DROPPABLE = ("temperature", "top_p", "thinking_config", "response_mime_type")


class GeminiProvider:
    """Google Gemini via google-genai: system_instruction, JSON mime type, thinking level."""

    def __init__(
        self,
        api_key: str,
        name: str = "gemini",
        *,
        base_url: str = "",
        display: str = "Gemini",
        price: Optional[Price] = None,
        timeout: float = 600.0,
    ):
        from google import genai
        from google.genai import types

        self.name = name
        self.display = display
        self.price = price
        self._types = types
        http = {"timeout": int(timeout * 1000)}
        if base_url:
            http["base_url"] = base_url.rstrip("/")
        self.client = genai.Client(api_key=api_key, http_options=types.HttpOptions(**http))

    def capabilities(self, model: str) -> ModelCapabilities:
        return {
            "structured_output": "json_object",
            "thinking": True,
            "streaming": True,
            "prefix_completion": False,
            "vision": True,
            "max_context": 1_000_000,
            "max_output": 65_536,
        }

    def price_for(self, model: str) -> Optional[Price]:
        return self.price or lookup_price(model)

    def _build(self, req: ChatRequest) -> tuple[list[Any], dict[str, Any]]:
        t = self._types
        msgs = inline_prefix(list(req["messages"]))
        system = "\n\n".join(m.get("content", "") for m in msgs if m.get("role") == "system")
        contents = [
            t.Content(role="model" if m["role"] == "assistant" else "user", parts=[t.Part(text=m.get("content", ""))])
            for m in msgs
            if m.get("role") != "system"
        ]
        cfg: dict[str, Any] = {
            "temperature": req.get("temperature", 0.85),
            "top_p": req.get("top_p", 0.9),
            "max_output_tokens": int(req.get("max_tokens", 8192)),
        }
        if system:
            cfg["system_instruction"] = system
        if req.get("json_mode"):
            cfg["response_mime_type"] = "application/json"
        thinking = req.get("thinking")
        if thinking is not None:
            cfg["thinking_config"] = t.ThinkingConfig(thinking_level="HIGH" if thinking else "LOW")
        return contents, cfg

    def _retry(self, fn: Any, req: ChatRequest) -> Any:
        contents, cfg = self._build(req)
        for _ in range(5):
            try:
                return fn(contents, self._types.GenerateContentConfig(**cfg))
            except Exception as e:
                msg = str(e)
                code = getattr(e, "code", None) or getattr(e, "status_code", None)
                dropped = False
                if code == 400:
                    for name in _DROPPABLE:
                        hit = name in msg or (name == "thinking_config" and "thinking" in msg.lower())
                        if hit and name in cfg:
                            cfg.pop(name)
                            dropped = True
                if not dropped:
                    raise call_failed(self.display, e) from e
        raise call_failed(self.display, RuntimeError("too many retries"))

    def chat(self, req: ChatRequest) -> ChatResponse:
        t0 = time.perf_counter()
        model = req["model"]
        resp = self._retry(
            lambda c, cfg: self.client.models.generate_content(model=model, contents=c, config=cfg), req
        )
        try:
            content = resp.text or ""
        except (ValueError, AttributeError):
            content = ""
        usage = getattr(resp, "usage_metadata", None)
        input_tokens = usage_int(usage, "prompt_token_count")
        output_tokens = usage_int(usage, "candidates_token_count") + usage_int(usage, "thoughts_token_count")
        cached_tokens = usage_int(usage, "cached_content_token_count")
        price = self.price_for(model)
        return {
            "content": content,
            "model": model,
            "input_tokens": input_tokens,
            "output_tokens": output_tokens,
            "cached_tokens": cached_tokens,
            "latency_ms": (time.perf_counter() - t0) * 1000,
            "cost_usd": estimate_cost(model, input_tokens, output_tokens, cached_tokens, price or {}),
            "price_known": price is not None,
            "raw": resp,
        }

    def stream(self, req: ChatRequest) -> Iterator[str]:
        model = req["model"]

        def start(c: Any, cfg: Any) -> tuple[Any, Iterator[Any]]:
            # 生成器在取第一块时才真正发请求，先取一块好让参数被拒时能重试
            g = iter(self.client.models.generate_content_stream(model=model, contents=c, config=cfg))
            return next(g, None), g

        first, rest = self._retry(start, req)
        if first is not None and getattr(first, "text", None):
            yield first.text
        try:
            for chunk in rest:
                text = getattr(chunk, "text", None)
                if text:
                    yield text
        except Exception as e:
            raise call_failed(self.display, e) from e

    def list_models(self) -> list[str]:
        try:
            out = []
            for m in self.client.models.list():
                actions = getattr(m, "supported_actions", None) or []
                if actions and "generateContent" not in actions:
                    continue
                out.append((m.name or "").removeprefix("models/"))
            return sorted(x for x in out if x)
        except Exception as e:
            raise call_failed(self.display, e) from e
