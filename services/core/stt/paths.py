from __future__ import annotations

import os
from pathlib import Path

from .. import db

MODEL_DIR_NAME = "sense-voice-int8-2024-07-17"


def _is_model_dir(path: Path) -> bool:
    return (path / "model.int8.onnx").is_file() and (path / "tokens.txt").is_file()


def _candidates() -> list[Path]:
    roots: list[Path] = []
    env = os.environ.get("NOVEL_GM_STT_MODEL", "").strip()
    if env:
        roots.append(Path(env))

    local = os.environ.get("LOCALAPPDATA") or os.environ.get("HOME") or ""
    if local:
        roots.append(Path(local) / "novel-gm" / "stt" / MODEL_DIR_NAME)

    root = db.get_root()
    # 开发机：仓库旁 TTSM（novel-gm 的上一级）
    roots.append(root.parent / "TTSM" / "models" / MODEL_DIR_NAME)
    roots.append(root / "stt" / MODEL_DIR_NAME)
    return roots


def resolve_model_dir() -> Path | None:
    """返回含 model.int8.onnx 与 tokens.txt 的目录；找不到则 None。"""
    for path in _candidates():
        try:
            resolved = path.resolve()
        except OSError:
            continue
        if _is_model_dir(resolved):
            return resolved
    return None
