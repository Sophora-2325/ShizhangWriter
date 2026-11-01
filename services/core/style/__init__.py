from __future__ import annotations

import re
from collections import Counter
from typing import Any

from ..context import DEFAULT_BANNED
from ..models import StyleProfile, StyleReport


def _sentences(text: str) -> list[str]:
    parts = re.split(r"(?<=[。！？…])\s*", text.strip())
    return [p for p in parts if p]


def compute_metrics(text: str) -> dict[str, float]:
    sents = _sentences(text)
    lengths = [len(s) for s in sents] or [0]
    avg = sum(lengths) / len(lengths)
    var = sum((x - avg) ** 2 for x in lengths) / max(len(lengths), 1)
    connectors = [
        "然而", "与此同时", "值得一提的是", "渐渐地", "不知不觉",
        "不禁", "仿佛", "宛如", "似乎",
    ]
    conn_hits = sum(text.count(c) for c in connectors)
    per_k = conn_hits / max(len(text) / 1000, 0.001)
    dash = text.count("——") / max(len(text) / 1000, 0.001)
    ellipsis = text.count("……") / max(len(text) / 1000, 0.001)
    dialogue_chars = sum(len(m) for m in re.findall(r"「[^」]*」|“[^”]*”", text))
    dialogue_ratio = dialogue_chars / max(len(text), 1)
    tokens = list(text)
    ttr = len(set(tokens)) / max(len(tokens), 1)
    return {
        "avg_sentence_len": round(avg, 2),
        "sentence_len_variance": round(var, 2),
        "connector_density": round(per_k, 3),
        "dash_density": round(dash, 3),
        "ellipsis_density": round(ellipsis, 3),
        "dialogue_ratio": round(dialogue_ratio, 3),
        "type_token_ratio": round(ttr, 4),
    }


def find_banned(text: str, banned: list[str] | None = None) -> list[dict[str, Any]]:
    words = banned or DEFAULT_BANNED
    hits = []
    for w in words:
        start = 0
        while True:
            i = text.find(w, start)
            if i < 0:
                break
            hits.append({"phrase": w, "position": i})
            start = i + len(w)
    return hits


def style_check(text: str, profile: StyleProfile | None = None) -> StyleReport:
    banned = (profile.banned_words if profile and profile.banned_words else DEFAULT_BANNED)
    hits = find_banned(text, banned)
    metrics = compute_metrics(text)
    baseline = (profile.metrics if profile and profile.metrics else {}) or {}
    drift: dict[str, float] = {}
    for k, v in metrics.items():
        if k in baseline and baseline[k]:
            drift[k] = round(v - baseline[k], 3)

    findings = []
    # pattern: 不是…而是…
    for m in re.finditer(r"不是.+?而是", text):
        findings.append({"pattern": "不是…而是…", "severity": "warn", "evidence": m.group()[:30]})
    # tone adverbs after dialogue
    for m in re.finditer(r"[」”].{0,2}(淡淡地|轻声|缓缓地)(说|道)", text):
        findings.append(
            {"pattern": "对话语气副词", "severity": "info", "evidence": m.group()[:20]}
        )

    verdict = "pass"
    if len(hits) >= 8:
        verdict = "fail"
    elif len(hits) >= 3 or metrics.get("connector_density", 0) > 4:
        verdict = "marginal"
    if baseline.get("sentence_len_variance") and metrics["sentence_len_variance"] < baseline[
        "sentence_len_variance"
    ] * 0.5:
        drift["sentence_len_variance"] = metrics["sentence_len_variance"] - baseline[
            "sentence_len_variance"
        ]
        if verdict == "pass":
            verdict = "marginal"

    suggestions = []
    if hits:
        top = Counter(h["phrase"] for h in hits).most_common(5)
        suggestions.append("删除或改写：" + "、".join(w for w, _ in top))
    if verdict == "fail":
        suggestions.append("建议以开篇范文为锚、用前缀补全重写本章一次（不递归）")

    return StyleReport(
        banned_phrase_hits=hits,
        ai_tic_findings=findings,
        metrics_drift=drift,
        verdict=verdict,
        rewrite_suggestions=suggestions,
    )


def update_baseline_from_chapter(profile: StyleProfile, text: str) -> StyleProfile:
    if not profile.metrics:
        profile.metrics = compute_metrics(text)
    return profile
