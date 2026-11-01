from __future__ import annotations

import json
import sys
from pathlib import Path

import keyring
import pytest
from fastapi.testclient import TestClient
from keyring.backend import KeyringBackend
from keyring.backends.fail import Keyring as FailKeyring

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db
from services.core import secret_store
from services.core.main import app

client = TestClient(app)
KEY = "fake-key-for-tests-not-a-secret-9f3a"


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
    monkeypatch.delenv("DEEPSEEK_API_KEY", raising=False)
    monkeypatch.delenv("NOVEL_GM_DEEPSEEK_API_KEY", raising=False)
    db.init_db()
    return tmp_path


@pytest.fixture
def vault():
    previous = keyring.get_keyring()
    backend = MemoryKeyring()
    keyring.set_keyring(backend)
    yield backend
    keyring.set_keyring(previous)


@pytest.fixture
def no_vault():
    previous = keyring.get_keyring()
    keyring.set_keyring(FailKeyring())
    yield
    keyring.set_keyring(previous)


def _plaintext_anywhere(storage: Path, secret: str) -> list[str]:
    hits = []
    for f in storage.rglob("*"):
        if f.is_file() and secret.encode() in f.read_bytes():
            hits.append(f.name)
    return hits


def _write_legacy(storage: Path, key: str = KEY) -> None:
    with db.connect() as conn:
        db.set_setting(conn, secret_store.DEEPSEEK, key)
    (storage / "secrets.json").write_text(json.dumps({secret_store.DEEPSEEK: key}), encoding="utf-8")


def test_save_key_only_goes_to_credential_store(vault, storage):
    r = client.post("/settings", json={"deepseek_api_key": KEY})
    assert r.status_code == 200
    body = r.json()
    assert body["has_deepseek_key"] is True
    assert body["key_hint"] == "…" + KEY[-4:]
    assert KEY not in r.text
    assert vault.data[(secret_store.SERVICE, secret_store.DEEPSEEK)] == KEY
    assert not (storage / "secrets.json").exists()
    assert _plaintext_anywhere(storage, KEY) == []


def test_clear_key(vault):
    client.post("/settings", json={"deepseek_api_key": KEY})
    r = client.post("/settings", json={"deepseek_api_key": ""})
    assert r.json()["has_deepseek_key"] is False
    assert vault.data == {}


def test_save_refused_without_credential_store(no_vault, storage):
    r = client.post("/settings", json={"deepseek_api_key": KEY}, headers={"Accept-Language": "en-US"})
    assert r.status_code == 503
    assert r.json()["detail"]["code"] == "KEYRING_UNAVAILABLE"
    assert "credential manager" in r.json()["detail"]["message"]
    assert _plaintext_anywhere(storage, KEY) == []


def test_migrates_and_scrubs_legacy_plaintext(vault, storage):
    _write_legacy(storage)
    assert _plaintext_anywhere(storage, KEY)
    secret_store.migrate_legacy_secrets()
    assert vault.data[(secret_store.SERVICE, secret_store.DEEPSEEK)] == KEY
    assert not (storage / "secrets.json").exists()
    assert _plaintext_anywhere(storage, KEY) == []
    assert secret_store.get_secret() == KEY


def test_migration_keeps_existing_vault_key(vault, storage):
    vault.set_password(secret_store.SERVICE, secret_store.DEEPSEEK, "sk-newer")
    _write_legacy(storage)
    secret_store.migrate_legacy_secrets()
    assert secret_store.get_secret() == "sk-newer"
    assert _plaintext_anywhere(storage, KEY) == []


def test_migration_keeps_other_secrets_json_fields(vault, storage):
    (storage / "secrets.json").write_text(json.dumps({secret_store.DEEPSEEK: KEY, "other": "x"}), encoding="utf-8")
    secret_store.migrate_legacy_secrets()
    assert json.loads((storage / "secrets.json").read_text(encoding="utf-8")) == {"other": "x"}


def test_migration_without_credential_store_keeps_key_usable(no_vault, storage):
    _write_legacy(storage)
    secret_store.migrate_legacy_secrets()
    assert secret_store.get_secret() == KEY


def test_env_var_fallback(vault, monkeypatch):
    monkeypatch.setenv("DEEPSEEK_API_KEY", "sk-from-env")
    assert secret_store.get_secret() == "sk-from-env"
