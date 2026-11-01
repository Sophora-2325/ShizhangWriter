from __future__ import annotations

import json
import re
from typing import Any, TypeVar

from pydantic import BaseModel, ValidationError

try:
    from json_repair import repair_json
except ImportError:

    def repair_json(s: str) -> str:  # type: ignore
        return s


T = TypeVar("T", bound=BaseModel)


def extract_json_object(text: str) -> str:
    text = text.strip()
    if text.startswith("```"):
        text = re.sub(r"^```(?:json)?\s*", "", text)
        text = re.sub(r"\s*```$", "", text)
    start = text.find("{")
    end = text.rfind("}")
    if start >= 0 and end > start:
        return text[start : end + 1]
    return text


def loads_lenient(text: str) -> Any:
    raw = extract_json_object(text)
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        repaired = repair_json(raw)
        return json.loads(repaired) if isinstance(repaired, str) else repaired


def parse_model(text: str, model: type[T], retry_hint: str | None = None) -> T:
    data = loads_lenient(text)
    try:
        return model.model_validate(data)
    except ValidationError:
        if retry_hint:
            raise
        raise


def safe_parse(text: str, model: type[T]) -> tuple[T | None, str | None]:
    try:
        return parse_model(text, model), None
    except Exception as e:
        return None, str(e)
