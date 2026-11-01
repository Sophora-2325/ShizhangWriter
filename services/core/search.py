"""全书 / 本章纯文本查找。

不做正则：``case_sensitive=False`` 时用 ``casefold()`` 比较；返回命中偏移、上下文与
按「章节顺序 + 命中偏移」全局分页的结果。``draft_text`` 用于覆盖当前编辑章尚未保存的正文。
"""
from __future__ import annotations

from typing import Any, Optional

from fastapi import HTTPException

from .models import ChapterStatus
from .repository import get_project, list_chapters

# 单次请求最多扫描的字符预算；超出后置 truncated，避免超大作品卡死界面
MAX_SCAN_CHARS = 2_000_000
CONTEXT_CHARS = 30
MAX_PAGE_SIZE = 100


def _index_map(text: str, folded: str) -> list[int]:
    """folded 中每个字符对应的原文下标（casefold 可能把一个字符变成多个）。"""
    mapping: list[int] = []
    for index, char in enumerate(text):
        mapping.extend([index] * len(char.casefold()))
    return mapping


def _find_offsets(text: str, query: str, case_sensitive: bool) -> list[tuple[int, int]]:
    if case_sensitive:
        haystack, needle, mapping = text, query, None
    else:
        haystack = text.casefold()
        needle = query.casefold()
        mapping = _index_map(text, haystack) if len(haystack) != len(text) else None
    offsets: list[tuple[int, int]] = []
    step = max(1, len(needle))
    position = haystack.find(needle)
    while position != -1:
        if mapping is None:
            offsets.append((position, position + len(needle)))
        else:
            offsets.append((mapping[position], mapping[position + len(needle) - 1] + 1))
        position = haystack.find(needle, position + step)
    return offsets


def _match(text: str, start: int, end: int, in_draft: bool) -> dict[str, Any]:
    context_start = max(0, start - CONTEXT_CHARS)
    context_end = min(len(text), end + CONTEXT_CHARS)
    return {
        "offset": start,
        "context": text[context_start:context_end],
        "context_offset": start - context_start,
        "in_draft": in_draft,
    }


def search(project_id: str, query: str, *, case_sensitive: bool = False, scope: str = "book",
           chapter_no: Optional[int] = None, draft_text: Optional[str] = None, page: int = 1,
           page_size: int = 50) -> dict[str, Any]:
    """全书/本章查找；空查询报 400 EMPTY_QUERY。

    ``draft_text`` 用 None 表示「客户端没有正在编辑的章」，此时用库里的正文；
    传 ``""`` 表示当前编辑章草稿为空，必须覆盖库里的正文（该章不再命中旧内容）。
    """
    if not (query or "").strip():
        raise HTTPException(400, detail={"code": "EMPTY_QUERY", "message": "请输入要查找的内容"})
    get_project(project_id)  # 作品不存在时保持 404 语义
    if scope not in ("book", "chapter"):
        scope = "book"
    try:
        page = max(1, int(page or 1))
    except (TypeError, ValueError):
        page = 1
    try:
        page_size = int(page_size or 50)
    except (TypeError, ValueError):
        page_size = 50
    page_size = max(1, min(page_size, MAX_PAGE_SIZE))

    chapters = [
        chapter for chapter in list_chapters(project_id)
        if chapter.status != ChapterStatus.superseded
    ]
    chapters.sort(key=lambda chapter: chapter.no)
    if scope == "chapter":
        chapters = [chapter for chapter in chapters if chapter.no == chapter_no]

    use_draft = draft_text is not None and chapter_no is not None
    total = 0
    truncated = False
    remaining = MAX_SCAN_CHARS
    counts: dict[int, int] = {}
    titles: dict[int, str] = {}
    flat: list[tuple[int, dict[str, Any]]] = []

    for chapter in chapters:
        text = chapter.body_text or ""
        in_draft = False
        if use_draft and chapter.no == chapter_no:
            text = draft_text or ""
            in_draft = True
        if remaining <= 0:
            truncated = True
            break
        truncated_here = len(text) > remaining
        scan_text = text[:remaining]
        remaining -= len(scan_text)
        if truncated_here:
            truncated = True
        offsets = _find_offsets(scan_text, query, case_sensitive)
        counts[chapter.no] = len(offsets)
        titles[chapter.no] = chapter.title
        total += len(offsets)
        for start, end in offsets:
            flat.append((chapter.no, _match(text, start, end, in_draft)))
        if truncated_here:
            break

    start_index = (page - 1) * page_size
    selected = flat[start_index:start_index + page_size]
    grouped: dict[int, list[dict[str, Any]]] = {}
    for no, match in selected:
        grouped.setdefault(no, []).append(match)
    result_chapters = [
        {
            "chapter_no": chapter.no,
            "title": titles.get(chapter.no, chapter.title),
            "count": counts.get(chapter.no, 0),
            "matches": grouped[chapter.no],
        }
        for chapter in chapters
        if chapter.no in grouped
    ]
    return {
        "total": total,
        "page": page,
        "page_size": page_size,
        "truncated": truncated,
        "chapters": result_chapters,
    }
