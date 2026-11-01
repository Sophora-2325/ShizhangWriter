"""从网上获取 Skill：GitHub 搜索 / 列出仓库里的 SKILL.md / 按链接导入，以及把任意文章交给模型提炼成 Skill 草稿。

所有联网请求都走 _http_get，测试里替换它即可离线运行。
"""
from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any, Optional
from urllib.parse import quote, urlparse

import httpx
from fastapi import HTTPException

from . import secret_store, skills
from .jsonutil import loads_lenient

GITHUB_API = "https://api.github.com"
RAW_HOST = "https://raw.githubusercontent.com"
MAX_BYTES = 2 * 1024 * 1024
TIMEOUT = 15.0
MAX_IMPORT_CHARS = 30_000
MAX_DISTILL_CHARS = 24_000
# 维基百科等站点要求 UA 带联系方式，否则一律 403
_BROWSER_UA = "Mozilla/5.0 (compatible; novel-gm/1.0; +https://github.com/Sophora-2325/Auto-Novel-Writer)"
# 需要登录才能看到正文的站点，直接提示用户复制粘贴
_LOGIN_WALLED = ("zhihu.com", "douban.com", "weibo.com", "xiaohongshu.com")

RECOMMENDED: list[dict[str, Any]] = [
    {
        "name": "blader/humanizer", "kind": "github", "url": "https://github.com/blader/humanizer",
        "desc": {
            "zh": "基于维基百科「AI 写作迹象」整理的去 AI 腔 Skill（英文）",
            "en": "De-AI rewriting skill built from Wikipedia's \"Signs of AI writing\"",
            "ja": "Wikipedia「AI 文章の兆候」に基づく AI 臭さ除去スキル（英語）",
        },
    },
    {
        "name": "zenstory-ai/oh-story-claudecode", "kind": "github",
        "url": "https://github.com/zenstory-ai/oh-story-claudecode",
        "desc": {
            "zh": "中文网文 Skill 包：长短篇写作、拆文、去 AI 味、审稿",
            "en": "Chinese web-novel skill pack: long/short fiction, analysis, de-AI, review",
            "ja": "中国語 Web 小説スキル集：長編・短編執筆、分析、AI 臭さ除去、レビュー",
        },
    },
    {
        "name": "Tomsawyerhu/Chinese-WebNovel-Skill", "kind": "github",
        "url": "https://github.com/Tomsawyerhu/Chinese-WebNovel-Skill",
        "desc": {
            "zh": "中文网文模块化 Skill：开头、转场、对白、章末、一致性",
            "en": "Modular Chinese web-novel skill: openings, transitions, dialogue, chapter endings",
            "ja": "中国語 Web 小説のモジュール型スキル：導入、場面転換、会話、章末",
        },
    },
    {
        "name": "haowjy/creative-writing-skills", "kind": "github",
        "url": "https://github.com/haowjy/creative-writing-skills",
        "desc": {
            "zh": "英文小说写作 Skill 集：文风、场景、修订、审稿",
            "en": "Fiction skills: voice, scenes, revision and critique",
            "ja": "英語の小説執筆スキル集：文体、場面、推敲、批評",
        },
    },
    {
        "name": "danjdewhurst/story-skills", "kind": "github", "url": "https://github.com/danjdewhurst/story-skills",
        "desc": {
            "zh": "故事圣经、人物、世界观、情节结构与连续性检查",
            "en": "Story bible, characters, worldbuilding, plot structure and continuity checks",
            "ja": "ストーリーバイブル、人物、世界観、プロット構成、整合性チェック",
        },
    },
    {
        "name": "Mouseww/yunn-skills", "kind": "github", "url": "https://github.com/Mouseww/yunn-skills",
        "desc": {
            "zh": "长篇小说写作流程，分商业网文和文学两种模式",
            "en": "Long-form fiction workflow with commercial and literary modes",
            "ja": "商業 Web 小説と文学の 2 モードを持つ長編執筆ワークフロー",
        },
    },
    {
        "name": "PenglongHuang/chinese-novelist-skill", "kind": "github",
        "url": "https://github.com/PenglongHuang/chinese-novelist-skill",
        "desc": {
            "zh": "中文小说创作 Skill：大纲、人物、章节写作与修订",
            "en": "Chinese fiction skill: outlines, characters, chapter drafting and revision",
            "ja": "中国語小説スキル：あらすじ、人物、章の執筆と推敲",
        },
    },
    {
        "name": "Wikipedia: Scene and sequel", "kind": "article",
        "url": "https://en.wikipedia.org/wiki/Scene_and_sequel",
        "desc": {
            "zh": "场景与续场（目标—冲突—挫折 / 反应—两难—决定）（英文）",
            "en": "Scenes and sequels (goal–conflict–disaster / reaction–dilemma–decision)",
            "ja": "シーンとシークエル（目標・葛藤・挫折／反応・ジレンマ・決断）（英語）",
        },
    },
    {
        "name": "维基百科：契诃夫之枪", "kind": "article",
        "url": "https://zh.wikipedia.org/wiki/%E5%A5%91%E8%A8%B6%E5%A4%AB%E4%B9%8B%E6%9E%AA",
        "desc": {
            "zh": "伏笔必须回收：故事里提到的元素都要有用",
            "en": "Chekhov's gun (Chinese): every element introduced must pay off",
            "ja": "チェーホフの銃（中国語）：登場させた要素は必ず回収する",
        },
    },
    {
        "name": "Wikipedia: Signs of AI writing", "kind": "article",
        "url": "https://en.wikipedia.org/wiki/Wikipedia:Signs_of_AI_writing",
        "desc": {
            "zh": "维基百科编辑总结的 AI 文本特征清单（英文）",
            "en": "Wikipedia editors' catalog of AI-generated text patterns",
            "ja": "Wikipedia 編集者がまとめた AI 生成文の特徴一覧（英語）",
        },
    },
]


def _bad(message: str, code: str = "BAD_SOURCE", status: int = 400) -> HTTPException:
    return HTTPException(status, detail={"code": code, "message": message})


# ---- 网络 ----

@dataclass
class Fetched:
    status: int
    text: str
    content_type: str
    url: str


def _http_get(url: str, headers: Optional[dict[str, str]] = None, timeout: float = TIMEOUT) -> Fetched:
    """GET，最多读 MAX_BYTES；超出部分直接截断。"""
    with (
        httpx.Client(follow_redirects=True, timeout=timeout) as client,
        client.stream("GET", url, headers=headers or {}) as resp,
    ):
        chunks: list[bytes] = []
        size = 0
        for chunk in resp.iter_bytes():
            chunks.append(chunk)
            size += len(chunk)
            if size >= MAX_BYTES:
                break
        raw = b"".join(chunks)[:MAX_BYTES]
        ctype = resp.headers.get("content-type", "")
        encoding = _charset(ctype, raw)
        final = str(resp.url)
        status = resp.status_code
    try:
        text = raw.decode(encoding, errors="replace")
    except LookupError:
        text = raw.decode("utf-8", errors="replace")
    return Fetched(status, text, ctype, final)


def _charset(content_type: str, raw: bytes) -> str:
    """响应头里的 charset 优先，其次网页 <meta> 声明，都没有按 UTF-8。"""
    m = re.search(r"charset=[\"']?([\w-]+)", content_type, re.IGNORECASE)
    if not m:
        m = re.search(rb"<meta[^>]+charset=[\"']?([\w-]+)", raw[:4096], re.IGNORECASE)
    if not m:
        return "utf-8"
    name = m.group(1)
    return (name.decode("ascii", "ignore") if isinstance(name, bytes) else name) or "utf-8"


def _get(url: str, headers: Optional[dict[str, str]] = None) -> Fetched:
    try:
        return _http_get(url, headers)
    except httpx.TimeoutException:
        raise _bad(f"连接超时：{url}", "FETCH_FAILED", 502)
    except httpx.HTTPError as e:
        raise _bad(f"无法访问 {url}：{type(e).__name__}", "FETCH_FAILED", 502)


def _github_headers() -> dict[str, str]:
    h = {"Accept": "application/vnd.github+json", "User-Agent": "novel-gm", "X-GitHub-Api-Version": "2022-11-28"}
    token = secret_store.get_secret(secret_store.GITHUB)
    if token:
        h["Authorization"] = f"Bearer {token}"
    return h


def _github_json(path: str) -> Any:
    import json

    r = _get(GITHUB_API + path, _github_headers())
    if r.status == 401:
        raise _bad("GitHub 令牌无效，请检查后重新填写", "GITHUB_TOKEN")
    if r.status in (403, 429):
        raise _bad("GitHub 请求次数已用完，请稍后再试，或填写 GitHub 令牌提高额度", "GITHUB_RATE_LIMIT", 429)
    if r.status == 404:
        raise _bad("GitHub 上找不到这个仓库或路径", "NOT_FOUND", 404)
    if r.status >= 400:
        raise _bad(f"GitHub 返回错误：HTTP {r.status}", "GITHUB_ERROR", 502)
    try:
        return json.loads(r.text)
    except ValueError:
        raise _bad("GitHub 返回的内容无法解析", "GITHUB_ERROR", 502)


def has_github_token() -> bool:
    return bool(secret_store.get_secret(secret_store.GITHUB))


def set_github_token(token: str) -> None:
    secret_store.set_secret(token, secret_store.GITHUB)


# ---- GitHub 搜索 ----

def search_github(query: str, page: int = 1) -> dict[str, Any]:
    """有令牌时按文件搜索（filename:SKILL.md），没有时按仓库搜索。"""
    q = " ".join((query or "").split())
    page = max(1, min(int(page or 1), 10))
    if has_github_token():
        data = _github_json(f"/search/code?q={quote(f'{q} filename:SKILL.md'.strip())}&per_page=30&page={page}")
        items = [
            {
                "repo": it.get("repository", {}).get("full_name", ""),
                "description": it.get("repository", {}).get("description") or "",
                "path": it.get("path", ""),
                "url": it.get("html_url", ""),
                "stars": None,
            }
            for it in data.get("items", [])
        ]
        return {"mode": "code", "total": data.get("total_count", len(items)), "items": items}
    # 搜 readme 会被 awesome 列表淹没，只搜名称、简介和 topic
    terms = f"{q} skill" if q else "writing skill"
    data = _github_json(
        f"/search/repositories?q={quote(terms + ' in:name,description,topics')}&sort=stars&per_page=30&page={page}"
    )
    items = [
        {
            "repo": it.get("full_name", ""),
            "description": it.get("description") or "",
            "path": "",
            "url": it.get("html_url", ""),
            "stars": it.get("stargazers_count", 0),
        }
        for it in data.get("items", [])
    ]
    return {"mode": "repo", "total": data.get("total_count", len(items)), "items": items}


def _check_repo(repo: str) -> str:
    repo = (repo or "").strip().strip("/")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo) or any(s.strip(".") == "" for s in repo.split("/")):
        raise _bad("仓库名应为 owner/name 格式")
    return repo


def list_repo_skills(repo: str) -> dict[str, Any]:
    """列出仓库里所有 SKILL.md；一个都没有时退而列出 .md 文件。"""
    repo = _check_repo(repo)
    info = _github_json(f"/repos/{repo}")
    branch = info.get("default_branch") or "main"
    tree = _github_json(f"/repos/{repo}/git/trees/{quote(branch)}?recursive=1")
    paths = [t["path"] for t in tree.get("tree", []) if t.get("type") == "blob" and isinstance(t.get("path"), str)]
    skill_files = [p for p in paths if p.rsplit("/", 1)[-1].lower() == "skill.md"]
    files = skill_files or [p for p in paths if p.lower().endswith(".md")][:200]
    return {
        "repo": repo,
        "branch": branch,
        "description": info.get("description") or "",
        "only_markdown": not skill_files,
        "files": [{"path": p, "url": f"https://github.com/{repo}/blob/{branch}/{p}"} for p in sorted(files)],
    }


# ---- 链接 ----

def _check_http(url: str) -> str:
    url = (url or "").strip()
    parsed = urlparse(url)
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise _bad("只支持 http 或 https 链接")
    return url


def _is_github(url: str) -> bool:
    return urlparse(url.strip()).netloc.lower() in ("github.com", "www.github.com", "raw.githubusercontent.com")


def raw_url(url: str) -> tuple[str, str]:
    """把 GitHub 页面链接换成原始文件地址；返回 (raw 地址, 建议的 Skill 名)。"""
    url = _check_http(url)
    p = urlparse(url)
    parts = [s for s in p.path.split("/") if s]
    if p.netloc in ("github.com", "www.github.com") and len(parts) >= 2:
        owner, repo = parts[0], parts[1]
        if len(parts) >= 5 and parts[2] in ("blob", "raw"):
            path = "/".join(parts[4:])
            return f"{RAW_HOST}/{owner}/{repo}/{parts[3]}/{path}", _name_from_path(path, repo)
        if len(parts) >= 4 and parts[2] == "tree":
            folder = "/".join(parts[4:])
            path = f"{folder}/SKILL.md" if folder else "SKILL.md"
            return f"{RAW_HOST}/{owner}/{repo}/{parts[3]}/{path}", _name_from_path(path, repo)
        if len(parts) == 2:
            branch = _github_json(f"/repos/{owner}/{repo}").get("default_branch") or "main"
            return f"{RAW_HOST}/{owner}/{repo}/{branch}/SKILL.md", repo
    if p.netloc == "raw.githubusercontent.com" and len(parts) >= 4:
        return url, _name_from_path("/".join(parts[3:]), parts[1])
    return url, _name_from_path(p.path, p.netloc)


def _name_from_path(path: str, fallback: str) -> str:
    segs = [s for s in path.split("/") if s]
    if segs and segs[-1].lower() == "skill.md":
        segs = segs[:-1]
    if not segs:
        return fallback
    last = segs[-1]
    return re.sub(r"\.(md|markdown|txt)$", "", last, flags=re.IGNORECASE) or fallback


def fetch_skill_text(url: str) -> tuple[str, str, str]:
    """下载一个 SKILL.md / Markdown 文件；返回 (文本, 建议名, 原始链接)。"""
    src, name = raw_url(url)
    r = _get(src, {"User-Agent": "novel-gm"})
    if r.status == 404:
        raise _bad("找不到这个文件；如果是仓库或文件夹链接，请确认里面有 SKILL.md", "NOT_FOUND", 404)
    if r.status >= 400:
        raise _bad(f"下载失败：HTTP {r.status}", "FETCH_FAILED", 502)
    if "html" in r.content_type.lower() and "<html" in r.text[:2000].lower():
        raise _bad("这个链接是网页而不是 Markdown 文件；网页文章请用「AI 提炼」", "NOT_MARKDOWN")
    return r.text, name, url.strip()


def preview(url: str) -> dict[str, Any]:
    text, name, src = fetch_skill_text(url)
    meta, body = skills.parse_skill_md(text)
    return {
        "name": meta.get("name") or name,
        "description": meta.get("description", ""),
        "chars": len(body),
        "too_long": len(body) > MAX_IMPORT_CHARS,
        "text": text[:20_000],
        "url": src,
    }


def import_url(url: str) -> skills.Skill:
    text, name, src = fetch_skill_text(url)
    meta, body = skills.parse_skill_md(text)
    if len(body) > MAX_IMPORT_CHARS:
        raise _bad(
            f"这个 Skill 有 {len(body)} 字，直接导入会占用大量上下文；建议改用「AI 提炼」生成精简版",
            "TOO_LONG",
        )
    data = skills.SkillInput(
        name=meta.get("name") or name,
        description=meta.get("description", ""),
        applies_to=meta.get("applies_to") if meta.get("applies_to") in skills.APPLIES else "both",
        scenes=skills.normalize_scenes(re.split(r"[,，、\s]+", meta.get("scenes", ""))),
        instructions=body,
        source=meta.get("source") or src,
    )
    return skills.create_skill(data)


# ---- 文章正文 ----

def extract_text(html: str) -> tuple[str, str]:
    """从网页里取 (标题, 正文)。优先 <article>，否则取段落最多的容器。"""
    from lxml import etree
    from lxml import html as lhtml

    try:
        doc = lhtml.fromstring(html)
    except (ValueError, etree.LxmlError):
        return "", ""
    title = (doc.findtext(".//title") or "").strip()
    for bad in doc.xpath("//script|//style|//noscript|//nav|//header|//footer|//aside|//form|//iframe|//svg"):
        bad.drop_tree()
    # 每个段落给父元素记全分、祖父元素记 0.6 分，得分最高的就是正文容器（正文分成两个以上 section 时祖父胜出）
    scores: dict[Any, float] = {}
    for p in doc.iter("p"):
        n = len(p.text_content().strip())
        if n < 20:
            continue
        parent = p.getparent()
        if parent is None:
            continue
        scores[parent] = scores.get(parent, 0) + n
        grand = parent.getparent()
        if grand is not None:
            scores[grand] = scores.get(grand, 0) + n * 0.6
    articles = doc.xpath("//article")
    if articles:
        root = max(articles, key=lambda e: len(e.text_content()))
    elif scores:
        root = max(scores, key=lambda e: scores[e])
    else:
        root = doc
    blocks: list[str] = []
    for el in root.iter("h1", "h2", "h3", "h4", "p", "li", "blockquote", "pre"):
        t = " ".join(el.text_content().split())
        if t:
            blocks.append(t)
    text = "\n".join(blocks) if blocks else " ".join(root.text_content().split())
    return title, text


def fetch_article(url: str) -> tuple[str, str]:
    url = _check_http(url)
    host = urlparse(url).netloc.lower()
    walled = any(host == h or host.endswith("." + h) for h in _LOGIN_WALLED)
    r = _get(url, {"User-Agent": _BROWSER_UA, "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8"})
    if r.status >= 400 or walled:
        if walled or r.status in (401, 403):
            raise _bad("这个网站需要登录或禁止抓取，读不到正文；请复制文章内容，改用「粘贴文本」", "LOGIN_REQUIRED")
        raise _bad(f"下载失败：HTTP {r.status}", "FETCH_FAILED", 502)
    ctype = r.content_type.lower()
    if "html" in ctype or "<html" in r.text[:2000].lower():
        title, text = extract_text(r.text)
    else:
        title, text = "", r.text
    if len(text.strip()) < 200:
        raise _bad("没能从这个网页读到足够的正文（可能需要登录或由脚本加载）；请复制文章内容，改用「粘贴文本」", "LOGIN_REQUIRED")
    return title, text


# ---- AI 提炼 ----

DISTILL_PROMPT = """把下面的参考资料提炼成一个给 AI 小说写手用的写作 Skill。

要求：
- 只提炼能直接用于写小说（规划章节或写正文）的技法，忽略广告、作者介绍、与写作无关的内容。
- instructions 用中文，写成可以直接执行的要点（以「- 」开头），具体到动作：该怎么写、不要怎么写，必要时给一句很短的例子。
- 300 到 1200 字。不要大段照抄原文，不要写成读后感。
- applies_to 只能是 plan（只影响章节规划）、generate（只影响写正文）或 both。
- scenes 只在技法明显只适用于某类戏份时才填，可选值：{scenes}；通用技法填 []。
{hint}
只输出一个 JSON 对象：{{"name": str, "description": str, "applies_to": str, "scenes": [str], "instructions": str}}
name 不超过 12 个字，description 一句话说明用途。

参考资料{title}：
{text}"""


def distill(url: str = "", text: str = "", hint: str = "") -> dict[str, Any]:
    from .providers import default_provider, model_for, thinking_for
    from .repository import add_usage

    title = ""
    source = ""
    if url.strip():
        if _is_github(url):
            text, title, _ = fetch_skill_text(url)
        else:
            title, text = fetch_article(url)
        source = url.strip()
    text = (text or "").strip()
    if len(text) < 50:
        raise _bad("参考内容太短，至少需要 50 个字", "TOO_SHORT")
    truncated = len(text) > MAX_DISTILL_CHARS
    text = text[:MAX_DISTILL_CHARS]

    provider, provider_id = default_provider()
    model = model_for("skill_distill", provider_id)
    prompt = DISTILL_PROMPT.format(
        scenes="、".join(skills.SCENES),
        hint=f"- 作者的额外要求：{hint.strip()}" if hint.strip() else "",
        title=f"《{title}》" if title else "",
        text=text,
    )
    resp = provider.chat({
        "model": model,
        "messages": [
            {"role": "system", "content": "你是小说写作教练，擅长把写作资料整理成简洁、可执行的写作指令。"},
            {"role": "user", "content": prompt},
        ],
        "temperature": 0.3,
        "max_tokens": 4096,
        "json_mode": True,
        "thinking": thinking_for(provider_id),
        "purpose": "skill_distill",
    })
    add_usage(
        None,
        getattr(provider, "name", provider_id),
        model,
        resp.get("input_tokens", 0),
        resp.get("output_tokens", 0),
        resp.get("cached_tokens", 0),
        resp.get("cost_usd", 0),
        resp.get("latency_ms", 0),
        "skill_distill",
    )
    try:
        data = loads_lenient(resp.get("content", ""))
    except ValueError:
        data = None
    if not isinstance(data, dict) or not str(data.get("instructions", "")).strip():
        raise _bad("模型没有返回可用的 Skill，请重试或换一篇资料", "DISTILL_FAILED", 502)
    applies = str(data.get("applies_to", "both")).strip()
    scenes = data.get("scenes") if isinstance(data.get("scenes"), list) else []
    return {
        "name": str(data.get("name") or title or "新 Skill").strip()[:40],
        "description": str(data.get("description", "")).strip(),
        "applies_to": applies if applies in skills.APPLIES else "both",
        "scenes": skills.normalize_scenes(scenes),
        "instructions": str(data["instructions"]).strip(),
        "source": source,
        "title": title,
        "truncated": truncated,
    }
