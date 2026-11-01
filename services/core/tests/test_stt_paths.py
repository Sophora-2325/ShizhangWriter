from __future__ import annotations

import os
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from services.core.stt.paths import MODEL_DIR_NAME, resolve_model_dir


def _make_model_dir(base: Path) -> Path:
    d = base / MODEL_DIR_NAME
    d.mkdir(parents=True)
    (d / "model.int8.onnx").write_bytes(b"fake")
    (d / "tokens.txt").write_text("a\n", encoding="utf-8")
    return d


def test_resolve_via_env(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    model = _make_model_dir(tmp_path)
    monkeypatch.setenv("NOVEL_GM_STT_MODEL", str(model))
    monkeypatch.delenv("LOCALAPPDATA", raising=False)
    assert resolve_model_dir() == model.resolve()


def test_resolve_via_localappdata(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.delenv("NOVEL_GM_STT_MODEL", raising=False)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    model = _make_model_dir(tmp_path / "novel-gm" / "stt")
    # 阻断开发机 TTSM / 仓库 stt 误命中：把 ROOT 指到空目录
    import services.core.db as db

    monkeypatch.setattr(db, "ROOT", tmp_path / "empty_root")
    (tmp_path / "empty_root").mkdir()
    assert resolve_model_dir() == model.resolve()


def test_resolve_missing(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.delenv("NOVEL_GM_STT_MODEL", raising=False)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "nope"))
    import services.core.db as db

    monkeypatch.setattr(db, "ROOT", tmp_path / "empty_root")
    (tmp_path / "empty_root").mkdir()
    assert resolve_model_dir() is None


def test_status_shape():
    from services.core.stt import status

    info = status()
    assert "available" in info
    assert "model_dir" in info
    assert "message" in info
    assert isinstance(info["available"], bool)
    assert info.get("languages") == ["en", "ja", "zh"]


def test_normalize_lang():
    from services.core.stt import normalize_lang

    assert normalize_lang("zh") == "zh"
    assert normalize_lang("EN") == "en"
    assert normalize_lang("ja") == "ja"
    assert normalize_lang("ko") == "zh"
    assert normalize_lang("yue") == "zh"
    assert normalize_lang(None) == "zh"


@pytest.mark.skipif(
    resolve_model_dir() is None,
    reason="SenseVoice model not present",
)
def test_transcribe_zh_wav_smoke():
    from services.core.stt import resolve_model_dir, transcribe_path

    model = resolve_model_dir()
    assert model is not None
    # TTSM 自带样例；否则跳过
    candidates = [
        model / "zh.wav",
        model.parent / "zh.wav",
        ROOT.parent / "TTSM" / "models" / MODEL_DIR_NAME / "zh.wav",
        ROOT.parent / "TTSM" / "test_wavs" / "zh.wav",
        ROOT.parent / "TTSM" / "models" / MODEL_DIR_NAME / "test_wavs" / "zh.wav",
    ]
    wav = next((p for p in candidates if p.is_file()), None)
    if wav is None:
        pytest.skip("zh.wav sample not found")
    text, lang = transcribe_path(wav, "zh")
    assert isinstance(text, str)
    assert len(text) > 0
    assert lang == "zh"
