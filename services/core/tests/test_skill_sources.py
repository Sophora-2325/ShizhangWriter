from __future__ import annotations

import json
import sys
from pathlib import Path

import keyring
import pytest
from fastapi.testclient import TestClient
from keyring.backend import KeyringBackend

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db
from services.core import skill_sources
from services.core.main import app
from services.core.skill_sources import Fetched
from services.core.skills import parse_skill_md

client = TestClient(app)

ARTICLE = """<html><head><title>场景写作</title><script>var x = 1;</script></head><body>
<nav>首页 关于 登录</nav>
<article><h1>如何写好场景</h1>
<p>每个场景都要有一个明确的目标，主角想要什么，必须在开头几段就让读者知道。</p>
<p>目标之后是冲突：有人或有事挡在主角和目标之间，冲突要一步步升级，不能一次说完。</p>
<p>场景结尾给出挫折，主角没能得到想要的东西，或者得到了却付出更大的代价，这样读者才会翻页。</p>
<p>挫折之后是续场：先写反应，让人物消化刚才的失败；再写两难，摆出几条都不好走的路；最后写决定，人物选定一条路，这个决定就是下一个场景的目标。</p>
<p>场景和续场交替出现，节奏就有了张弛：场景快，续场慢；动作戏多用场景，情感戏多给续场留篇幅。</p>
</article><footer>版权所有</footer></body></html>"""


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
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)
    monkeypatch.delenv("NOVEL_GM_GITHUB_TOKEN", raising=False)
    db.init_db()
    previous = keyring.get_keyring()
    keyring.set_keyring(MemoryKeyring())
    yield tmp_path
    keyring.set_keyring(previous)


@pytest.fixture
def web(monkeypatch):
    """按 URL 返回预设内容，并记下每次请求。"""
    pages: dict[str, Fetched] = {}
    calls: list[tuple[str, dict]] = []

    def fake_get(url, headers=None, timeout=0):
        calls.append((url, headers or {}))
        if url in pages:
            return pages[url]
        return Fetched(404, "", "text/plain", url)

    monkeypatch.setattr(skill_sources, "_http_get", fake_get)

    def add(url, text, status=200, ctype="text/plain"):
        if not isinstance(text, str):
            text, ctype = json.dumps(text), "application/json"
        pages[url] = Fetched(status, text, ctype, url)

    add.calls = calls
    return add


def test_parse_skill_md_accepts_block_scalars_and_nested_keys():
    meta, body = parse_skill_md(
        "---\nname: humanizer\ndescription: |\n  Remove signs of\n  AI writing.\nmetadata:\n  version: 2\nlicense: MIT\n---\n\n# Body"
    )
    assert meta == {"name": "humanizer", "description": "Remove signs of AI writing.", "metadata": "", "license": "MIT"}
    assert body == "# Body"


def test_raw_url_conversion(web):
    assert skill_sources.raw_url("https://github.com/o/r/blob/main/skills/de-ai/SKILL.md") == (
        "https://raw.githubusercontent.com/o/r/main/skills/de-ai/SKILL.md", "de-ai")
    assert skill_sources.raw_url("https://github.com/o/r/tree/dev/pack") == (
        "https://raw.githubusercontent.com/o/r/dev/pack/SKILL.md", "pack")
    assert skill_sources.raw_url("https://raw.githubusercontent.com/o/r/main/notes/tips.md") == (
        "https://raw.githubusercontent.com/o/r/main/notes/tips.md", "tips")
    web("https://api.github.com/repos/o/r", {"default_branch": "trunk"})
    assert skill_sources.raw_url("https://github.com/o/r") == ("https://raw.githubusercontent.com/o/r/trunk/SKILL.md", "r")
    r = client.post("/skills/import-url", json={"url": "file:///C:/x.md"}, headers={"Accept-Language": "en"})
    assert r.status_code == 400 and r.json()["detail"]["message"] == "Only http or https links are supported"


def test_import_url_creates_skill_with_source(web):
    url = "https://github.com/o/r/blob/main/skills/hook/SKILL.md"
    web("https://raw.githubusercontent.com/o/r/main/skills/hook/SKILL.md",
        "---\nname: 章末钩子\ndescription: 结尾留悬念\napplies_to: generate\n---\n\n- 最后一句停在动作上。")
    s = client.post("/skills/import-url", json={"url": url}).json()
    assert (s["name"], s["applies_to"], s["instructions"], s["source"]) == ("章末钩子", "generate", "- 最后一句停在动作上。", url)
    assert s["enabled"] is False
    md = (db.STORAGE / "skills" / s["id"] / "SKILL.md").read_text(encoding="utf-8")
    assert f"source: {url}" in md

    pv = client.post("/skills/preview-url", json={"url": url}).json()
    assert (pv["name"], pv["too_long"]) == ("章末钩子", False)


def test_import_url_rejects_html_and_huge_files(web):
    web("https://example.com/post.md", "<html><body>hi</body></html>", ctype="text/html")
    r = client.post("/skills/import-url", json={"url": "https://example.com/post.md"})
    assert r.json()["detail"]["code"] == "NOT_MARKDOWN"
    web("https://example.com/big.md", "x" * (skill_sources.MAX_IMPORT_CHARS + 1))
    r = client.post("/skills/import-url", json={"url": "https://example.com/big.md"}, headers={"Accept-Language": "en"})
    assert r.json()["detail"]["code"] == "TOO_LONG"
    assert "Distill with AI" in r.json()["detail"]["message"]
    r = client.post("/skills/import-url", json={"url": "https://example.com/missing.md"})
    assert r.status_code == 404


def test_github_search_without_and_with_token(web):
    repo_q = "https://api.github.com/search/repositories?q=%E6%96%87%E9%A3%8E%20skill%20in%3Aname%2Cdescription%2Ctopics&sort=stars&per_page=30&page=1"
    web(repo_q, {"total_count": 1, "items": [{"full_name": "a/b", "description": "d", "html_url": "https://github.com/a/b", "stargazers_count": 7}]})
    r = client.get("/skills/github/search", params={"q": "文风"}).json()
    assert r["mode"] == "repo" and r["items"][0]["repo"] == "a/b" and r["items"][0]["stars"] == 7
    assert "Authorization" not in web.calls[-1][1]

    assert client.put("/skills/github-token", json={"token": "ghp_fake_for_tests"}).json() == {"has_github_token": True}
    assert client.get("/skills/sources").json()["has_github_token"] is True
    code_q = "https://api.github.com/search/code?q=%E6%96%87%E9%A3%8E%20filename%3ASKILL.md&per_page=30&page=1"
    web(code_q, {"total_count": 1, "items": [{"path": "x/SKILL.md", "html_url": "https://github.com/a/b/blob/main/x/SKILL.md",
                                             "repository": {"full_name": "a/b", "description": None}}]})
    r = client.get("/skills/github/search", params={"q": "文风"}).json()
    assert r["mode"] == "code" and r["items"][0]["path"] == "x/SKILL.md"
    assert web.calls[-1][1]["Authorization"] == "Bearer ghp_fake_for_tests"
    assert client.put("/skills/github-token", json={"token": ""}).json() == {"has_github_token": False}


def test_github_rate_limit_is_friendly(web):
    web("https://api.github.com/repos/a/b", {"message": "rate limit"}, status=403)
    r = client.get("/skills/github/repo", params={"repo": "a/b"}, headers={"Accept-Language": "en"})
    assert r.status_code == 429 and "rate limit" in r.json()["detail"]["message"]
    assert client.get("/skills/github/repo", params={"repo": "../etc"}).status_code == 400


def test_list_repo_skills(web):
    web("https://api.github.com/repos/a/b", {"default_branch": "main", "description": "pack"})
    web("https://api.github.com/repos/a/b/git/trees/main?recursive=1", {"tree": [
        {"path": "skills/hook/SKILL.md", "type": "blob"},
        {"path": "skills/hook/ref.md", "type": "blob"},
        {"path": "README.md", "type": "blob"},
        {"path": "skills", "type": "tree"},
    ]})
    r = client.get("/skills/github/repo", params={"repo": "a/b"}).json()
    assert r["only_markdown"] is False
    assert [f["path"] for f in r["files"]] == ["skills/hook/SKILL.md"]
    assert r["files"][0]["url"] == "https://github.com/a/b/blob/main/skills/hook/SKILL.md"


def test_extract_text_prefers_article():
    title, text = skill_sources.extract_text(ARTICLE)
    assert title == "场景写作"
    assert text.startswith("如何写好场景\n每个场景都要有一个明确的目标")
    assert "首页" not in text and "版权所有" not in text and "var x" not in text


def test_extract_text_keeps_all_sections():
    para = "<p>" + "这是一段足够长的正文，用来测试正文分成多个小节时是否都能读到。" * 2 + "</p>"
    html = (f"<html><body><div class=side><p>{'侧栏广告文字很长很长很长很长很长很长很长' * 2}</p></div>"
            f"<div id=content><section><h2>一</h2>{para}{para}</section><section><h2>二</h2>{para}{para}</section></div></body></html>")
    _, text = skill_sources.extract_text(html)
    assert text.count("这是一段足够长的正文") == 8 and "一" in text.split("\n") and "二" in text.split("\n")
    assert "侧栏广告" not in text


def test_charset_from_meta():
    assert skill_sources._charset("text/html", b'<html><head><meta charset="gbk">') == "gbk"
    assert skill_sources._charset("text/html; charset=ISO-8859-1", b'<meta charset="utf-8">') == "ISO-8859-1"
    assert skill_sources._charset("text/plain", b"plain") == "utf-8"


def test_distill_url_with_mock_returns_draft_only(web, storage):
    with db.connect() as conn:
        db.set_setting(conn, "provider", "mock")
    web("https://blog.example.com/scene", ARTICLE, ctype="text/html; charset=utf-8")
    before = {s["id"] for s in client.get("/skills").json()}
    r = client.post("/skills/distill", json={"url": "https://blog.example.com/scene", "hint": "偏网文"})
    assert r.status_code == 200, r.text
    d = r.json()
    assert d["name"] == "示例技法" and d["applies_to"] == "generate" and d["scenes"] == []
    assert d["instructions"].startswith("- ") and d["source"] == "https://blog.example.com/scene"
    assert d["title"] == "场景写作" and d["truncated"] is False
    assert {s["id"] for s in client.get("/skills").json()} == before
    with db.connect() as conn:
        row = conn.execute("SELECT project_id, purpose FROM usage_log").fetchone()
    assert tuple(row) == (None, "skill_distill")


def test_distill_github_link_reads_raw_file(web, storage):
    with db.connect() as conn:
        db.set_setting(conn, "provider", "mock")
    web("https://raw.githubusercontent.com/o/r/main/big/SKILL.md", "---\nname: big\n---\n\n" + "很长的技法说明。" * 40)
    d = client.post("/skills/distill", json={"url": "https://github.com/o/r/blob/main/big/SKILL.md"}).json()
    assert d["source"] == "https://github.com/o/r/blob/main/big/SKILL.md" and d["title"] == "big"
    assert web.calls[-1][0] == "https://raw.githubusercontent.com/o/r/main/big/SKILL.md"


def test_distill_text_and_errors(web, storage):
    with db.connect() as conn:
        db.set_setting(conn, "provider", "mock")
    assert client.post("/skills/distill", json={"text": "太短"}).json()["detail"]["code"] == "TOO_SHORT"
    ok = client.post("/skills/distill", json={"text": "写对白时，" * 30})
    assert ok.status_code == 200 and ok.json()["source"] == ""

    r = client.post("/skills/distill", json={"url": "https://zhuanlan.zhihu.com/p/1"}, headers={"Accept-Language": "en"})
    assert r.json()["detail"]["code"] == "LOGIN_REQUIRED" and "Paste text" in r.json()["detail"]["message"]
    web("https://example.com/spa", "<html><body><div id=app></div></body></html>", ctype="text/html")
    assert client.post("/skills/distill", json={"url": "https://example.com/spa"}).json()["detail"]["code"] == "LOGIN_REQUIRED"


def test_distill_without_key_does_not_fall_back_to_mock(web, storage):
    with db.connect() as conn:
        db.set_setting(conn, "provider", "openai")
    r = client.post("/skills/distill", json={"text": "写对白时，" * 30})
    assert r.status_code == 400 and r.json()["detail"]["code"] == "NO_API_KEY"
