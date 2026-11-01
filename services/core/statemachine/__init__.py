from __future__ import annotations

from typing import Optional

from fastapi import HTTPException

from ..models import Phase

# Allowed transitions
TRANSITIONS: dict[Phase, set[Phase]] = {
    Phase.SETUP: {Phase.PLAN},
    Phase.PLAN: {Phase.GENERATE},
    Phase.GENERATE: {Phase.EXTRACT},
    Phase.EXTRACT: {Phase.GATE},
    Phase.GATE: {Phase.REFINE},
    Phase.REFINE: {Phase.REPLAN},
    Phase.REPLAN: {Phase.PLAN},
}


def assert_phase(current: Phase, expected: Phase | set[Phase], action: str) -> None:
    allowed = {expected} if isinstance(expected, Phase) else expected
    if current not in allowed:
        raise HTTPException(
            status_code=409,
            detail={
                "code": "INVALID_PHASE",
                "message": f"Cannot {action} while in {current.value}",
                "current": current.value,
                "expected": [p.value for p in allowed],
            },
        )


def transition(current: Phase, target: Phase) -> Phase:
    if target not in TRANSITIONS.get(current, set()):
        raise HTTPException(
            status_code=409,
            detail={
                "code": "INVALID_TRANSITION",
                "message": f"Cannot transition {current.value} → {target.value}",
            },
        )
    return target


GATE_NOT_SATISFIED = HTTPException(
    status_code=409,
    detail={
        "code": "GATE_NOT_SATISFIED",
        "message": "你需要先读完当前章并提交细修，才能继续生成",
    },
)


def require_gate_satisfied(satisfied: bool, chapter_no: Optional[int] = None) -> None:
    if not satisfied:
        detail = dict(GATE_NOT_SATISFIED.detail)  # type: ignore[arg-type]
        if chapter_no is not None:
            detail["chapter_no"] = chapter_no
            detail["message"] = f"你需要先读完第 {chapter_no} 章并提交细修"
        raise HTTPException(status_code=409, detail=detail)
