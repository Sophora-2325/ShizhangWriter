"""把作品整理成各平台可直接提交的发布包。

半自动的分工：本模块只负责「文本 + 平台规则」，产出平台适配的正文文件与一份
`发布信息.txt`；登录、提交、审核仍然由作者在平台后台上完成。这样就可以 pytest，
和 `export/` 是同一层的东西。

正文来源和导出完全一致：带格式正文（`body_rich`，FlowDocument XAML）优先，
解析不出来时退回 `body_text` 纯文本，段落语义与写作面一致。
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Sequence
from xml.sax.saxutils import escape

from .. import workspace as ws
from ..db import get_storage
from ..export import chapter_heading
from ..export.rich import Block, blocks_from_plain, parse_rich
from ..export.writers import block_to_xhtml, blocks_to_markdown
from ..models import Chapter, Project
from ..repository import list_chapters
from .platforms import (
    FORMAT_HTML,
    FORMAT_MD,
    INDENT_FULL,
    INDENT_NONE,
    PLATFORMS,
    PLATFORM_GROUPS,
    PlatformSpec,
    get as get_platform,
)

__all__ = [
    "INDENT_FULL",
    "INDENT_NONE",
    "PUBLISH_DIR",
    "RenderOptions",
    "default_synopsis",
    "platform_table",
    "prepare",
    "render_html",
    "render_markdown",
    "render_plain",
    "scope_chapters",
]

PUBLISH_DIR = "发布"
TXT_NAME = "正文.txt"
MD_NAME = "正文.md"
HTML_NAME = "正文.html"
INFO_NAME = "发布信息.txt"

# 剪贴板负载的上限：正文长到这个量级（约 200 万字节）就不走剪贴板，让用户用文件
CLIP_LIMIT = 500_000

SCOPE_CHAPTER = "chapter"
SCOPE_BOOK = "book"

# 中文段落首行缩进：两个全角空格，和「首行缩进 2 字」在视觉上等价
FULL_SPACE = "\u3000"


@dataclass(frozen=True)
class RenderOptions:
    """一次排版要用的全部选项；默认值 = 平台默认。"""

    title: str = ""
    author: str = ""
    synopsis: str = ""
    indent: str = INDENT_NONE
    with_head: bool = False


# ---------------------------------------------------------------- 取正文


def default_synopsis(project: Project) -> str:
    """简介默认取背景设定里「世界观」那一组的正文，和导出时的前导同源。"""
    return "\n".join(
        e.content.strip() for e in project.entries if e.category == "world" and e.content.strip()
    )


def split_scope(scope: str) -> str:
    return SCOPE_BOOK if (scope or "").strip().lower() == SCOPE_BOOK else SCOPE_CHAPTER


def scope_chapters(project: Project, scope: str, chapter_no: int = 0) -> list[Chapter]:
    """本次要发布的章节；全书跳过已作废章节（与导出同语义）。"""
    chapters = list_chapters(project.id)
    if split_scope(scope) == SCOPE_BOOK:
        return [c for c in chapters if c.status.value != "superseded"]
    # 这里不用 repository.get_chapter：它找不到时抛 404，而「章号不存在」是一次请求写错了，
    # 应该按 400 回给客户端，消息也能是中文。
    wanted = int(chapter_no or 0)
    chapter = next((c for c in chapters if c.no == wanted), None)
    if chapter is None:
        raise ws.bad_request(f"找不到第 {chapter_no} 章", "NO_CHAPTER")
    return [chapter]


def _blocks(chapter: Chapter) -> list[Block]:
    blocks = parse_rich(chapter.body_rich)
    return blocks or blocks_from_plain(chapter.body_text)


def _chars(chapters: Sequence[Chapter]) -> int:
    return sum(len(b.text.strip()) for c in chapters for b in _blocks(c))


# ---------------------------------------------------------------- 排版


def _indent_one(text: str, indent: str) -> str:
    return FULL_SPACE * 2 + text if indent == INDENT_FULL else text


def _plain_body(chapters: Sequence[Chapter], indent: str) -> list[str]:
    """每章一段纯文本；段间空一行，章标题独立成行。"""
    out: list[str] = []
    for chapter in chapters:
        body = "\n\n".join(
            _indent_one(b.text.strip(), indent) for b in _blocks(chapter) if b.text.strip()
        )
        out.append(chapter_heading(chapter) + ("\n\n" + body if body else ""))
    return out


def render_plain(chapters: Sequence[Chapter], opt: RenderOptions) -> str:
    """纯文本发布包。utf-8-sig + CRLF 由调用方在写文件时落盘，这里只管内容。"""
    chunks: list[str] = []
    if opt.with_head:
        head = _head_lines(opt)
        if head:
            chunks.append("\n".join(head))
    chunks.extend(_plain_body(chapters, opt.indent))
    return "\n\n\n".join(chunks) + "\n"


def render_markdown(chapters: Sequence[Chapter], opt: RenderOptions) -> str:
    chunks: list[str] = []
    if opt.with_head:
        if opt.title:
            chunks.append(f"# {opt.title}")
        if opt.author:
            chunks.append(f"作者：{opt.author}")
        if opt.synopsis:
            quoted = "\n".join(f"> {line.strip()}" for line in opt.synopsis.splitlines() if line.strip())
            if quoted:
                chunks.append(quoted)
    for chapter in chapters:
        chunks.append(f"## {chapter_heading(chapter)}")
        body = "\n\n".join(
            _indent_one(blocks_to_markdown([b]), opt.indent)
            for b in _blocks(chapter)
            if b.text.strip()
        )
        if body:
            chunks.append(body)
    return "\n\n".join(chunks) + "\n"


def render_html(chapters: Sequence[Chapter], opt: RenderOptions) -> str:
    """HTML 片段（不带 <html> 外壳）：既能粘进富文本编辑器，也能包一层存成文件。"""
    parts: list[str] = []
    if opt.with_head:
        if opt.title:
            parts.append(f"<h1>{escape(opt.title)}</h1>")
        if opt.author:
            parts.append(f"<p>作者：{escape(opt.author)}</p>")
        if opt.synopsis:
            lines = "<br/>".join(escape(line.strip()) for line in opt.synopsis.splitlines() if line.strip())
            if lines:
                parts.append(f"<blockquote><p>{lines}</p></blockquote>")
    for chapter in chapters:
        parts.append(f"<h1>{escape(chapter_heading(chapter))}</h1>")
        for block in _blocks(chapter):
            html = block_to_xhtml(block)
            if opt.indent == INDENT_FULL and block.kind == "p" and html.startswith("<p>"):
                html = "<p>" + FULL_SPACE * 2 + html[3:]  # 3 = len("<p>")
            parts.append(html)
    return "".join(parts)


def html_document(fragment: str, title: str) -> str:
    return (
        '<!DOCTYPE html>\n<html lang="zh-CN">\n<head>\n<meta charset="utf-8"/>\n'
        f"<title>{escape(title)}</title>\n</head>\n<body>\n{fragment}\n</body>\n</html>\n"
    )


def _document_title(project: Project, chapters: Sequence[Chapter]) -> str:
    """单章时标题带章名，整本时只留书名。"""
    if len(chapters) == 1:
        return f"{project.title} · {chapter_heading(chapters[0])}"
    return project.title


def _head_lines(opt: RenderOptions) -> list[str]:
    out = [opt.title] if opt.title else []
    if opt.author:
        out.append(f"作者：{opt.author}")
    if opt.synopsis:
        out.append("简介：")
        out.extend(line.strip() for line in opt.synopsis.splitlines() if line.strip())
    return out


# ---------------------------------------------------------------- 写盘


def _target_file(spec: PlatformSpec) -> tuple[str, str]:
    """返回 (正文文件名, 落盘编码参数)：txt 带 BOM，保证书站导入器不乱码。"""
    if spec.format == FORMAT_MD:
        return MD_NAME, "utf-8"
    if spec.format == FORMAT_HTML:
        return HTML_NAME, "utf-8"
    return TXT_NAME, "utf-8-sig"


def _write(path: Path, text: str, encoding: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if encoding == "utf-8-sig":
        # 书站的导入器在记事本时代长大，BOM + CRLF 是最不容易被吃掉换行的组合
        path.write_text(text, encoding=encoding, newline="\r\n")
    else:
        path.write_text(text, encoding=encoding, newline="\n")


def _info_text(
    project: Project,
    spec: PlatformSpec,
    opt: RenderOptions,
    chapters: Sequence[Chapter],
    *,
    scope_key: str,
    root_missing: bool,
) -> str:
    scope_line = (
        f"全书（{len(chapters)} 章 / {_chars(chapters)} 字）"
        if scope_key == SCOPE_BOOK
        else f"本章（{chapter_heading(chapters[0])} / {_chars(chapters)} 字）"
    )
    lines = [
        f"# {project.title} · 发布包",
        "",
        f"平台：{spec.name}",
        f"范围：{scope_line}",
        f"生成时间：{datetime.now().strftime('%Y-%m-%d %H:%M')}",
        f"段首缩进：{'全角两格（自带）' if opt.indent == INDENT_FULL else '不加（交给平台编辑器）'}",
    ]
    if opt.author:
        lines.append(f"作者：{opt.author}")
    if project.genre:
        lines.append(f"分类：{'、'.join(project.genre)}")
    if project.tone:
        lines.append(f"基调：{project.tone}")
    if project.audience:
        lines.append(f"受众：{project.audience}")
    if root_missing:
        lines.append("")
        lines.append("注意：该作品还没有作品文件夹，发布包写在了数据目录里。")
    if opt.synopsis:
        lines += ["", "## 简介", "", opt.synopsis.strip()]
    lines += ["", "## 章节", ""]
    for chapter in chapters:
        count = chapter.word_count or len(chapter.body_text or "")
        lines.append(f"- {chapter_heading(chapter)}（{count} 字）")
    lines += ["", "## 该平台要求速查", ""]
    lines += [f"- {note}" for note in spec.notes]
    lines += [
        "",
        "## 后台地址",
        "",
        spec.backend_url,
        f"（打不开时可以试 {spec.homepage}，或在浏览器里搜「{spec.name} 作家专区」）",
        "",
    ]
    return "\n".join(lines)


def _item_payload(spec: PlatformSpec, out_dir: Path, files: list[str], chars: int) -> dict[str, Any]:
    return {
        "key": spec.key,
        "name": spec.name,
        "group": spec.group,
        "format": spec.format,
        "backend_url": spec.backend_url,
        "homepage": spec.homepage,
        "notes": list(spec.notes),
        "unverified": spec.unverified,
        "dir": str(out_dir),
        "files": files,
        "chars": chars,
        "empty": chars == 0,
    }


def prepare(
    project: Project,
    *,
    platforms: Sequence[str],
    scope: str = SCOPE_CHAPTER,
    chapter_no: int = 0,
    author: str = "",
    synopsis: str = "",
    indent: str = "",
    with_head: bool | None = None,
    write_files: bool = True,
) -> dict[str, Any]:
    """生成发布包并返回落盘结果 + 剪贴板负载。

    `indent` 传空串表示用平台默认；`with_head` 传 None 表示用平台默认。
    已存在的文件会被覆盖（同一个平台反复生成只会得到同一个路径）。
    """
    keys = [k for k in dict.fromkeys(platforms or []) if k]
    if not keys:
        raise ws.bad_request("请至少选择一个平台", "NO_PLATFORM")
    unknown = [k for k in keys if get_platform(k) is None]
    if unknown:
        legal = "、".join(p.key for p in PLATFORMS)
        raise ws.bad_request(f"未知的平台：{'、'.join(unknown)}；可选：{legal}", "BAD_PLATFORM")

    scope_key = split_scope(scope)
    chapters = scope_chapters(project, scope_key, chapter_no)
    chars = _chars(chapters)

    root = ws.check_root(ws.root_of(project.id))
    root_missing = root is None
    out_root = (
        root / ws.EXPORT_DIR / PUBLISH_DIR
        if root is not None
        else get_storage() / "projects" / project.id / "publish"
    )

    synopsis_text = (synopsis or "").strip() or default_synopsis(project)
    author_text = (author or "").strip()

    items: list[dict[str, Any]] = []
    plain_cache: dict[tuple[str, bool], str] = {}
    html_cache: dict[tuple[str, bool], str] = {}
    # 剪贴板只跟缩进/抬头有关，跟平台无关；同一批里只留一份就够
    clip_plain = ""
    clip_html = ""

    for key in keys:
        spec = get_platform(key)
        assert spec is not None  # 上面已经拦过未知 key
        opt = RenderOptions(
            title=project.title,
            author=author_text,
            synopsis=synopsis_text,
            indent=indent if indent in (INDENT_FULL, INDENT_NONE) else spec.indent,
            with_head=spec.with_head if with_head is None else bool(with_head),
        )
        cache_key = (opt.indent, opt.with_head)
        if cache_key not in plain_cache:
            plain_cache[cache_key] = render_plain(chapters, opt)
            html_cache[cache_key] = render_html(chapters, opt)
        plain = plain_cache[cache_key]
        html = html_cache[cache_key]
        if not clip_plain:
            clip_plain, clip_html = plain, html

        out_dir = out_root / ws.sanitize_name(spec.name, spec.key)
        files: list[str] = []
        if write_files:
            file_name, encoding = _target_file(spec)
            content = plain
            if spec.format == FORMAT_MD:
                content = render_markdown(chapters, opt)
            elif spec.format == FORMAT_HTML:
                content = html_document(html, _document_title(project, chapters))
            body_path = out_dir / file_name
            _write(body_path, content, encoding)
            files.append(str(body_path))
            info_path = out_dir / INFO_NAME
            _write(
                info_path,
                _info_text(project, spec, opt, chapters, scope_key=scope_key, root_missing=root_missing),
                "utf-8",
            )
            files.append(str(info_path))

        items.append(_item_payload(spec, out_dir, files, chars))

    truncated = len(clip_plain) > CLIP_LIMIT or len(clip_html) > CLIP_LIMIT
    return {
        "items": items,
        "scope": scope_key,
        "chapter_no": chapter_no,
        "chapters": len(chapters),
        "chars": chars,
        "root_missing": root_missing,
        "out_root": str(out_root),
        "preview": {
            "text": clip_plain[:CLIP_LIMIT],
            "html": clip_html[:CLIP_LIMIT],
            "chars": chars,
            "truncated": truncated,
        },
    }


def platform_table() -> dict[str, Any]:
    """下发给客户端的平台表，客户端不硬编码任何平台。"""
    return {
        "groups": [{"key": k, "name": n, "hint": h} for k, n, h in PLATFORM_GROUPS],
        "platforms": [
            {
                "key": p.key,
                "name": p.name,
                "group": p.group,
                "backend_url": p.backend_url,
                "homepage": p.homepage,
                "format": p.format,
                "indent": p.indent,
                "min_words": p.min_words,
                "batch_import": p.batch_import,
                "unverified": p.unverified,
                "notes": list(p.notes),
            }
            for p in PLATFORMS
        ],
    }
