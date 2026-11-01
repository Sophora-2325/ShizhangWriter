from __future__ import annotations

import copy
import json
import sys
from pathlib import Path
from types import SimpleNamespace

import keyring
import pytest
from fastapi import HTTPException
from fastapi.testclient import TestClient
from keyring.backend import KeyringBackend

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db
from services.core import repository, secret_store
from services.core.main import app
from services.core.providers import (
    PRESET_IDS,
    default_provider,
    effective_provider,
    list_instances,
    model_for,
    provider_config,
    resolve_provider,
    save_provider_config,
    thinking_for,
)
from services.core.providers.anthropic_provider import (
    JSON_INSTRUCTION,
    AnthropicProvider,
)
from services.core.providers.base import PRICE_TABLE, estimate_cost, lookup_price
from services.core.providers.gemini_provider import GeminiProvider
from services.core.providers.openai_compat import OpenAICompatibleProvider

client = TestClient(app)
KEY = "fake-openai-key-for-tests-7c21"


class MemoryKeyring(KeyringBackend):
    priority = 1

    def __init__(self):
        super().__init__()
        self.data: dict[tuple[str, str], str] = {}

    def get_password(self, service, username):
        return self.data.get((service, username))

    def set_password(self, service, username, password):
        self.data[(service, username)] = password

    def delete_password(self, service, username):
        self.data.pop((service, username), None)


@pytest.fixture(autouse=True)
def storage(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path)
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "t.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "projects")
    for names in secret_store.ENV_VARS.values():
        for var in names:
            monkeypatch.delenv(var, raising=False)
    db.init_db()
    previous = keyring.get_keyring()
    backend = MemoryKeyring()
    keyring.set_keyring(backend)
    yield tmp_path
    keyring.set_keyring(previous)


@pytest.fixture
def vault():
    return keyring.get_keyring()


def _setting(key: str, value: str) -> None:
    with db.connect() as conn:
        db.set_setting(conn, key, value)


def _get_setting(key: str) -> str:
    with db.connect() as conn:
        return db.get_setting(conn, key, "")


def _add(preset: str, **fields) -> dict:
    r = client.post("/providers", json={"preset": preset, **fields})
    assert r.status_code == 200, r.text
    return r.json()


class FakeErr(Exception):
    def __init__(self, msg: str, status_code: int = 400):
        super().__init__(msg)
        self.status_code = status_code
        self.code = status_code


class Recorder:
    """按顺序返回预设结果（异常就抛出），并记下每次调用的参数。"""

    def __init__(self, outcomes, default):
        self.outcomes = list(outcomes)
        self.default = default
        self.calls: list[dict] = []

    def __call__(self, **kw):
        self.calls.append(copy.deepcopy({k: v for k, v in kw.items() if k != "config"}) | ({"config": kw["config"]} if "config" in kw else {}))
        out = self.outcomes.pop(0) if self.outcomes else self.default
        if isinstance(out, Exception):
            raise out
        return out


# ---- 预设、模型配置与接口 ----

def test_presets_and_empty_list():
    assert client.get("/providers").json() == []
    presets = client.get("/providers/presets").json()
    ids = [p["id"] for p in presets]
    assert "mock" not in ids and ids == list(PRESET_IDS)
    by_id = {p["id"]: p for p in presets}
    assert by_id["openai"]["group"] == "intl" and by_id["qwen"]["group"] == "cn" and by_id["local"]["group"] == "local"
    assert by_id["anthropic"]["supports_thinking"] is True and by_id["xai"]["supports_thinking"] is False
    assert by_id["custom"]["base_url_required"] is True and by_id["openai"]["base_url_required"] is False
    assert by_id["local"]["price"] == {"input_miss": 0.0, "input_cache_hit": 0.0, "output": 0.0}
    assert by_id["openai"]["env_vars"] == ["OPENAI_API_KEY"]


def test_create_update_key_models_and_price(vault, storage):
    price = {"input_miss": 1, "input_cache_hit": 0.5, "output": 3}
    r = client.post("/providers", json={"preset": "openai", "api_key": KEY, "model_fast": "gpt-x", "price": price})
    v = r.json()
    assert KEY not in r.text
    assert (v["id"], v["preset"], v["name"]) == ("openai", "openai", "OpenAI")
    assert (v["has_key"], v["key_hint"], v["model_fast"], v["configured"]) == (True, "…" + KEY[-4:], "gpt-x", True)
    assert v["custom_price"] == {"input_miss": 1.0, "input_cache_hit": 0.5, "output": 3.0} == v["price"]
    assert vault.data[(secret_store.SERVICE, "openai_api_key")] == KEY
    assert not any(KEY.encode() in f.read_bytes() for f in storage.rglob("*") if f.is_file())

    v = client.put("/providers/openai", json={"model_fast": "", "clear_price": True, "name": "主力"}).json()
    assert v["model_fast"] == "gpt-6-luna" and v["custom_price"] is None and v["name"] == "主力"
    assert v["price"] == PRICE_TABLE["gpt-6.1-sol"]
    v = client.put("/providers/openai", json={"price": {"input_miss": -1, "input_cache_hit": 0, "output": 0}}).json()
    assert v["custom_price"] is None

    assert client.delete("/providers/openai/key").json()["has_key"] is False
    assert (secret_store.SERVICE, "openai_api_key") not in vault.data
    assert client.put("/providers/nope", json={}).json()["detail"]["code"] == "BAD_PROVIDER"
    assert client.post("/providers", json={"preset": "mock"}).json()["detail"]["code"] == "BAD_PROVIDER"
    assert client.post("/providers", json={"preset": "nope"}).json()["detail"]["code"] == "BAD_PROVIDER"


def test_same_preset_twice_gets_numbered_ids_and_names(vault):
    a = _add("openai", api_key="key-a")
    b = _add("openai", api_key="key-b")
    c = _add("openai", name="中转站")
    assert [(x["id"], x["name"]) for x in (a, b, c)] == [("openai", "OpenAI"), ("openai-2", "OpenAI 2"), ("openai-3", "中转站")]
    assert vault.data[(secret_store.SERVICE, "openai_api_key")] == "key-a"
    assert vault.data[(secret_store.SERVICE, "openai-2_api_key")] == "key-b"
    assert [p["id"] for p in client.get("/providers").json()] == ["openai", "openai-2", "openai-3"]

    r = client.put("/providers/openai-2", json={"name": "中转站"}, headers={"Accept-Language": "en"})
    assert r.status_code == 400 and r.json()["detail"]["message"] == "A model config named 中转站 already exists"
    assert client.post("/providers", json={"preset": "gemini", "name": "openai"}).json()["detail"]["code"] == "DUPLICATE_NAME"


def test_delete_removes_key_and_moves_default(vault):
    _add("openai", api_key="key-a")
    _add("deepseek", api_key="key-d")
    assert client.post("/settings", json={"provider": "openai"}).json()["provider"] == "openai"
    assert client.delete("/providers/openai").json() == {"ok": True}
    assert (secret_store.SERVICE, "openai_api_key") not in vault.data
    assert _get_setting("provider") == "deepseek"
    assert client.get("/settings").json()["provider"] == "deepseek"
    assert client.delete("/providers/openai").json()["detail"]["code"] == "BAD_PROVIDER"
    # 删掉后再添加同一预设，id 可以复用，旧密钥不会回来
    assert _add("openai")["has_key"] is False


def test_project_pointing_at_deleted_config_inherits():
    _add("openai", api_key="a")
    _add("openai", api_key="b")
    _setting("provider", "openai")
    assert effective_provider("openai-2", False) == ("openai-2", False)
    client.delete("/providers/openai-2")
    assert effective_provider("openai-2", False) == ("openai", False)
    assert resolve_provider(*effective_provider("openai-2", False))[1] == "openai"


def test_migration_keeps_ids_and_keys(vault):
    _setting("provider_configs", json.dumps({"openai": {"model_fast": "gpt-x", "thinking": False}}))
    _setting("provider", "gemini")
    _setting("local_model", "qwen-local")
    vault.set_password(secret_store.SERVICE, "deepseek_api_key", "old-deepseek-key")
    items = list_instances()
    assert [i["id"] for i in items] == ["openai", "gemini", "deepseek", "local"]
    assert all(i["id"] == i["preset"] for i in items)
    views = {v["id"]: v for v in client.get("/providers").json()}
    assert views["openai"]["model_fast"] == "gpt-x" and views["openai"]["thinking"] is False
    assert views["deepseek"]["has_key"] is True and views["deepseek"]["name"] == "DeepSeek"
    assert views["local"]["model_fast"] == "qwen-local"
    assert client.get("/settings").json()["provider"] == "gemini"
    # 迁移只做一次：删掉的配置不会因为旧数据又出现
    client.delete("/providers/deepseek")
    assert "deepseek" not in [i["id"] for i in list_instances()]


def test_fresh_install_has_no_configs():
    assert list_instances() == []
    assert client.get("/settings").json()["provider"] == ""
    with pytest.raises(HTTPException) as e:
        default_provider()
    assert e.value.detail["code"] == "NO_PROVIDER"


def test_env_var_counts_as_key_only_for_the_preset_id(monkeypatch):
    monkeypatch.setenv("OPENAI_API_KEY", "sk-from-env")
    # 首次迁移时，设了环境变量的预设会自动生成配置
    (a,) = client.get("/providers").json()
    b = _add("openai")
    assert (a["id"], a["has_key"], a["env_vars"]) == ("openai", True, ["OPENAI_API_KEY"])
    assert b["id"] == "openai-2"
    assert (b["has_key"], b["env_vars"]) == (False, [])


def test_local_config_shares_legacy_settings():
    _add("local")
    save_provider_config("local", {"base_url": "http://127.0.0.1:1234/v1/", "model_fast": "qwen-local"})
    s = client.get("/settings").json()
    assert (s["local_base_url"], s["local_model"]) == ("http://127.0.0.1:1234/v1", "qwen-local")
    cfg = provider_config("local")
    assert (cfg["base_url"], cfg["model_fast"], cfg["model_strong"]) == ("http://127.0.0.1:1234/v1", "qwen-local", "qwen-local")
    # 第二个本地模型不和旧设置项共用
    v = _add("local", base_url="http://127.0.0.1:5678/v1", model_fast="other")
    assert (v["id"], v["base_url"], v["model_fast"]) == ("local-2", "http://127.0.0.1:5678/v1", "other")


def test_model_and_thinking_choice():
    _add("openai")
    _add("xai")
    _add("local")
    assert model_for("plan", "openai") == "gpt-6.1-sol"
    assert model_for("audit", "openai") == "gpt-6.1-sol"
    assert model_for("skill_distill", "openai") == "gpt-6.1-sol"
    assert model_for("generate", "openai") == "gpt-6-luna"
    assert model_for("generate", "mock") == "mock"
    assert thinking_for("openai") is True
    save_provider_config("openai", {"thinking": False})
    assert thinking_for("openai") is False
    assert thinking_for("xai") is None and thinking_for("local") is None and thinking_for("mock") is None


def test_offline_uses_a_local_config():
    _add("gemini", api_key="g")
    _setting("provider", "gemini")
    assert effective_provider("inherit", False) == ("gemini", False)
    _setting("offline_mode", "1")
    with pytest.raises(HTTPException) as e:
        default_provider()
    assert e.value.detail["code"] == "NO_LOCAL_MODEL"
    _add("local", name="我的本地")
    assert effective_provider("inherit", False) == ("local", True)
    assert effective_provider("mock", False) == ("mock", True)
    assert default_provider()[1] == "local"


def test_missing_key_is_an_error_not_mock():
    _add("anthropic")
    with pytest.raises(HTTPException) as e:
        resolve_provider("anthropic")
    assert e.value.detail["code"] == "NO_API_KEY"
    r = client.post("/providers/anthropic/test", headers={"Accept-Language": "en"})
    assert r.status_code == 400
    assert r.json()["detail"]["message"] == "Claude (Anthropic) has no API key yet; add one in Settings → Models"
    with pytest.raises(HTTPException) as e:
        resolve_provider("gemini")
    assert e.value.detail["code"] == "NO_PROVIDER"


def test_custom_needs_base_url_and_model():
    _add("custom", name="我的中转")
    r = client.post("/providers/custom/test").json()["detail"]
    assert r["code"] == "NO_BASE_URL" and r["message"].startswith("我的中转")
    v = client.put("/providers/custom", json={"base_url": "http://127.0.0.1:9/v1/"}).json()
    assert v["base_url"] == "http://127.0.0.1:9/v1"
    assert client.post("/providers/custom/test").json()["detail"]["code"] == "NO_MODEL"


def test_mock_test_endpoint():
    r = client.post("/providers/mock/test").json()
    assert r["ok"] is True and r["model"] == "mock"


def test_draft_test_saves_nothing_and_reuses_saved_key(monkeypatch, vault):
    seen: list[tuple[str, str, str]] = []

    def fake_chat(self, req):
        seen.append((self.api_key, self.base_url, req["model"]))
        return {"content": "收到", "latency_ms": 12.3}

    monkeypatch.setattr(OpenAICompatibleProvider, "chat", fake_chat)
    monkeypatch.setattr(OpenAICompatibleProvider, "list_models", lambda self: ["m1", self.api_key])
    r = client.post("/providers/test", json={"preset": "xai", "api_key": "draft-key", "model_fast": "grok-x"}).json()
    assert r == {"ok": True, "model": "grok-x", "reply": "收到", "latency_ms": 12}
    assert seen[-1] == ("draft-key", "https://api.x.ai/v1", "grok-x")
    assert list_instances() == [] and vault.data == {}

    _add("xai", api_key="saved-key")
    client.post("/providers/test", json={"id": "xai", "base_url": "https://relay.example/v1"})
    assert seen[-1] == ("saved-key", "https://relay.example/v1", "grok-4.7")
    assert client.post("/providers/models", json={"id": "xai"}).json() == {"models": ["m1", "saved-key"]}

    r = client.post("/providers/test", json={"preset": "anthropic", "name": "测试用"}).json()["detail"]
    assert r["code"] == "NO_API_KEY" and r["message"].startswith("测试用")
    assert client.post("/providers/test", json={"preset": "custom", "base_url": "http://x/v1"}).json()["detail"]["code"] == "NO_MODEL"
    assert client.post("/providers/test", json={"id": "nope"}).json()["detail"]["code"] == "BAD_PROVIDER"


def test_price_lookup_and_unknown_usage():
    _add("openai")
    _add("local")
    assert lookup_price("claude-sonnet-5-5-20260901") == PRICE_TABLE["claude-sonnet-5-5"]
    assert lookup_price("totally-new-model") is None
    assert estimate_cost("totally-new-model", 1000, 1000) == 0.0
    repository.add_usage(None, "openai", "totally-new-model", 10, 10, 0, 0.0, 1.0, "generate")
    repository.add_usage(None, "local", "whatever", 10, 10, 0, 0.0, 1.0, "generate")
    s = repository.usage_summary()
    assert s["has_unknown_price"] is True
    assert {e["provider"]: e["price_known"] for e in s["entries"]} == {"openai": False, "local": True}
    save_provider_config("openai", {"price": {"input_miss": 1, "input_cache_hit": 1, "output": 1}})
    assert repository.usage_summary()["has_unknown_price"] is False


# ---- OpenAI 兼容 ----

def _openai_resp(text="好"):
    usage = SimpleNamespace(prompt_tokens=1000, completion_tokens=500, prompt_tokens_details=SimpleNamespace(cached_tokens=200))
    return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=text))], usage=usage)


def _openai_client(outcomes):
    rec = Recorder(outcomes, _openai_resp())
    return SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=rec))), rec


MSGS = [
    {"role": "system", "content": "你是作者"},
    {"role": "user", "content": "写第一章"},
    {"role": "assistant", "content": "开头", "prefix": True},
]


def test_openai_params_prefix_and_retry():
    p = OpenAICompatibleProvider("https://api.openai.com/v1", "k", "openai", display="OpenAI",
                                 thinking_style="reasoning_effort", max_tokens_param="max_completion_tokens")
    p.client, rec = _openai_client([FakeErr("Unsupported parameter: 'temperature' is not supported with this model.")])
    r = p.chat({"model": "gpt-6.1-sol", "messages": MSGS, "thinking": True, "json_mode": True, "max_tokens": 100})
    first, second = rec.calls
    assert first["temperature"] == 0.85 and "temperature" not in second
    assert first["reasoning_effort"] == "high" and first["max_completion_tokens"] == 100 and "max_tokens" not in first
    assert first["response_format"] == {"type": "json_object"}
    last = first["messages"][-1]
    assert last["role"] == "user" and "【已写开头】\n开头" in last["content"] and "JSON" in last["content"]
    assert all("prefix" not in m for m in first["messages"])
    assert (r["content"], r["input_tokens"], r["cached_tokens"], r["price_known"]) == ("好", 1000, 200, True)
    assert r["cost_usd"] == round((800 * 2.0 + 200 * 0.1 + 500 * 10.0) / 1_000_000, 6)

    r = p.chat({"model": "my-own-model", "messages": MSGS[:2]})
    assert (r["price_known"], r["cost_usd"]) == (False, 0.0)


def test_deepseek_uses_beta_prefix_and_drops_rejected_thinking():
    p = OpenAICompatibleProvider("https://api.deepseek.com", "k", "deepseek", thinking_style="type_extra",
                                 prefix_style="deepseek_beta")
    p.client, main_rec = _openai_client([])
    p._beta, beta_rec = _openai_client([FakeErr("unknown field: thinking")])
    p.chat({"model": "deepseek-flash", "messages": MSGS, "thinking": False})
    assert main_rec.calls == []
    first, second = beta_rec.calls
    assert first["messages"][-1] == {"role": "assistant", "content": "开头", "prefix": True}
    assert first["extra_body"] == {"thinking": {"type": "disabled"}} and "extra_body" not in second


def test_openai_bad_key_maps_to_friendly_error():
    p = OpenAICompatibleProvider("https://api.x.ai/v1", "k", "xai", display="xAI Grok")
    p.client, _ = _openai_client([FakeErr("Incorrect API key provided", status_code=401)])
    with pytest.raises(HTTPException) as e:
        p.chat({"model": "grok-4.7", "messages": MSGS[:2]})
    assert e.value.status_code == 400 and e.value.detail["code"] == "BAD_API_KEY"
    p.client, _ = _openai_client([FakeErr("model overloaded", status_code=529)])
    with pytest.raises(HTTPException) as e:
        p.chat({"model": "grok-4.7", "messages": MSGS[:2]})
    assert e.value.status_code == 502 and e.value.detail["message"] == "xAI Grok 调用失败：model overloaded"


# ---- Anthropic ----

def _claude_resp():
    usage = SimpleNamespace(input_tokens=100, cache_read_input_tokens=900, cache_creation_input_tokens=0, output_tokens=50)
    return SimpleNamespace(
        content=[SimpleNamespace(type="thinking", thinking="想一想"), SimpleNamespace(type="text", text="续写")],
        usage=usage,
    )


def test_claude_system_cache_thinking_fallback_and_prefill_fallback():
    p = AnthropicProvider("k", "anthropic", display="Claude")
    rec = Recorder([
        FakeErr("thinking.type: adaptive thinking is not supported on this model"),
        FakeErr("This model does not support assistant message prefill"),
    ], _claude_resp())
    p.client = SimpleNamespace(messages=SimpleNamespace(create=rec))
    msgs = MSGS[:2] + [{"role": "assistant", "content": "开头  ", "prefix": True}]
    r = p.chat({"model": "claude-opus-5-5", "messages": msgs, "thinking": True, "temperature": 0.9})
    a, b, c = rec.calls
    assert a["system"] == [{"type": "text", "text": "你是作者", "cache_control": {"type": "ephemeral"}}]
    assert "temperature" not in a
    assert a["messages"][-1] == {"role": "assistant", "content": "开头"}
    assert a["thinking"] == {"type": "adaptive"} and a["output_config"] == {"effort": "high"}
    assert b["thinking"] == {"type": "enabled", "budget_tokens": 4096} and b["max_tokens"] >= 8192
    assert c["messages"][-1]["role"] == "user" and "【已写开头】\n开头" in c["messages"][-1]["content"]
    assert c["thinking"] == b["thinking"]
    assert (r["content"], r["input_tokens"], r["cached_tokens"], r["output_tokens"]) == ("续写", 1000, 900, 50)


def test_claude_json_and_low_effort():
    p = AnthropicProvider("k", "anthropic")
    rec = Recorder([FakeErr("output_config.effort: unsupported")], _claude_resp())
    p.client = SimpleNamespace(messages=SimpleNamespace(create=rec))
    p.chat({"model": "claude-sonnet-5-5", "messages": MSGS[:2], "thinking": False, "json_mode": True})
    a, b = rec.calls
    assert a["output_config"] == {"effort": "low"} and "thinking" not in a and "output_config" not in b
    assert a["messages"][-1]["content"].endswith(JSON_INSTRUCTION)


# ---- Gemini ----

def test_gemini_config_and_rejected_thinking():
    p = GeminiProvider("k", "gemini", display="Gemini")
    resp = SimpleNamespace(text='{"a": 1}', usage_metadata=SimpleNamespace(
        prompt_token_count=100, candidates_token_count=20, thoughts_token_count=5, cached_content_token_count=40))
    rec = Recorder([FakeErr("thinking_level is not supported for this model")], resp)
    p.client = SimpleNamespace(models=SimpleNamespace(generate_content=rec))
    r = p.chat({"model": "gemini-3.8-flash", "messages": MSGS, "thinking": True, "json_mode": True})
    a, b = rec.calls
    cfg = a["config"]
    assert cfg.system_instruction == "你是作者" and cfg.response_mime_type == "application/json"
    assert "HIGH" in str(cfg.thinking_config.thinking_level)
    assert b["config"].thinking_config is None and b["config"].response_mime_type == "application/json"
    roles = [c.role for c in a["contents"]]
    assert roles == ["user"] and "【已写开头】" in a["contents"][-1].parts[0].text
    assert (r["content"], r["input_tokens"], r["output_tokens"], r["cached_tokens"]) == ('{"a": 1}', 100, 25, 40)
