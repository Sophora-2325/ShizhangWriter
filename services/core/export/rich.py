"""把写作面的带格式正文（WPF FlowDocument XAML）解析成与界面一致的块模型。

导出要和正文的排印与格式对齐，就必须知道用户到底排了什么：段落、章节内标题、
引用，以及加粗 / 斜体 / 下划线。`body_rich` 为空或解析失败时，调用方回退到纯文本。

只有块级语义在这里判定；具体输出成 Markdown 还是 XHTML 由调用方决定。
"""

from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

# 格式栏的标题预设按磅给字号（Heading1 = 22pt、Heading2 = 16pt），WPF 里 1pt = 96/72 px
_H1_SIZE = 28.0
_H2_SIZE = 20.0
# 引用预设左缩进 24，正文是 0；超过这个值就当引用
_QUOTE_MARGIN = 20.0

_BOLD_WEIGHTS = {
    "bold",
    "semibold",
    "demibold",
    "extrabold",
    "ultrabold",
    "black",
    "heavy",
}

# 容器：继续往下找 Paragraph
_CONTAINERS = {"Section", "FlowDocument", "List", "ListItem"}
# 明确跳过：表格、图片、嵌入控件在纯文本导出里没有对应物
_SKIPPED = {"Table", "TableRow", "TableCell", "Figure", "Floater", "BlockUIContainer"}


@dataclass
class Run:
    """一段连续文字及其行内样式。"""

    text: str
    bold: bool = False
    italic: bool = False
    underline: bool = False
    size: float | None = None


@dataclass
class Block:
    """一个块级元素。kind：p 正文 / h1 章节内一级标题 / h2 章节内二级标题 / quote 引用。"""

    kind: str = "p"
    runs: list[Run] = field(default_factory=list)

    @property
    def text(self) -> str:
        return "".join(r.text for r in self.runs)


@dataclass(frozen=True)
class _Fmt:
    """沿元素树往下累积的行内样式。"""

    bold: bool = False
    italic: bool = False
    underline: bool = False
    size: float | None = None

    def child(self, el: ET.Element) -> "_Fmt":
        attrs = el.attrib
        bold, italic, underline, size = self.bold, self.italic, self.underline, self.size
        if "FontWeight" in attrs:
            bold = _is_bold(attrs["FontWeight"])
        if "FontStyle" in attrs:
            italic = attrs["FontStyle"].strip().lower() in {"italic", "oblique"}
        if "TextDecorations" in attrs:
            underline = "underline" in attrs["TextDecorations"].lower()
        if "FontSize" in attrs:
            try:
                size = float(attrs["FontSize"])
            except ValueError:
                pass
        return _Fmt(bold, italic, underline, size)


def _local(tag: object) -> str:
    return str(tag).rsplit("}", 1)[-1]


def _is_bold(value: str | None) -> bool:
    if not value:
        return False
    text = value.strip().lower()
    if text in _BOLD_WEIGHTS:
        return True
    try:
        return float(text) >= 600
    except ValueError:
        return False


def _number(value: str | None) -> float | None:
    if not value:
        return None
    try:
        return float(value)
    except ValueError:
        return None


def _margin_left(value: str | None) -> float:
    if not value:
        return 0.0
    parts = value.split(",")
    return _number(parts[0].strip()) or 0.0


_TOKEN = re.compile(r"«(?:插图|删):[^»]*»")


def _strip_tokens(text: str) -> str:
    return _TOKEN.sub("", text)


def _collect(el: ET.Element, fmt: _Fmt, runs: list[Run]) -> None:
    """递归收集一段里所有文字，保留每个 Run 的样式。"""
    if el.text:
        runs.append(Run(_strip_tokens(el.text), fmt.bold, fmt.italic, fmt.underline, fmt.size))
    for child in list(el):
        name = _local(child.tag)
        child_fmt = fmt.child(child)
        if name == "LineBreak":
            runs.append(Run(" ", child_fmt.bold, child_fmt.italic, child_fmt.underline, child_fmt.size))
        else:
            _collect(child, child_fmt, runs)
        if child.tail:
            runs.append(Run(_strip_tokens(child.tail), child_fmt.bold, child_fmt.italic, child_fmt.underline, child_fmt.size))


def _explicit_bold(el: ET.Element) -> bool:
    """段落内是否显式设过加粗。

    不能直接用继承到的样式：整篇被选成全粗时根节点会带 FontWeight="Bold"，
    那样每个段落都会被误判成标题。
    """
    if _local(el.tag) == "Bold" or _is_bold(el.attrib.get("FontWeight")):
        return True
    return any(_explicit_bold(child) for child in list(el))


def _max_size(el: ET.Element) -> float | None:
    sizes = [_number(el.attrib.get("FontSize"))]
    sizes.extend(_max_size(child) for child in list(el))
    present = [s for s in sizes if s is not None]
    return max(present) if present else None


def _trim(runs: list[Run]) -> list[Run]:
    """去掉整段首尾的空白，免得行内标记（** / <em>）包住空格而失效。"""
    out = [Run(r.text, r.bold, r.italic, r.underline, r.size) for r in runs if r.text]
    while out and not out[0].text.strip():
        out.pop(0)
    while out and not out[-1].text.strip():
        out.pop()
    if out:
        out[0].text = out[0].text.lstrip()
        out[-1].text = out[-1].text.rstrip()
    return [r for r in out if r.text]


def _block_of(paragraph: ET.Element, fmt: _Fmt) -> Block | None:
    own = fmt.child(paragraph)
    runs: list[Run] = []
    _collect(paragraph, own, runs)
    runs = _trim(runs)
    if not runs:
        return None
    # 插画在正文里存成一行标记，导出时不当成段落文字
    if "".join(r.text for r in runs).strip().startswith("«插图:") and "".join(r.text for r in runs).strip().endswith("»"):
        return None

    kind = "p"
    if _margin_left(paragraph.attrib.get("Margin")) >= _QUOTE_MARGIN:
        kind = "quote"
    elif _explicit_bold(paragraph):
        size = _max_size(paragraph) or own.size or 0.0
        if size >= _H1_SIZE:
            kind = "h1"
        elif size >= _H2_SIZE:
            kind = "h2"
    return Block(kind, runs)


def _is_figure(paragraph: ET.Element, fmt: _Fmt) -> tuple[bool, bool]:
    runs: list[Run] = []
    _collect(paragraph, fmt.child(paragraph), runs)
    text = "".join(r.text for r in runs).strip()
    raw = "".join(r.text for r in _raw_runs(paragraph))
    # _collect 已经剥掉标记，整段只剩空时再看原始文字
    source = raw if raw.strip() else text
    if source.strip().startswith("«插图:") and source.strip().endswith("»"):
        return True, "|cap=1" in source
    return False, False


def _raw_runs(paragraph: ET.Element) -> list[Run]:
    """收集剥标记之前的文字，用来认出整段插图标记。"""
    runs: list[Run] = []

    def walk(el: ET.Element) -> None:
        if el.text:
            runs.append(Run(el.text))
        for child in list(el):
            walk(child)
            if child.tail:
                runs.append(Run(child.tail))

    walk(paragraph)
    return runs


def _walk(el: ET.Element, fmt: _Fmt, out: list[Block], caption_next: list[bool] | None = None) -> None:
    pending = caption_next if caption_next is not None else [False]
    for child in list(el):
        name = _local(child.tag)
        if name == "Paragraph":
            figure, has_caption = _is_figure(child, fmt)
            if figure:
                pending[0] = has_caption
                continue
            block = _block_of(child, fmt)
            if block is not None:
                if pending[0]:
                    block = Block("caption", block.runs)
                    pending[0] = False
                out.append(block)
        elif name in _CONTAINERS:
            _walk(child, fmt.child(child), out, pending)
        elif name in _SKIPPED:
            continue


def parse_rich(xaml: str | None) -> list[Block]:
    """解析 FlowDocument XAML；解析不出来或没有内容时返回空列表，由调用方回退纯文本。"""
    if not xaml or not xaml.strip():
        return []
    try:
        root = ET.fromstring(xaml)
    except ET.ParseError:
        return []
    blocks: list[Block] = []
    _walk(root, _Fmt().child(root), blocks)
    return blocks


def blocks_from_plain(text: str | None) -> list[Block]:
    """纯文本按行分段（写作面里一个 Paragraph 就是一行）。"""
    blocks: list[Block] = []
    for line in (text or "").replace("\r\n", "\n").split("\n"):
        line = line.strip()
        if line:
            blocks.append(Block("p", [Run(line)]))
    return blocks
