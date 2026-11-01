"""本地语音转文字（SenseVoice Small INT8 + sherpa-onnx）。"""

from .engine import ALLOWED_LANGS, DEFAULT_LANG, normalize_lang, status, transcribe_bytes, transcribe_path
from .paths import MODEL_DIR_NAME, resolve_model_dir

__all__ = [
    "ALLOWED_LANGS",
    "DEFAULT_LANG",
    "MODEL_DIR_NAME",
    "normalize_lang",
    "resolve_model_dir",
    "status",
    "transcribe_bytes",
    "transcribe_path",
]
