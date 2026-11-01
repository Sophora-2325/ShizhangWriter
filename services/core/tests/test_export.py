from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db

from services.core.export import chapter_heading
from services.core.export.rich import blocks_from_plain, parse_rich
from services.core.export.writers import (
    blocks_to_markdown,
    blocks_to_xhtml,
    css_font_stack,
    epub_stylesheet,
)
from services.core.main import app
from services.core.models import Chapter, LoreEntry, ProjectCreate
from services.core.repository import create_project, upsert_chapter

client = TestClient(app)

# 一份典型的带格式正文：普通段落里有加粗和斜体，第二段是章节内一级标题，第三段是引用
RICH = (
    '<Section xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xml:space="preserve" '
    'FontFamily="Microsoft YaHei" FontStyle="Normal" FontWeight="Normal" FontSize="16" '
    'Foreground="#FF262626" LineHeight="Auto">'
    '<Paragraph TextIndent="34" Margin="0,0,0,8"><Run>第一段</Run>'
    '<Run FontWeight="Bold">加粗</Run><Run FontStyle="Italic">斜体</Run></Paragraph>'
    '<Paragraph TextIndent="0" Margin="0,8,0,12"><Run FontWeight="Bold" FontSize="29.3333">章内标题</Run></Paragraph>'
    '<Paragraph TextIndent="0" Margin="24,4,0,8"><Run>引用一句</Run></Paragraph>'
    "</Section>"
)


def _ch(no: int, title: str = "") -> Chapter:
    return Chapter(no=no, title=title or f"第{no}章")


# ---------------------------------------------------------------- 解析


def test_parse_rich_reads_kinds_and_inline_styles():
    blocks = parse_rich(RICH)
    assert [b.kind for b in blocks] == ["p", "h1", "quote"]

    first = blocks[0]
    assert first.text == "第一段加粗斜体"
    assert [r.bold for r in first.runs] == [False, True, False]
    assert [r.italic for r in first.runs] == [False, False, True]


def test_parse_rich_skips_empty_paragraphs_and_bad_input():
    empty = (
        '<Section xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">'
        "<Paragraph><Run>正文</Run></Paragraph><Paragraph><Run></Run></Paragraph></Section>"
    )
    assert [b.text for b in parse_rich(empty)] == ["正文"]
    assert parse_rich("") == []
    assert parse_rich(None) == []
    assert parse_rich("<Section 这不是 XML") == []
    figure = (
        '<Section xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">'
        "<Paragraph><Run>正文</Run></Paragraph>"
        "<Paragraph><Run>«插图:插图/a.png»</Run></Paragraph></Section>"
    )
    assert [b.text for b in parse_rich(figure)] == ["正文"]
    inline = (
        '<Section xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">'
        "<Paragraph><Run>甲«插图:插图/a.png|w=120|in=1»乙«删:旧»</Run></Paragraph>"
        "<Paragraph><Run>«插图:插图/a.png|cap=1»</Run></Paragraph>"
        "<Paragraph><Run>题注</Run></Paragraph></Section>"
    )
    blocks = parse_rich(inline)
    assert [b.text for b in blocks] == ["甲乙", "题注"]
    assert blocks[1].kind == "caption"
    assert "«插图:" not in "".join(b.text for b in blocks)


def test_parse_rich_does_not_treat_whole_document_bold_as_heading():
    """整篇被选成全粗时根节点带 FontWeight="Bold"，段落不该被误判成标题。"""
    bold_all = (
        '<Section xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" FontWeight="Bold" FontSize="16">'
        "<Paragraph><Run>整篇加粗的正文</Run></Paragraph></Section>"
    )
    blocks = parse_rich(bold_all)
    assert [b.kind for b in blocks] == ["p"]
    assert blocks[0].runs[0].bold is True  # 样式仍然继承下来


def test_blocks_from_plain_splits_lines():
    blocks = blocks_from_plain("一\n\n二\n  \n三")
    assert [b.text for b in blocks] == ["一", "二", "三"]


# ---------------------------------------------------------------- Markdown


def test_markdown_keeps_emphasis_headings_and_quotes():
    md = blocks_to_markdown(parse_rich(RICH))
    assert "**加粗**" in md
    assert "*斜体*" in md
    assert "### 章内标题" in md
    assert "> 引用一句" in md
    assert md.startswith("第一段")


def test_markdown_escapes_text_that_would_become_structure():
    assert blocks_to_markdown(blocks_from_plain("# 不是标题")) == "\\# 不是标题"
    assert blocks_to_markdown(blocks_from_plain("> 不是引用")) == "\\> 不是引用"
    assert blocks_to_markdown(blocks_from_plain("1. 不是列表")) == "1\\. 不是列表"
    assert blocks_to_markdown(blocks_from_plain("带 * 星号和 _ 下划线")) == "带 \\* 星号和 \\_ 下划线"


def test_xhtml_uses_strong_em_and_headings():
    xhtml = blocks_to_xhtml(parse_rich(RICH))
    assert "<strong>加粗</strong>" in xhtml
    assert "<em>斜体</em>" in xhtml
    assert "<h2>章内标题</h2>" in xhtml
    assert "<blockquote><p>引用一句</p></blockquote>" in xhtml
    assert xhtml.startswith("<p>第一段")


def test_xhtml_escapes_markup():
    xhtml = blocks_to_xhtml(blocks_from_plain("5 < 6 & 7"))
    assert xhtml == "<p>5 &lt; 6 &amp; 7</p>"


# ---------------------------------------------------------------- 字体与样式表


def test_css_font_stack_keeps_theme_fonts():
    assert css_font_stack("Cambria, SimSun, 宋体") == '"Cambria", "SimSun", "宋体", serif'
    assert css_font_stack(None) == '"Cambria", "SimSun", "宋体", serif'


def test_css_font_stack_drops_injection_attempts():
    got = css_font_stack('Evil"; } body { color: red } /*')
    assert got == '"Cambria", "SimSun", "宋体", serif'
    assert "red" not in got
    for ch in "{};":
        assert ch not in got


def test_epub_stylesheet_matches_body_typography():
    css = epub_stylesheet("Cambria, SimSun, 宋体")
    assert "line-height: 1.95" in css  # 正文区的 1.5 × 1.3
    assert "text-indent: 2em" in css
    assert "max-width: 760px" in css
    assert '"SimSun"' in css
    assert "font-size: 1em" in css  # 字号交给阅读器
    assert "h1" in css and "blockquote" in css


# ---------------------------------------------------------------- 章标题


def test_chapter_heading_does_not_repeat_default_title():
    assert chapter_heading(_ch(3)) == "第3章"
    assert chapter_heading(_ch(3, "第3章")) == "第3章"
    assert chapter_heading(_ch(3, "老屋吹过的晚风")) == "第3章 老屋吹过的晚风"


# ---------------------------------------------------------------- 端到端


@pytest.fixture()
def project(tmp_path, monkeypatch):
    """把数据目录指到临时目录，建一个真的作品 + 一章带格式正文。"""
    monkeypatch.setattr(db, "STORAGE", tmp_path / "storage")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "storage" / "t.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "storage" / "projects")
    db.init_db()
    proj = create_project(
        ProjectCreate(
            title="测试作品",
            entries=[LoreEntry(category="world", content="一句话梗概。")],
            parent_dir=str(tmp_path),
        )
    )
    upsert_chapter(
        proj.id,
        Chapter(no=1, title="第1章", body_text="第一段\n\n加粗斜体", body_rich=RICH),
    )
    return proj


def test_markdown_export_end_to_end(project):
    body = client.get(f"/project/{project.id}/export?fmt=markdown").json()
    text = Path(body["path"]).read_text(encoding="utf-8")
    assert text.startswith("# 测试作品")
    assert "> 一句话梗概。" in text
    assert "## 第1章\n" in text  # 默认标题不重复成「第1章 第1章」
    assert "**加粗**" in text
    assert "### 章内标题" in text
    assert "> 引用一句" in text


def test_epub_export_end_to_end(project):
    body = client.get(
        f"/project/{project.id}/export?fmt=epub&font=Cambria%2C%20SimSun%2C%20宋体"
    ).json()
    path = Path(body["path"])
    assert path.suffix == ".epub"

    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        assert names[0] == "mimetype"  # 必须是第一个且不压缩
        assert z.getinfo("mimetype").compress_type == zipfile.ZIP_STORED
        assert "OEBPS/style.css" in names

        css = z.read("OEBPS/style.css").decode("utf-8")
        assert "line-height: 1.95" in css
        assert "text-indent: 2em" in css
        assert '"SimSun"' in css

        opf = z.read("OEBPS/content.opf").decode("utf-8")
        assert 'href="style.css"' in opf
        assert "text/css" in opf
        assert "dcterms:modified" in opf  # EPUB 3 必需，缺了 epubcheck 会报错

        chapter = z.read("OEBPS/chapters/ch1.xhtml").decode("utf-8")
        assert 'href="../style.css"' in chapter
        assert "<h1>第1章</h1>" in chapter
        assert "<strong>加粗</strong>" in chapter
        assert "<em>斜体</em>" in chapter
        assert "<h2>章内标题</h2>" in chapter
        assert "<blockquote><p>引用一句</p></blockquote>" in chapter

        nav = z.read("OEBPS/nav.xhtml").decode("utf-8")
        assert "第1章" in nav


def test_epub_export_without_font_falls_back_to_default_stack(project):
    body = client.get(f"/project/{project.id}/export?fmt=epub").json()
    with zipfile.ZipFile(Path(body["path"])) as z:
        css = z.read("OEBPS/style.css").decode("utf-8")
    assert '"Cambria", "SimSun", "宋体", serif' in css


def test_txt_export_end_to_end(project):
    body = client.get(f"/project/{project.id}/export?fmt=txt").json()
    raw = Path(body["path"]).read_bytes()
    assert raw.startswith(b"\xef\xbb\xbf")  # BOM
    text = raw.decode("utf-8-sig")
    assert text.startswith("测试作品\r\n")
    assert "第1章\r\n\r\n第一段加粗斜体" in text
    assert "**" not in text


def test_docx_export_end_to_end(project):
    body = client.get(
        f"/project/{project.id}/export?fmt=docx&font=Cambria%2C%20SimSun%2C%20宋体"
    ).json()
    path = Path(body["path"])
    assert path.suffix == ".docx"
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        for part in ("[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml"):
            assert part in names
        for name in names:
            ET.fromstring(z.read(name))
        doc = z.read("word/document.xml").decode("utf-8")
        styles = z.read("word/styles.xml").decode("utf-8")
    assert '<w:pStyle w:val="Title"/></w:pPr><w:r><w:t xml:space="preserve">测试作品</w:t>' in doc
    assert '<w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t xml:space="preserve">第1章</w:t>' in doc
    assert '<w:rPr><w:b/></w:rPr><w:t xml:space="preserve">加粗</w:t>' in doc
    assert '<w:rPr><w:i/></w:rPr><w:t xml:space="preserve">斜体</w:t>' in doc
    assert '<w:pStyle w:val="Heading2"/>' in doc and '<w:pStyle w:val="Quote"/>' in doc
    assert 'w:ascii="Cambria"' in styles and 'w:eastAsia="SimSun"' in styles


def test_epub_documents_are_well_formed_xml(project):
    """正文里的 & < > 必须转义，否则阅读器打不开。"""
    body = client.get(f"/project/{project.id}/export?fmt=epub").json()
    with zipfile.ZipFile(Path(body["path"])) as z:
        for name in z.namelist():
            if name.endswith((".xhtml", ".opf", ".xml")):
                ET.fromstring(z.read(name))
