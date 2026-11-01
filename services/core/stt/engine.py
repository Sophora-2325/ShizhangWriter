from __future__ import annotations

import array
import io
import os
import threading
import wave
from pathlib import Path
from typing import Any

from .paths import resolve_model_dir

ALLOWED_LANGS = frozenset({"zh", "en", "ja"})
DEFAULT_LANG = "zh"

_lock = threading.Lock()
_recognizer = None
_model_dir: Path | None = None
_language: str | None = None


def normalize_lang(lang: str | None) -> str:
    code = (lang or "").strip().lower()
    return code if code in ALLOWED_LANGS else DEFAULT_LANG


def status() -> dict[str, Any]:
    model_dir = resolve_model_dir()
    if model_dir is None:
        return {
            "available": False,
            "model_dir": None,
            "message": "未找到语音模型。请将 SenseVoice INT8 放到 %LOCALAPPDATA%\\novel-gm\\stt\\sense-voice-int8-2024-07-17，或设置 NOVEL_GM_STT_MODEL。",
            "languages": sorted(ALLOWED_LANGS),
        }
    return {
        "available": True,
        "model_dir": str(model_dir),
        "message": "语音模型已就绪",
        "languages": sorted(ALLOWED_LANGS),
    }


def _get_recognizer(language: str):
    global _recognizer, _model_dir, _language
    lang = normalize_lang(language)
    model_dir = resolve_model_dir()
    if model_dir is None:
        raise FileNotFoundError("STT_MODEL_MISSING")
    with _lock:
        if _recognizer is not None and _model_dir == model_dir and _language == lang:
            return _recognizer
        import sherpa_onnx

        _recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=str(model_dir / "model.int8.onnx"),
            tokens=str(model_dir / "tokens.txt"),
            num_threads=max(1, min(4, (os.cpu_count() or 2))),
            language=lang,
            use_itn=True,
            provider="cpu",
        )
        _model_dir = model_dir
        _language = lang
        return _recognizer


def _pcm_float_from_wav(data: bytes) -> tuple[int, list[float]]:
    with wave.open(io.BytesIO(data), "rb") as wf:
        channels = wf.getnchannels()
        width = wf.getsampwidth()
        rate = wf.getframerate()
        n = wf.getnframes()
        raw = wf.readframes(n)
    if width != 2:
        raise ValueError("WAV 仅支持 16-bit PCM")
    samples = array.array("h")
    samples.frombytes(raw)
    if channels == 1:
        floats = [s / 32768.0 for s in samples]
    elif channels == 2:
        floats = [(samples[i] + samples[i + 1]) / 2 / 32768.0 for i in range(0, len(samples) - 1, 2)]
    else:
        raise ValueError("WAV 声道数过多")
    if not floats:
        raise ValueError("音频为空")
    return rate, floats


def transcribe_bytes(data: bytes, language: str = DEFAULT_LANG) -> tuple[str, str]:
    if not data:
        raise ValueError("音频为空")
    lang = normalize_lang(language)
    rate, samples = _pcm_float_from_wav(data)
    rec = _get_recognizer(lang)
    stream = rec.create_stream()
    stream.accept_waveform(rate, samples)
    rec.decode_stream(stream)
    return (stream.result.text or "").strip(), lang


def transcribe_path(path: Path | str, language: str = DEFAULT_LANG) -> tuple[str, str]:
    return transcribe_bytes(Path(path).read_bytes(), language)
