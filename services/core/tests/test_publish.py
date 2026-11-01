"""发布包：平台表、平台排版规则、落盘编码、边界处理。

这里刻意不去碰 AI：发布包是纯文本处理，和 `test_export.py` 同一层。
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db
import services.core.publish as publish
from services.core.main import app
from services.core.models import Chapter, ChapterStatus, LoreEntry, ProjectCreate
from services.core.publish.platforms import PLATFORMS, PLATFORM_GROUPS, PlatformSpec
from services.core.repository import create_project, list_chapters, upsert_chapter

client = TestClient(app)

# 一段带格式正文：普通段落（含加粗 / 斜体）、章内一级标题、引用
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


@pytest.fixture()
def project(tmp_path, monkeypatch):
    """数据目录指到临时目录，建一个真作品：1 章带格式、1 章普通、1 章作废、1 章空。"""
    monkeypatch.setattr(db, "STORAGE", tmp_path / "storage")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "storage" / "t.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "storage" / "projects")
    db.init_db()
    proj = create_project(
        ProjectCreate(
            title="测试作品",
            genre=["轻小说"],
            tone="欢快",
            audience="青年",
            entries=[LoreEntry(category="world", content="一句话梗概。")],
            parent_dir=str(tmp_path),
        )
    )
    upsert_chapter(proj.id, Chapter(no=1, title="第1章", body_text="第一段加粗斜体", body_rich=RICH))
    upsert_chapter(proj.id, Chapter(no=2, title="雨天的天台", body_text="第二段正文", word_count=5))
    upsert_chapter(
        proj.id, Chapter(no=3, title="第3章", body_text="这一章作废了", status=ChapterStatus.superseded)
    )
    upsert_chapter(proj.id, Chapter(no=4, title="第4章", body_text=""))
    return proj


# ---------------------------------------------------------------- 平台表


def test_platform_table_is_consistent():
    keys = [p.key for p in PLATFORMS]
    assert len(keys) == len(set(keys)), "平台 key 有重复"
    assert len(PLATFORMS) >= 10, "平台太少，用户点名的几组都得在"

    group_keys = {g[0] for g in PLATFORM_GROUPS}
    covered = set()
    for spec in PLATFORMS:
        assert spec.key and spec.name and spec.group
        assert spec.group in group_keys, f"{spec.key} 的分组 {spec.group} 不在 PLATFORM_GROUPS 里"
        assert spec.backend_url.startswith("http")
        assert spec.homepage.startswith("http")
        assert spec.format in {"txt", "md", "html"}
        assert spec.indent in {"none", "full"}
        assert spec.notes, f"{spec.key} 没有要求速查"
        covered.add(spec.group)
    assert covered == group_keys, f"空分组：{group_keys - covered}"


def test_platform_endpoint_returns_the_same_table():
    body = client.get("/publish/platforms").json()
    assert [g["key"] for g in body["groups"]] == [g[0] for g in PLATFORM_GROUPS]
    assert [p["key"] for p in body["platforms"]] == [p.key for p in PLATFORMS]
    assert body["platforms"][0]["notes"], "notes 要一并下发，对话框右侧靠它"


# ---------------------------------------------------------------- 排版


def _ch(no: int = 1, title: str = "第1章", **kw) -> Chapter:
    return Chapter(no=no, title=title, **kw)


def test_plain_indent_full_prefixes_every_paragraph():
    text = publish.render_plain(
        [_ch(body_text="第一段\n第二段")], publish.RenderOptions(indent=publish.INDENT_FULL)
    )
    assert "\u3000\u3000第一段\n\n\u3000\u3000第二段" in text


def test_plain_without_indent_stays_clean():
    text = publish.render_plain([_ch(body_text="第一段")], publish.RenderOptions())
    assert text.startswith("第1章\n\n第一段")
    assert "\u3000" not in text


def test_plain_head_comes_from_options():
    opt = publish.RenderOptions(title="测试作品", author="某人", synopsis="一句话梗概。", with_head=True)
    text = publish.render_plain([_ch(body_text="正文")], opt)
    assert text.startswith("测试作品\n作者：某人\n简介：\n一句话梗概。")
    assert "正文" in text


def test_markdown_keeps_inline_styles_and_kinds():
    md = publish.render_markdown([_ch(body_rich=RICH)], publish.RenderOptions())
    assert md.startswith("## 第1章")
    assert "**加粗**" in md
    assert "*斜体*" in md
    assert "### 章内标题" in md
    assert "> 引用一句" in md


def test_html_fragment_has_paragraphs_heading_and_quote():
    html = publish.render_html([_ch(body_rich=RICH)], publish.RenderOptions())
    assert html.startswith("<h1>第1章</h1>"), "章标题用 h1，章内标题才有 h2/h3 的层级"
    assert "<strong>加粗</strong>" in html
    assert "<h2>章内标题</h2>" in html
    assert "<blockquote><p>引用一句</p></blockquote>" in html


def test_html_indent_applies_to_body_only():
    html = publish.render_html([_ch(body_rich=RICH)], publish.RenderOptions(indent=publish.INDENT_FULL))
    assert "<p>\u3000\u3000第一段" in html
    assert "<blockquote><p>引用一句</p></blockquote>" in html, "引用不该带首行缩进"


def test_html_document_wraps_fragment():
    doc = publish.html_document("<p>正文</p>", "测试作品")
    assert doc.startswith("<!DOCTYPE html>")
    assert '<meta charset="utf-8"/>' in doc
    assert "<title>测试作品</title>" in doc


# ---------------------------------------------------------------- 范围


def test_scope_chapter_returns_only_that_chapter(project):
    result = publish.prepare(project, platforms=["qidian"], scope="chapter", chapter_no=2)
    text = Path(result["items"][0]["files"][0]).read_text(encoding="utf-8-sig")
    assert "第二段正文" in text
    assert "第一段" not in text
    assert "第2章 雨天的天台" in text


def test_scope_book_skips_superseded(project):
    result = publish.prepare(project, platforms=["qidian"], scope="book")
    text = Path(result["items"][0]["files"][0]).read_text(encoding="utf-8-sig")
    assert "第一段" in text and "第二段正文" in text
    assert "这一章作废了" not in text
    assert result["chapters"] == 3


def test_sort_order_is_by_chapter_no(project):
    result = publish.prepare(project, platforms=["qidian"], scope="book")
    text = Path(result["items"][0]["files"][0]).read_text(encoding="utf-8-sig")
    assert text.index("第1章") < text.index("第2章")


# ---------------------------------------------------------------- 落盘


def test_txt_uses_bom_and_crlf(project):
    result = publish.prepare(project, platforms=["qidian"], scope="chapter", chapter_no=1)
    path = Path(result["items"][0]["files"][0])
    assert path.name == "正文.txt"
    raw = path.read_bytes()
    assert raw.startswith(b"\xef\xbb\xbf"), "书站导入器需要 BOM"
    assert b"\r\n" in raw
    assert b"\n" not in raw.replace(b"\r\n", b""), "不该出现裸 LF"


def test_publish_dir_layout_and_info_file(project):
    result = publish.prepare(
        project, platforms=["qidian", "zhihu", "bilibili"], scope="chapter", chapter_no=1, author="某人"
    )
    root = Path(project.root_path)
    names = {Path(i["dir"]).name for i in result["items"]}
    assert names == {"起点中文网", "知乎（写文章）", "B站专栏"}
    for item in result["items"]:
        assert Path(item["dir"]).parent == root / "导出" / "发布"
        assert len(item["files"]) == 2
        info = Path(item["dir"]) / "发布信息.txt"
        assert info.is_file()
        text = info.read_text(encoding="utf-8")
        assert item["name"] in text
        assert "作者：某人" in text
        assert "## 该平台要求速查" in text
        assert item["backend_url"] in text
        assert "- " + item["notes"][0] in text


def test_each_format_writes_its_own_body_file(project):
    result = publish.prepare(project, platforms=["qidian", "zhihu", "bilibili"], scope="chapter", chapter_no=1)
    by_key = {i["key"]: i for i in result["items"]}
    assert Path(by_key["qidian"]["files"][0]).name == "正文.txt"
    assert Path(by_key["zhihu"]["files"][0]).name == "正文.md"
    assert Path(by_key["bilibili"]["files"][0]).name == "正文.html"

    md = Path(by_key["zhihu"]["files"][0]).read_text(encoding="utf-8")
    assert md.startswith("# 测试作品"), "社区平台默认带书名抬头"
    assert "**加粗**" in md

    html = Path(by_key["bilibili"]["files"][0]).read_text(encoding="utf-8")
    assert html.startswith("<!DOCTYPE html>")
    assert "<strong>加粗</strong>" in html


def test_txt_platform_does_not_add_head_unless_asked(project):
    plain = publish.prepare(project, platforms=["qidian"], scope="chapter", chapter_no=1, author="某人")
    text = Path(plain["items"][0]["files"][0]).read_text(encoding="utf-8-sig")
    assert "作者：某人" not in text, "导入类书站的头会污染章节切分"

    forced = publish.prepare(
        project, platforms=["qidian"], scope="chapter", chapter_no=1, author="某人", with_head=True
    )
    text2 = Path(forced["items"][0]["files"][0]).read_text(encoding="utf-8-sig")
    assert "作者：某人" in text2


def test_indent_override_wins_over_platform_default(project):
    result = publish.prepare(
        project, platforms=["qidian"], scope="chapter", chapter_no=2, indent=publish.INDENT_FULL
    )
    text = Path(result["items"][0]["files"][0]).read_text(encoding="utf-8-sig")
    assert "\u3000\u3000第二段正文" in text


def test_unknown_indent_falls_back_to_platform_default(project):
    result = publish.prepare(project, platforms=["qidian"], scope="chapter", chapter_no=2, indent="bogus")
    text = Path(result["items"][0]["files"][0]).read_text(encoding="utf-8-sig")
    assert "\u3000" not in text


def test_empty_chapter_marks_item_empty_and_still_writes(project):
    result = publish.prepare(project, platforms=["qidian"], scope="chapter", chapter_no=4)
    item = result["items"][0]
    assert item["empty"] is True
    assert item["chars"] == 0
    assert Path(item["files"][0]).is_file()
    assert Path(item["files"][1]).is_file()


def test_output_dir_name_is_sanitized(project, monkeypatch):
    fake = PlatformSpec(
        key="weird",
        name="a/b:c?d",
        group="booksite",
        backend_url="https://example.com/",
        homepage="https://example.com/",
    )
    monkeypatch.setattr(publish, "get_platform", lambda k: fake if k == "weird" else None)
    monkeypatch.setattr(publish, "PLATFORMS", (fake,))
    result = publish.prepare(project, platforms=["weird"], scope="chapter", chapter_no=1)
    assert Path(result["items"][0]["dir"]).name == "a_b_c_d"


def test_generating_twice_overwrites_the_same_paths(project):
    first = publish.prepare(project, platforms=["qidian"], scope="chapter", chapter_no=1)
    second = publish.prepare(project, platforms=["qidian"], scope="chapter", chapter_no=1)
    assert first["items"][0]["files"] == second["items"][0]["files"]


# ---------------------------------------------------------------- 剪贴板与边界


def test_preview_carries_full_text_for_clipboard(project):
    result = publish.prepare(project, platforms=["zhihu"], scope="chapter", chapter_no=1)
    preview = result["preview"]
    assert preview["truncated"] is False
    assert "第一段" in preview["text"]
    assert "<p>" in preview["html"]


def test_preview_truncates_when_too_long(project, monkeypatch):
    monkeypatch.setattr(publish, "CLIP_LIMIT", 3)
    result = publish.prepare(project, platforms=["zhihu"], scope="chapter", chapter_no=1)
    assert result["preview"]["truncated"] is True
    assert len(result["preview"]["text"]) == 3
    # 文件必须还是完整的：剪贴板截断了，落盘不能截断
    on_disk = Path(result["items"][0]["files"][0]).read_text(encoding="utf-8")
    assert "加粗" in on_disk


def test_prepare_rejects_empty_and_unknown_platforms(project):
    with pytest.raises(Exception) as empty:
        publish.prepare(project, platforms=[], scope="chapter", chapter_no=1)
    assert "NO_PLATFORM" in str(empty.value)

    with pytest.raises(Exception) as unknown:
        publish.prepare(project, platforms=["nope"], scope="chapter", chapter_no=1)
    assert "BAD_PLATFORM" in str(unknown.value)


def test_prepare_endpoint_errors_are_400(project):
    empty = client.post(f"/project/{project.id}/publish/prepare", json={"platforms": []})
    assert empty.status_code == 400
    assert empty.json()["detail"]["code"] == "NO_PLATFORM"

    unknown = client.post(f"/project/{project.id}/publish/prepare", json={"platforms": ["nope"]})
    assert unknown.status_code == 400
    assert unknown.json()["detail"]["code"] == "BAD_PLATFORM"

    missing = client.post(
        f"/project/{project.id}/publish/prepare", json={"platforms": ["qidian"], "chapter_no": 99}
    )
    assert missing.status_code == 400
    assert missing.json()["detail"]["code"] == "NO_CHAPTER"


def test_prepare_endpoint_roundtrip(project):
    resp = client.post(
        f"/project/{project.id}/publish/prepare",
        json={"platforms": ["qidian", "weibo"], "scope": "book", "author": "某人", "synopsis": "自己写的简介"},
    )
    assert resp.status_code == 200
    body = resp.json()
    assert body["root_missing"] is False
    assert [i["key"] for i in body["items"]] == ["qidian", "weibo"]
    assert Path(body["items"][0]["files"][0]).is_file()
    info = (Path(body["items"][0]["dir"]) / "发布信息.txt").read_text(encoding="utf-8")
    assert "自己写的简介" in info
    assert "全书" in info


def test_prepare_does_not_touch_chapter_data(project):
    def snapshot():
        return [
            (c.no, c.body_text, c.body_rich, c.word_count, c.consistency_report, c.status)
            for c in list_chapters(project.id)
        ]

    before = snapshot()
    publish.prepare(project, platforms=["qidian", "bilibili"], scope="book", author="某人")
    assert snapshot() == before


def test_synopsis_defaults_to_world_entry(project):
    result = publish.prepare(project, platforms=["zhihu"], scope="chapter", chapter_no=1)
    text = Path(result["items"][0]["files"][0]).read_text(encoding="utf-8")
    assert "一句话梗概。" in text, "简介默认取背景设定的世界观"
