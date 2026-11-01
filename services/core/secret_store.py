from __future__ import annotations

import json
import logging
import os
import sqlite3

from . import db

log = logging.getLogger("novel_gm.secrets")

SERVICE = "novel-gm"
DEEPSEEK = "deepseek_api_key"
GITHUB = "github_token"
# 其余厂商的环境变量由 providers.registry 在导入时登记
ENV_VARS: dict[str, tuple[str, ...]] = {
    DEEPSEEK: ("NOVEL_GM_DEEPSEEK_API_KEY", "DEEPSEEK_API_KEY"),
    GITHUB: ("NOVEL_GM_GITHUB_TOKEN", "GITHUB_TOKEN"),
}


class SecretStoreUnavailable(RuntimeError):
    pass


def _keyring():
    try:
        import keyring
        from keyring.backends.fail import Keyring as FailKeyring
    except Exception as exc:
        raise SecretStoreUnavailable(str(exc)) from exc
    if isinstance(keyring.get_keyring(), FailKeyring):
        raise SecretStoreUnavailable("no keyring backend")
    return keyring


def get_secret(name: str = DEEPSEEK) -> str:
    try:
        value = _keyring().get_password(SERVICE, name)
        if value:
            return value.strip()
    except Exception as exc:
        log.warning("credential store read failed: %s", type(exc).__name__)
    for var in ENV_VARS.get(name, ()):
        value = os.environ.get(var, "").strip()
        if value:
            return value
    # 仅在迁移失败（凭据管理器不可用）时才会还有旧明文残留
    return _legacy_value(name)


def set_secret(value: str, name: str = DEEPSEEK) -> None:
    value = (value or "").strip()
    kr = _keyring()
    try:
        if value:
            kr.set_password(SERVICE, name, value)
        else:
            try:
                kr.delete_password(SERVICE, name)
            except Exception:
                pass
    except SecretStoreUnavailable:
        raise
    except Exception as exc:
        raise SecretStoreUnavailable(type(exc).__name__) from exc
    purge_legacy(name)


def migrate_legacy_secrets() -> None:
    """Move plaintext keys left by old versions into the credential store, then scrub them."""
    name = DEEPSEEK
    legacy = _legacy_value(name)
    if not legacy and not _legacy_present(name):
        return
    try:
        kr = _keyring()
        if legacy and not kr.get_password(SERVICE, name):
            kr.set_password(SERVICE, name, legacy)
        if legacy and not kr.get_password(SERVICE, name):
            raise SecretStoreUnavailable("write not persisted")
    except Exception as exc:
        log.warning("plaintext key kept, credential store unavailable: %s", type(exc).__name__)
        return
    purge_legacy(name)
    log.info("plaintext API key moved to credential store")


def _secrets_file():
    return db.get_storage() / "secrets.json"


def _legacy_db_value(name: str) -> str:
    if not db.DB_PATH.exists():
        return ""
    conn = sqlite3.connect(str(db.DB_PATH))
    try:
        row = conn.execute("SELECT value FROM app_settings WHERE key=?", (name,)).fetchone()
    except sqlite3.Error:
        row = None
    finally:
        conn.close()
    return (row[0] or "").strip() if row else ""


def _legacy_file_value(name: str) -> str:
    path = _secrets_file()
    if not path.exists():
        return ""
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return str(data.get(name) or "").strip()
    except Exception:
        return ""


def _legacy_value(name: str) -> str:
    return _legacy_db_value(name) or _legacy_file_value(name)


def _legacy_present(name: str) -> bool:
    if _secrets_file().exists():
        return True
    if not db.DB_PATH.exists():
        return False
    conn = sqlite3.connect(str(db.DB_PATH))
    try:
        return conn.execute("SELECT 1 FROM app_settings WHERE key=?", (name,)).fetchone() is not None
    except sqlite3.Error:
        return False
    finally:
        conn.close()


def purge_legacy(name: str = DEEPSEEK) -> None:
    _purge_db(name)
    _purge_file(name)


def _purge_db(name: str) -> None:
    if not db.DB_PATH.exists():
        return
    conn = sqlite3.connect(str(db.DB_PATH), isolation_level=None)
    try:
        if conn.execute("SELECT 1 FROM app_settings WHERE key=?", (name,)).fetchone() is None:
            return
        # secure_delete + VACUUM + 截断 WAL：删掉的明文不会残留在空闲页或日志里
        conn.execute("PRAGMA secure_delete = ON")
        conn.execute("DELETE FROM app_settings WHERE key=?", (name,))
        try:
            conn.execute("VACUUM")
            conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        except sqlite3.Error as exc:
            log.warning("vacuum after key purge failed: %s", exc)
    except sqlite3.Error as exc:
        log.warning("purge plaintext key from db failed: %s", exc)
    finally:
        conn.close()


def _purge_file(name: str) -> None:
    path = _secrets_file()
    if not path.exists():
        return
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        data = {}
    if not isinstance(data, dict):
        data = {}
    data.pop(name, None)
    if data:
        path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    else:
        path.unlink(missing_ok=True)
