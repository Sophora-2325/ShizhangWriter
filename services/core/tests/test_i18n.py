from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from services.core.i18n import language_from_header, translate
from services.core.main import app

STRINGS = ROOT.parent / "apps" / "desktop-wpf" / "Strings"
X_KEY = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"

client = TestClient(app)


def _keys(name: str) -> list[str]:
    root = ET.parse(STRINGS / f"{name}.xaml").getroot()
    return [el.get(X_KEY) for el in root]


def test_client_string_tables_have_same_keys():
    zh = _keys("zh-CN")
    assert len(zh) == len(set(zh)), "zh-CN 里有重复的 key"
    for other in ("en-US", "ja-JP"):
        keys = _keys(other)
        assert len(keys) == len(set(keys)), f"{other} 里有重复的 key"
        assert set(keys) == set(zh), f"{other} 与 zh-CN 的 key 不一致：{set(keys) ^ set(zh)}"


def test_error_message_follows_accept_language():
    body = {"provider": "bogus"}
    zh = client.post("/settings", json=body)
    assert zh.status_code == 400
    assert zh.json()["detail"] == {"code": "BAD_PROVIDER", "message": "未知的生成方式"}

    en = client.post("/settings", json=body, headers={"Accept-Language": "en-US"})
    assert en.status_code == 400
    assert en.json()["detail"]["code"] == "BAD_PROVIDER"
    assert en.json()["detail"]["message"] == "Unknown generation method"

    ja = client.post("/settings", json=body, headers={"Accept-Language": "ja-JP,ja;q=0.9"})
    assert ja.json()["detail"]["message"] == "不明な生成方式です"


def test_parameterized_messages_and_field_names():
    msg = "人物「林烬」必填项未完成：age、outfit"
    assert translate(msg, "en") == 'Required fields missing for character "林烬": age, outfit'
    assert translate(msg, "zh") == "人物「林烬」必填项未完成：年龄、着装"
    assert translate("作品文件夹缺失：D:\\书\\a", "ja") == "作品フォルダーが見つかりません：D:\\书\\a"
    assert translate("没有收录的提示", "en") == "没有收录的提示"


def test_language_from_header():
    assert language_from_header(None) == "zh"
    assert language_from_header("zh-CN") == "zh"
    assert language_from_header("ja") == "ja"
    assert language_from_header("fr-FR,en;q=0.8") == "en"
