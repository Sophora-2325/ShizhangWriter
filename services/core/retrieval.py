"""Version-filtered BM25 + optional semantic retrieval; no network by default."""
from __future__ import annotations

import hashlib
import math
import os
import re
import struct
from collections import Counter
from typing import Any
from urllib.parse import urlparse

import httpx

from .db import connect, dumps, get_setting, loads, new_id
from .freshness import body_hash, summary_valid


def _tokens(text: str) -> Counter:
    words = re.findall(r"[a-z0-9_]+|[\u3400-\u9fff]+", text.lower())
    return Counter(t for w in words for t in
                   ([w] if len(w) == 1 or w.isascii() else [w[i:i+2] for i in range(len(w)-1)]))


def _config(offline=False):
    with connect() as conn:
        offline = offline or get_setting(conn, "offline_mode", "0") in ("1", "true")
    url = os.environ.get("NOVEL_GM_EMBEDDING_URL", "").strip().rstrip("/")
    model = os.environ.get("NOVEL_GM_EMBEDDING_MODEL", "").strip()
    parsed = urlparse(url)
    if not url or not model or parsed.scheme not in ("http", "https"):
        return None
    if offline and parsed.hostname not in ("localhost", "127.0.0.1", "::1"):
        return None
    return url, model, hashlib.sha256(f"{url}\n{model}".encode()).hexdigest()


def _embed(texts, config):
    key = os.environ.get("NOVEL_GM_EMBEDDING_API_KEY", "").strip()
    response = httpx.post(config[0] + "/embeddings", json={"model": config[1], "input": texts},
                          headers={"Authorization": f"Bearer {key}"} if key else {}, timeout=5.0,
                          follow_redirects=False)
    response.raise_for_status()
    data = sorted(response.json()["data"], key=lambda item: item["index"])
    if [x["index"] for x in data] != list(range(len(texts))):
        raise ValueError("embedding indices mismatch")
    vectors = []
    for item in data:
        vector = [float(v) for v in item["embedding"]]
        norm = math.sqrt(sum(v*v for v in vector))
        if not vector or len(vector) > 65536 or not math.isfinite(norm) or norm == 0:
            raise ValueError("invalid embedding")
        vectors.append([v / norm for v in vector])
    if len({len(v) for v in vectors}) != 1:
        raise ValueError("embedding dimensions mismatch")
    return vectors


def upsert_fact_embedding(project_id, chapter_no, fact, known_by, source_hash=""):
    from .repository import get_chapter
    if not source_hash and chapter_no is not None:
        source_hash = body_hash(get_chapter(project_id, chapter_no).body_text)
    fid = new_id()
    with connect() as conn:
        conn.execute("INSERT INTO facts(id,project_id,chapter_no,fact,known_by_json,source_hash) VALUES(?,?,?,?,?,?)",
                     (fid, project_id, chapter_no, fact, dumps(known_by), source_hash))
    return fid


def _eligible(project_id, before_chapter):
    from .repository import list_chapters
    chapters = {c.no: c for c in list_chapters(project_id)
                if c.status.value != "superseded" and (before_chapter is None or c.no < before_chapter)}
    with connect() as conn:
        rows = conn.execute("SELECT * FROM facts WHERE project_id=? ORDER BY chapter_no,id", (project_id,)).fetchall()
    return [r for r in rows if r["chapter_no"] in chapters and summary_valid(chapters[r["chapter_no"]])
            and (not r["source_hash"] or r["source_hash"] == body_hash(chapters[r["chapter_no"]].body_text))]


def _index(rows, config, batch_limit=64):
    missing = [r for r in rows if r["embedding_model"] != config[2] or not r["embedding"]]
    if batch_limit is not None:
        missing = missing[:batch_limit]
    updated = {}
    for start in range(0, len(missing), 32):
        batch = missing[start:start+32]
        vectors = _embed([r["fact"] for r in batch], config)
        with connect() as conn:
            for row, vector in zip(batch, vectors):
                conn.execute("UPDATE facts SET embedding=?,embedding_model=? WHERE id=? AND source_hash=?",
                             (struct.pack(f"<{len(vector)}f", *vector), config[2], row["id"], row["source_hash"]))
                updated[row["id"]] = vector
    return updated


def reindex(project_id):
    from .repository import get_project
    config = _config(get_project(project_id).offline_mode)
    if config is None:
        return {"mode": "lexical", "indexed": 0}
    return {"mode": "hybrid", "indexed": len(_index(_eligible(project_id, None), config, None))}


def search_similar(project_id: str, query: str, limit: int = 8, *, before_chapter=None,
                   character_ids=None, allow_remote=True) -> list[dict[str, Any]]:
    if not query.strip() or limit <= 0:
        return []
    rows = _eligible(project_id, before_chapter)
    if not rows:
        return []
    q = _tokens(query)
    docs = [_tokens(r["fact"]) for r in rows]
    avg = sum(sum(d.values()) for d in docs) / len(docs) or 1
    frequencies = Counter(t for doc in docs for t in doc)
    lexical = {}
    for row, doc in zip(rows, docs):
        score = sum(math.log(1 + (len(docs)-frequencies[t]+.5)/(frequencies[t]+.5))
                    * doc[t]*2.2/(doc[t]+1.2*(.25+.75*sum(doc.values())/avg)) for t in q if doc[t])
        if score > 0:
            lexical[row["id"]] = score
    scores = {fid: 1/(60+rank) for rank, fid in enumerate(sorted(lexical, key=lambda f: -lexical[f]), 1)}
    from .repository import get_project
    config = _config(get_project(project_id).offline_mode) if allow_remote else None
    mode = "lexical"
    if config:
        try:
            query_vector = _embed([query], config)[0]
            updated = _index(rows, config)
            semantic = {}
            for row in rows:
                vector = updated.get(row["id"])
                if vector is None and row["embedding_model"] == config[2] and row["embedding"]:
                    blob = row["embedding"]
                    if len(blob) % 4:
                        continue
                    vector = struct.unpack(f"<{len(blob)//4}f", blob)
                if vector is not None and len(vector) == len(query_vector):
                    score = sum(a*b for a, b in zip(query_vector, vector))
                    if math.isfinite(score) and score >= .25:
                        semantic[row["id"]] = score
            for rank, fid in enumerate(sorted(semantic, key=lambda f: -semantic[f]), 1):
                scores[fid] = scores.get(fid, 0) + 1/(60+rank)
            mode = "hybrid"
        except (httpx.HTTPError, ValueError, KeyError, TypeError, OverflowError):
            pass
    lookup = {r["id"]: r for r in rows}
    if character_ids:
        for fid in scores:
            if set(loads(lookup[fid]["known_by_json"]) or []) & set(character_ids):
                scores[fid] *= 1.1
    # Recheck after a possible embedding network call: edits may have invalidated facts meanwhile.
    valid = {r["id"] for r in _eligible(project_id, before_chapter)}
    result = []
    for fid in sorted(scores, key=lambda f: (-scores[f], f)):
        if fid not in valid:
            continue
        row = lookup[fid]
        result.append({"id": fid, "chapter_no": row["chapter_no"], "fact": row["fact"],
                       "known_by": loads(row["known_by_json"]) or [], "source_hash": row["source_hash"],
                       "score": scores[fid], "retrieval_mode": mode})
        if len(result) >= limit:
            break
    return result
