from __future__ import annotations

import zipfile
from datetime import datetime, timezone
from pathlib import Path
from xml.sax.saxutils import escape

from ..models import LORE_CATEGORIES, LORE_CATEGORY_LABELS, Chapter, Project
from ..repository import list_chapters
from .docx import write_docx
from .rich import Block, blocks_from_plain, parse_rich
from .writers import blocks_to_markdown, blocks_to_xhtml
from .writers import epub_stylesheet

__all__ = [
    "chapter_heading",
    "export_bible_appendix",
    "export_docx",
    "export_epub",
    "export_markdown",
    "export_txt",
]


def chapter_heading(chapter: Chapter) -> str:
    """章标题；标题本身就是默认的「第N章」时不要重复输出（预览里也是这么处理的）。"""
    no = f"第{chapter.no}章"
    title = (chapter.title or "").strip()
    return no if not title or title == no else f"{no} {title}"


def _chapters(project: Project) -> list[Chapter]:
    return [c for c in list_chapters(project.id) if c.status.value != "superseded"]


def _blocks(chapter: Chapter) -> list[Block]:
    """带格式正文优先；为空或解析不出来时退回纯文本。"""
    blocks = parse_rich(chapter.body_rich)
    return blocks or blocks_from_plain(chapter.body_text)


def _quote(text: str, prefix: str) -> str:
    return "\n".join(f"{prefix}{line.strip()}" for line in text.splitlines() if line.strip())


def _front_matter(project: Project) -> str:
    """正文前导：背景设定里「世界观」那一组的正文，没有就返回空串（导出时整段跳过）。"""
    lines = [e.content.strip() for e in project.entries if e.category == "world" and e.content.strip()]
    return "\n".join(lines)


def _entry_lines(project: Project) -> list[str]:
    """设定附录用的条目清单，按分类分组。"""
    lines: list[str] = []
    for cat in LORE_CATEGORIES:
        group = [e for e in project.entries if e.category == cat and (e.title.strip() or e.content.strip())]
        if not group:
            continue
        lines.append(f"### {LORE_CATEGORY_LABELS[cat]}\n")
        for e in group:
            head = f"**{e.title.strip()}**：" if e.title.strip() else ""
            hard = "（硬规则）" if cat == "rule" and e.hard else ""
            lines.append(f"- {head}{e.content.strip()}{hard}\n")
    return lines


def export_markdown(project: Project, out_path: Path) -> Path:
    """Markdown 存不了字体和行距，但段落结构与加粗 / 斜体 / 标题 / 引用的语义能保留。"""
    chunks = [f"# {project.title}"]
    premise = _front_matter(project)
    if premise:
        chunks.append(_quote(premise, "> "))
    for c in _chapters(project):
        chunks.append(f"## {chapter_heading(c)}")
        body = blocks_to_markdown(_blocks(c))
        if body:
            chunks.append(body)
    out_path.write_text("\n\n".join(chunks) + "\n", encoding="utf-8")
    return out_path


def export_txt(project: Project, out_path: Path) -> Path:
    """纯文本；带 BOM 和 CRLF，Windows 记事本打开也不乱码、不挤成一行。"""
    chunks = [project.title.strip()]
    premise = _front_matter(project)
    if premise:
        chunks.append(premise)
    for c in _chapters(project):
        body = "\n\n".join(b.text.strip() for b in _blocks(c) if b.text.strip())
        chunks.append(chapter_heading(c) + ("\n\n" + body if body else ""))
    out_path.write_text("\n\n\n".join(chunks) + "\n", encoding="utf-8-sig", newline="\r\n")
    return out_path


def export_docx(project: Project, out_path: Path, font: str | None = None) -> Path:
    chapters = [(chapter_heading(c), _blocks(c)) for c in _chapters(project)]
    return write_docx(out_path, project.title, _front_matter(project), chapters, font)


def export_epub(project: Project, out_path: Path, font: str | None = None) -> Path:
    """EPUB3，不依赖 pandoc。

    `font` 是写作面当前主题的正文字体（如 `Cambria, SimSun, 宋体`），
    写进样式表后电子书里也是同一款字。字号用 1em，交给阅读器决定实际大小。
    """
    chapters = _chapters(project)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    def chapter_xhtml(c: Chapter) -> str:
        heading = escape(chapter_heading(c))
        paras = blocks_to_xhtml(_blocks(c))
        return (
            '<?xml version="1.0" encoding="utf-8"?>'
            '<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="zh-CN" lang="zh-CN">'
            f"<head><title>{heading}</title>"
            '<link rel="stylesheet" type="text/css" href="../style.css"/></head>'
            f"<body><h1>{heading}</h1>{paras}</body></html>"
        )

    container = """<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
"""
    manifest_items = ['<item id="css" href="style.css" media-type="text/css"/>']
    spine = []
    for c in chapters:
        id_ = f"ch{c.no}"
        href = f"chapters/ch{c.no}.xhtml"
        manifest_items.append(
            f'<item id="{id_}" href="{href}" media-type="application/xhtml+xml"/>'
        )
        spine.append(f'<itemref idref="{id_}"/>')

    opf = f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="uid">{escape(project.id)}</dc:identifier>
    <dc:title>{escape(project.title)}</dc:title>
    <dc:language>zh-CN</dc:language>
    <meta property="dcterms:modified">{datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}</meta>
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    {''.join(manifest_items)}
  </manifest>
  <spine>
    {''.join(spine)}
  </spine>
</package>
"""
    nav_lis = "".join(
        f'<li><a href="chapters/ch{c.no}.xhtml">{escape(chapter_heading(c))}</a></li>'
        for c in chapters
    )
    nav = f"""<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"
      xml:lang="zh-CN" lang="zh-CN">
<head><title>目录</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
<body><nav epub:type="toc"><ol>{nav_lis}</ol></nav></body></html>
"""
    with zipfile.ZipFile(out_path, "w", compression=zipfile.ZIP_DEFLATED) as z:
        z.writestr("mimetype", "application/epub+zip", compress_type=zipfile.ZIP_STORED)
        z.writestr("META-INF/container.xml", container)
        z.writestr("OEBPS/content.opf", opf)
        z.writestr("OEBPS/nav.xhtml", nav)
        z.writestr("OEBPS/style.css", epub_stylesheet(font))
        for c in chapters:
            z.writestr(f"OEBPS/chapters/ch{c.no}.xhtml", chapter_xhtml(c))
    return out_path


def export_bible_appendix(project: Project, out_path: Path) -> Path:
    lines = [f"# {project.title} · 设定附录\n", "## 背景设定\n"]
    lines.extend(_entry_lines(project))
    if not project.entries:
        lines.append("（无）\n")
    lines.append("\n## 角色\n")
    for c in project.characters:
        lines.append(
            f"### {c.name}\n"
            f"- 年龄：{c.age}\n"
            f"- 性格：{c.personality}\n"
            f"- 相貌：{c.appearance}\n"
            f"- 着装：{c.outfit}\n"
            f"- 最近所想：{c.recent_thoughts}\n"
            f"- 身份：{c.role}\n"
            f"- 长久目标：{'、'.join(c.goals) if c.goals else '无'}\n"
            f"- 出场原因：{c.join_reason or '无'}\n"
            f"- 秘密：{c.secret or '无'}\n"
        )
    lines.append("## 脉络\n")
    for t in project.threads:
        lines.append(f"- {t.name} ({t.state}): {t.user_directive}\n")
    out_path.write_text("".join(lines), encoding="utf-8")
    return out_path
