"""把块模型写成 Markdown / XHTML，并给出与写作面排印对齐的 EPUB 样式表。"""

from __future__ import annotations

import re
from dataclasses import replace
from xml.sax.saxutils import escape

from .rich import Block, Run

# 与正文区（BodyEditor）一致：行距 1.95（= 格式栏「行距 1.5」× 1.3）、首行缩进 2 字、行宽 760px
LINE_HEIGHT = 1.95
TEXT_INDENT_EM = 2
MEASURE_PX = 760
PARAGRAPH_MARGIN_EM = 0.6

# 主题字体拿不到时的兜底：英文衬线 + 中文宋体
DEFAULT_FONTS = ("Cambria", "SimSun", "宋体")

_FONT_NAME = re.compile(r"[A-Za-z0-9 _\-\u4e00-\u9fff]{1,64}")
# Markdown 里会改变结构的字符，正文出现时要转义
_MD_ESCAPE = "\\`*_[]"


def font_names(raw: str | None) -> list[str]:
    """把主题的正文字体串拆成字体名列表。

    字体名会被写进样式表 / 文档，所以只接受字母、数字、空格、连字符、下划线和汉字，
    其余（引号、分号、花括号等）一律丢掉，避免拼接出可注入的内容。
    """
    names: list[str] = []
    for part in (raw or "").split(","):
        name = part.strip().strip("\"'")
        if name and _FONT_NAME.fullmatch(name) and name not in names:
            names.append(name)
    return names or list(DEFAULT_FONTS)


def css_font_stack(raw: str | None) -> str:
    """把主题的正文字体串转成 CSS 字体栈。"""
    return ", ".join(f'"{n}"' for n in font_names(raw)) + ", serif"


def epub_stylesheet(font: str | None) -> str:
    """EPUB 的样式表，数值全部取自正文区的排印。"""
    return f"""/* novel-gm：与写作面的正文排印对齐 */
html {{
  font-size: 100%;
}}
body {{
  font-family: {css_font_stack(font)};
  font-size: 1em;
  line-height: {LINE_HEIGHT};
  max-width: {MEASURE_PX}px;
  margin: 0 auto;
  padding: 0 1em;
}}
h1 {{
  font-size: 1.6em;
  font-weight: 600;
  text-align: center;
  text-indent: 0;
  margin: 0 0 1.6em;
}}
h2 {{
  font-size: 1.25em;
  font-weight: 600;
  text-indent: 0;
  margin: 1.6em 0 0.7em;
}}
h3 {{
  font-size: 1.1em;
  font-weight: 600;
  text-indent: 0;
  margin: 1.3em 0 0.6em;
}}
p {{
  text-indent: {TEXT_INDENT_EM}em;
  margin: 0 0 {PARAGRAPH_MARGIN_EM}em;
}}
blockquote {{
  margin: 0 0 {PARAGRAPH_MARGIN_EM}em {TEXT_INDENT_EM}em;
  font-style: italic;
}}
blockquote p {{
  text-indent: 0;
}}
"""


def _md_text(text: str) -> str:
    for ch in _MD_ESCAPE:
        text = text.replace(ch, "\\" + ch)
    return text


def _md_inline(runs: list[Run]) -> str:
    parts: list[str] = []
    for run in runs:
        text = _md_text(run.text)
        if not text.strip():
            parts.append(text)
            continue
        if run.bold:
            text = f"**{text}**"
        if run.italic:
            text = f"*{text}*"
        if run.underline:
            text = f"<u>{text}</u>"
        parts.append(text)
    return "".join(parts)


def blocks_to_markdown(blocks: list[Block]) -> str:
    out: list[str] = []
    for block in blocks:
        if block.kind == "h1":
            out.append(f"### {_md_inline(_as_heading(block.runs))}")
        elif block.kind == "h2":
            out.append(f"#### {_md_inline(_as_heading(block.runs))}")
        elif block.kind == "quote":
            out.append(f"> {_md_inline(block.runs)}")
        elif block.kind == "caption":
            out.append(f"*{_md_inline(block.runs)}*")
        else:
            out.append(_md_guard(_md_inline(block.runs)))
    return "\n\n".join(out)


def _md_guard(text: str) -> str:
    """正文段落开头的 # > - + 或「1.」会被 Markdown 当成结构，转义掉。"""
    if text[:1] in "#>-+":
        return "\\" + text
    if re.match(r"^\d+\.", text):
        return text.replace(".", "\\.", 1)
    return text


def _as_heading(runs: list[Run]) -> list[Run]:
    """标题天生就是粗体，不必再套一层行内加粗。"""
    return [replace(run, bold=False) for run in runs]


def _xhtml_inline(runs: list[Run]) -> str:
    parts: list[str] = []
    for run in runs:
        text = escape(run.text)
        if run.bold:
            text = f"<strong>{text}</strong>"
        if run.italic:
            text = f"<em>{text}</em>"
        if run.underline:
            text = f"<u>{text}</u>"
        parts.append(text)
    return "".join(parts)


def block_to_xhtml(block: Block) -> str:
    if block.kind == "h1":
        return f"<h2>{_xhtml_inline(_as_heading(block.runs))}</h2>"
    if block.kind == "h2":
        return f"<h3>{_xhtml_inline(_as_heading(block.runs))}</h3>"
    body = _xhtml_inline(block.runs)
    if block.kind == "quote":
        return f"<blockquote><p>{body}</p></blockquote>"
    if block.kind == "caption":
        return f"<p><em>{body}</em></p>"
    return f"<p>{body}</p>"


def blocks_to_xhtml(blocks: list[Block]) -> str:
    return "".join(block_to_xhtml(block) for block in blocks)
