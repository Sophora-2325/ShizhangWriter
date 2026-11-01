"""手写 WordprocessingML 生成 .docx，不依赖 python-docx。

只用到最基础的部件：段落样式（正文 / 标题 / 引用）和行内加粗、斜体、下划线，
Word 和 WPS 都能直接打开。
"""

from __future__ import annotations

import re
from dataclasses import replace
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from xml.sax.saxutils import escape

from .rich import Block, Run
from .writers import font_names

# XML 1.0 不允许的控制字符，正文里偶尔会混进来，留着 Word 会拒绝打开
_INVALID_XML = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f]")
_CJK = re.compile(r"[\u4e00-\u9fff]")
_CJK_FONTS = {
    "simsun", "nsimsun", "simhei", "kaiti", "fangsong", "dengxian",
    "microsoft yahei", "microsoft jhenghei", "pmingliu", "mingliu",
}
_W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"

_CONTENT_TYPES = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
  <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
</Types>"""

_ROOT_RELS = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
</Relationships>"""

_DOC_RELS = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>"""


def _x(text: str) -> str:
    return escape(_INVALID_XML.sub("", text))


def _fonts(raw: str | None) -> tuple[str, str]:
    """（西文字体, 中文字体）；主题字体里找不到中文字体时用宋体。"""
    names = font_names(raw)
    east = next((n for n in names if _CJK.search(n) or n.lower() in _CJK_FONTS), "SimSun")
    return names[0], east


def _styles(raw_font: str | None) -> str:
    latin, east = _fonts(raw_font)
    fonts = f'<w:rFonts w:ascii="{_x(latin)}" w:hAnsi="{_x(latin)}" w:eastAsia="{_x(east)}" w:cs="{_x(latin)}"/>'

    # pPr 子元素必须按 schema 顺序：keepNext, pageBreakBefore, spacing, ind, jc, outlineLvl
    def heading(style_id: str, name: str, size: int, level: int, chapter: bool = False) -> str:
        page_break = "<w:pageBreakBefore/>" if chapter else ""
        center = '<w:jc w:val="center"/>' if chapter else ""
        return (
            f'<w:style w:type="paragraph" w:styleId="{style_id}"><w:name w:val="{name}"/>'
            '<w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>'
            f'<w:pPr><w:keepNext/>{page_break}<w:spacing w:before="240" w:after="240"/>'
            f'<w:ind w:firstLineChars="0" w:firstLine="0"/>{center}<w:outlineLvl w:val="{level}"/></w:pPr>'
            f'<w:rPr><w:b/><w:sz w:val="{size}"/><w:szCs w:val="{size}"/></w:rPr></w:style>'
        )

    return (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        f'<w:styles xmlns:w="{_W}">'
        f'<w:docDefaults><w:rPrDefault><w:rPr>{fonts}<w:sz w:val="24"/><w:szCs w:val="24"/>'
        '<w:lang w:val="zh-CN" w:eastAsia="zh-CN"/></w:rPr></w:rPrDefault></w:docDefaults>'
        '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/>'
        '<w:pPr><w:spacing w:after="120" w:line="360" w:lineRule="auto"/>'
        '<w:ind w:firstLineChars="200" w:firstLine="480"/><w:jc w:val="both"/></w:pPr></w:style>'
        '<w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/>'
        '<w:next w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:before="480" w:after="480"/>'
        '<w:ind w:firstLineChars="0" w:firstLine="0"/><w:jc w:val="center"/></w:pPr>'
        '<w:rPr><w:b/><w:sz w:val="44"/><w:szCs w:val="44"/></w:rPr></w:style>'
        + heading("Heading1", "heading 1", 32, 0, chapter=True)
        + heading("Heading2", "heading 2", 28, 1)
        + heading("Heading3", "heading 3", 26, 2)
        + '<w:style w:type="paragraph" w:styleId="Quote"><w:name w:val="Quote"/><w:basedOn w:val="Normal"/>'
        '<w:qFormat/><w:pPr><w:ind w:left="480" w:firstLineChars="0" w:firstLine="0"/></w:pPr>'
        '<w:rPr><w:i/></w:rPr></w:style>'
        "</w:styles>"
    )


def _run(run: Run) -> str:
    if not run.text:
        return ""
    props = ("<w:b/>" if run.bold else "") + ("<w:i/>" if run.italic else "") + (
        '<w:u w:val="single"/>' if run.underline else ""
    )
    rpr = f"<w:rPr>{props}</w:rPr>" if props else ""
    return f'<w:r>{rpr}<w:t xml:space="preserve">{_x(run.text)}</w:t></w:r>'


def _para(runs: list[Run], style: str | None = None) -> str:
    ppr = f'<w:pPr><w:pStyle w:val="{style}"/></w:pPr>' if style else ""
    return f"<w:p>{ppr}{''.join(_run(r) for r in runs)}</w:p>"


_BLOCK_STYLE = {"p": None, "h1": "Heading2", "h2": "Heading3", "quote": "Quote", "caption": None}


def _block(block: Block) -> str:
    runs = block.runs
    if block.kind == "caption":
        runs = [replace(run, italic=True) for run in runs]
        ppr = '<w:pPr><w:jc w:val="center"/></w:pPr>'
        return f"<w:p>{ppr}{''.join(_run(r) for r in runs)}</w:p>"
    return _para(runs, _BLOCK_STYLE.get(block.kind))


def write_docx(
    out_path: Path,
    title: str,
    front_matter: str,
    chapters: list[tuple[str, list[Block]]],
    font: str | None = None,
) -> Path:
    """chapters 是（章标题, 正文块）列表；front_matter 是正文前导（背景设定的世界观一组）。"""
    body = [_para([Run(title)], "Title")]
    for line in (front_matter or "").splitlines():
        if line.strip():
            body.append(_para([Run(line.strip())], "Quote"))
    for heading, blocks in chapters:
        body.append(_para([Run(heading)], "Heading1"))
        body.extend(_block(b) for b in blocks)
    document = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        f'<w:document xmlns:w="{_W}"><w:body>{"".join(body)}'
        '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/>'
        '<w:pgMar w:top="1440" w:right="1800" w:bottom="1440" w:left="1800" w:header="851" w:footer="992" w:gutter="0"/>'
        "</w:sectPr></w:body></w:document>"
    )
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    core = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" '
        'xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">'
        f"<dc:title>{_x(title)}</dc:title>"
        f'<dcterms:created xsi:type="dcterms:W3CDTF">{now}</dcterms:created>'
        f'<dcterms:modified xsi:type="dcterms:W3CDTF">{now}</dcterms:modified>'
        "</cp:coreProperties>"
    )
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out_path, "w", compression=zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", _CONTENT_TYPES)
        z.writestr("_rels/.rels", _ROOT_RELS)
        z.writestr("word/_rels/document.xml.rels", _DOC_RELS)
        z.writestr("word/document.xml", document)
        z.writestr("word/styles.xml", _styles(font))
        z.writestr("docProps/core.xml", core)
    return out_path
