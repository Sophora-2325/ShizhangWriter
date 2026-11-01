from __future__ import annotations

import json
import os
import sqlite3
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator, Optional

# Defaults: repo root = novel-gm/ (parents[2] from services/core/db.py)
_CODE_ROOT = Path(__file__).resolve().parents[2]


def _default_root() -> Path:
    env = os.environ.get("NOVEL_GM_ROOT", "").strip()
    if env:
        return Path(env)
    return _CODE_ROOT


def _default_storage(root: Path) -> Path:
    env = os.environ.get("NOVEL_GM_DATA", "").strip()
    if env:
        return Path(env)
    # Installed / portable: prefer LOCALAPPDATA when NOVEL_GM_USE_APPDATA=1
    if os.environ.get("NOVEL_GM_USE_APPDATA", "").strip() in ("1", "true", "yes"):
        local = os.environ.get("LOCALAPPDATA") or os.environ.get("HOME") or str(root)
        return Path(local) / "novel-gm" / "storage"
    return root / "storage"


ROOT = _default_root()
STORAGE = _default_storage(ROOT)
DB_PATH = STORAGE / "novel.db"
PROJECTS_DIR = STORAGE / "projects"


def configure_paths(
    root: Optional[Path | str] = None,
    storage: Optional[Path | str] = None,
) -> None:
    """Rebind data paths (call before init_db; used by desktop shell via env)."""
    global ROOT, STORAGE, DB_PATH, PROJECTS_DIR
    if root is not None:
        ROOT = Path(root)
        os.environ["NOVEL_GM_ROOT"] = str(ROOT)
    else:
        ROOT = _default_root()
    if storage is not None:
        STORAGE = Path(storage)
        os.environ["NOVEL_GM_DATA"] = str(STORAGE)
    else:
        STORAGE = _default_storage(ROOT)
    DB_PATH = STORAGE / "novel.db"
    PROJECTS_DIR = STORAGE / "projects"


def get_storage() -> Path:
    return STORAGE


def get_root() -> Path:
    return ROOT


def path_info() -> dict[str, str]:
    return {
        "root": str(ROOT),
        "storage": str(STORAGE),
        "db": str(DB_PATH),
        "projects": str(PROJECTS_DIR),
    }


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def ensure_dirs() -> None:
    STORAGE.mkdir(parents=True, exist_ok=True)
    PROJECTS_DIR.mkdir(parents=True, exist_ok=True)


def chapter_path(project_id: str, chapter_no: int) -> Path:
    d = PROJECTS_DIR / project_id / "chapters"
    d.mkdir(parents=True, exist_ok=True)
    return d / f"ch_{chapter_no:04d}.md"


def summary_path(project_id: str, chapter_no: int) -> Path:
    d = PROJECTS_DIR / project_id / "summaries"
    d.mkdir(parents=True, exist_ok=True)
    return d / f"ch_{chapter_no:04d}.json"


@contextmanager
def connect() -> Iterator[sqlite3.Connection]:
    ensure_dirs()
    conn = sqlite3.connect(str(DB_PATH), check_same_thread=False)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    conn.execute("PRAGMA journal_mode = WAL")
    try:
        yield conn
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


SCHEMA = """
CREATE TABLE IF NOT EXISTS projects (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    created_at TEXT NOT NULL,
    phase TEXT NOT NULL,
    current_chapter INTEGER NOT NULL DEFAULT 0,
    data_json TEXT NOT NULL,
    offline_mode INTEGER NOT NULL DEFAULT 0,
    provider TEXT NOT NULL DEFAULT 'inherit',
    estimated_cost_usd REAL NOT NULL DEFAULT 0,
    root_path TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS chapters (
    project_id TEXT NOT NULL,
    no INTEGER NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL,
    plan_json TEXT,
    body_path TEXT,
    word_count INTEGER NOT NULL DEFAULT 0,
    created_at TEXT,
    summary_json TEXT,
    state_delta_json TEXT,
    consistency_json TEXT,
    plan_check_json TEXT,
    style_json TEXT,
    gate_json TEXT,
    refinement_json TEXT,
    lore_json TEXT,
    body_rich TEXT,
    lore_from INTEGER,
    review_json TEXT,
    actions_json TEXT,
    stacked_constraints_json TEXT,
    PRIMARY KEY (project_id, no),
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id TEXT NOT NULL,
    chapter_no INTEGER,
    event_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS usage_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id TEXT,
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    input_tokens INTEGER NOT NULL DEFAULT 0,
    output_tokens INTEGER NOT NULL DEFAULT 0,
    cached_tokens INTEGER NOT NULL DEFAULT 0,
    cost_usd REAL NOT NULL DEFAULT 0,
    latency_ms REAL NOT NULL DEFAULT 0,
    purpose TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS app_settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS proposal_receipts (
    project_id TEXT NOT NULL,
    chapter_uid TEXT NOT NULL,
    request_id TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    status TEXT NOT NULL,
    response_json TEXT,
    created_at TEXT NOT NULL,
    PRIMARY KEY(project_id, chapter_uid, request_id),
    FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS facts (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL,
    chapter_no INTEGER,
    fact TEXT NOT NULL,
    known_by_json TEXT NOT NULL DEFAULT '[]',
    embedding BLOB,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
);

CREATE VIRTUAL TABLE IF NOT EXISTS facts_fts USING fts5(
    fact,
    content='facts',
    content_rowid='rowid'
);
"""


def _ensure_column(conn: sqlite3.Connection, table: str, column: str, decl: str) -> None:
    cols = {r["name"] for r in conn.execute(f"PRAGMA table_info({table})").fetchall()}
    if column not in cols:
        conn.execute(f"ALTER TABLE {table} ADD COLUMN {column} {decl}")


def init_db() -> None:
    with connect() as conn:
        conn.executescript(SCHEMA)
        _ensure_column(conn, "projects", "root_path", "TEXT NOT NULL DEFAULT ''")
        _ensure_column(conn, "chapters", "lore_json", "TEXT")
        _ensure_column(conn, "chapters", "body_rich", "TEXT")
        _ensure_column(conn, "chapters", "lore_from", "INTEGER")
        _ensure_column(conn, "chapters", "review_json", "TEXT")
        _ensure_column(conn, "chapters", "plan_check_json", "TEXT")
        _ensure_column(conn, "chapters", "freshness_json", "TEXT")
        _ensure_column(conn, "chapters", "actions_json", "TEXT")
        _ensure_column(conn, "chapters", "stacked_constraints_json", "TEXT")
        _ensure_column(conn, "chapters", "chapter_uid", "TEXT NOT NULL DEFAULT ''")
        _ensure_column(conn, "chapters", "proposal_json", "TEXT")
        _ensure_column(conn, "chapters", "body_revision_json", "TEXT")
        conn.execute("""CREATE TABLE IF NOT EXISTS body_revision_receipts (
            project_id TEXT NOT NULL, chapter_uid TEXT NOT NULL, request_id TEXT NOT NULL,
            request_hash TEXT NOT NULL, status TEXT NOT NULL, response_json TEXT, created_at TEXT NOT NULL,
            PRIMARY KEY(project_id,chapter_uid,request_id),
            FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE)""")
        for row in conn.execute("SELECT project_id,no FROM chapters WHERE chapter_uid=''").fetchall():
            conn.execute("UPDATE chapters SET chapter_uid=? WHERE project_id=? AND no=?",
                         (uuid.uuid4().hex, row["project_id"], row["no"]))
        _ensure_column(conn, "facts", "source_hash", "TEXT NOT NULL DEFAULT ''")
        _ensure_column(conn, "facts", "embedding_model", "TEXT NOT NULL DEFAULT ''")
        # 生成方式改为全局设置后，旧作品里存的都是显式的 deepseek，统一改回跟随全局。
        if get_setting(conn, "provider_inherit_migrated", "") != "1":
            conn.execute("UPDATE projects SET provider='inherit', offline_mode=0")
            set_setting(conn, "provider_inherit_migrated", "1")


def dumps(obj: Any) -> str:
    if hasattr(obj, "model_dump"):
        obj = obj.model_dump(mode="json")
    return json.dumps(obj, ensure_ascii=False, default=str)


def loads(s: Optional[str]) -> Any:
    if not s:
        return None
    return json.loads(s)


def new_id() -> str:
    return uuid.uuid4().hex[:12]


def append_event(
    conn: sqlite3.Connection,
    project_id: str,
    event_type: str,
    payload: Any,
    chapter_no: Optional[int] = None,
) -> None:
    conn.execute(
        "INSERT INTO events (project_id, chapter_no, event_type, payload_json, created_at) VALUES (?,?,?,?,?)",
        (project_id, chapter_no, event_type, dumps(payload), utcnow().isoformat()),
    )


def get_setting(conn: sqlite3.Connection, key: str, default: str = "") -> str:
    row = conn.execute("SELECT value FROM app_settings WHERE key=?", (key,)).fetchone()
    return row["value"] if row else default


def set_setting(conn: sqlite3.Connection, key: str, value: str) -> None:
    conn.execute(
        "INSERT INTO app_settings(key, value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
        (key, value),
    )
