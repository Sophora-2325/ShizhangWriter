"""Read-only, uncapped session totals. The desktop supplies its startup time in UTC."""
from datetime import datetime, timezone
from typing import Any

from .db import connect


def session_usage(since: datetime) -> dict[str, Any]:
    from .providers import instance_spec, lookup_price, provider_config

    start = since.replace(tzinfo=timezone.utc) if since.tzinfo is None else since.astimezone(timezone.utc)
    cutoff = start.isoformat()
    with connect() as conn:
        conn.execute("BEGIN")  # One read snapshot, even if a model finishes between the two queries.
        # Totals cover every matching call, independently of the recent-entry limit in /cost.
        groups = conn.execute(
            """SELECT provider, model, COUNT(*) AS calls,
               SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens,
               SUM(cached_tokens) AS cached_tokens, SUM(cost_usd) AS cost_usd
               FROM usage_log WHERE created_at >= ? GROUP BY provider, model""", (cutoff,)
        ).fetchall()
        last = conn.execute(
            "SELECT * FROM usage_log WHERE created_at >= ? ORDER BY id DESC LIMIT 1", (cutoff,)
        ).fetchone()

    def known(provider: str, model: str) -> bool:
        spec = instance_spec(provider)
        if provider in ("local", "mock") or (spec is not None and spec.kind in ("local", "mock")):
            return True
        if lookup_price(model) is not None:
            return True
        return spec is not None and provider_config(provider)["price"] is not None

    unknown = sum(g["calls"] for g in groups if not known(g["provider"], g["model"]))
    latest = None if last is None else {
        key: last[key] for key in ("provider", "model", "purpose", "cost_usd", "created_at")
    }
    if latest is not None:
        latest["price_known"] = known(last["provider"], last["model"])
    return {
        "input_tokens": sum(g["input_tokens"] for g in groups),
        "output_tokens": sum(g["output_tokens"] for g in groups),
        "cached_tokens": sum(g["cached_tokens"] for g in groups),
        "cost_usd": sum(g["cost_usd"] for g in groups),
        "calls": sum(g["calls"] for g in groups),
        "unknown_price_calls": unknown,
        "last": latest,
    }
