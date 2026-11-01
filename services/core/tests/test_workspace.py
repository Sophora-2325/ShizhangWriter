from __future__ import annotations

import sys
import tempfile
from pathlib import Path

from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db

db.STORAGE = ROOT / "storage" / "test_ws"
db.DB_PATH = db.STORAGE / "test_ws.db"
db.PROJECTS_DIR = db.STORAGE / "projects"
db.STORAGE.mkdir(parents=True, exist_ok=True)
if db.DB_PATH.exists():
    db.DB_PATH.unlink()

from services.core.db import init_db
from services.core.main import app
from services.core.models import ProjectCreate
from services.core.repository import create_project

init_db()
client = TestClient(app)
PARENT = Path(tempfile.mkdtemp(prefix="novelgm_ws_"))

CHAR = {
    "id": "",
    "name": "林烬",
    "age": "28",
    "personality": "沉默",
    "appearance": "高瘦",
    "outfit": "旧风衣",
    "recent_thoughts": "想安稳度日",
}


def _create(title: str) -> dict:
    r = client.post(
        "/project",
        json={
            "title": title,
            "entries": [{"category": "world", "content": "前提"}],
            "characters": [],
            "style": {},
            "threads": [],
            "parent_dir": str(PARENT),
        },
    )
    assert r.status_code == 200, r.text
    pid = r.json()["id"]
    client.patch(f"/project/{pid}/settings", json={"provider": "mock"})
    return r.json()


def _full_chapter(pid: str) -> int:
    n = client.post(f"/project/{pid}/plan", json={"user_notes": ""}).json()["no"]
    assert client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True}).status_code == 200
    assert client.post(f"/project/{pid}/chapter/{n}/finalize").status_code == 200
    ref = client.post(
        f"/project/{pid}/chapter/{n}/refine",
        json={"chapter_no": n, "user_annotations": "", "next_chapter_intent": "继续", "thread_updates": [], "bible_edits": [], "constraints_for_next": []},
    )
    assert ref.status_code == 200, ref.text
    return n


def test_create_requires_parent_dir():
    r = client.post("/project", json={"title": "无路径"})
    assert r.status_code == 400
    assert r.json()["detail"]["code"] == "PARENT_REQUIRED"


def test_folder_layout_and_chapter_file():
    p = _create("文件夹测试")
    root = Path(p["root_path"])
    assert root == PARENT / "文件夹测试"
    assert (root / "作品.json").is_file()

    client.post(f"/project/{p['id']}/plan", json={"user_notes": ""})
    assert (root / "第1章.md").is_file()
    assert (root / ".novel-gm" / "chapters" / "0001.json").is_file()

    gen = client.post(f"/project/{p['id']}/chapter", json={"prefix": "", "confirm_plan": True}).json()
    assert (root / "第1章.md").read_text(encoding="utf-8") == gen["body_text"]

    dup = client.post("/project", json={"title": "文件夹测试", "parent_dir": str(PARENT)})
    assert dup.status_code == 409


def test_rename_chapter_and_project():
    p = _create("改名测试")
    pid, root = p["id"], Path(p["root_path"])
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})

    r = client.patch(f"/project/{pid}/chapter/1", json={"title": "初遇"})
    assert r.status_code == 200
    assert (root / "初遇.md").is_file()
    assert not (root / "第1章.md").exists()

    r = client.patch(f"/project/{pid}", json={"title": "改名之后"})
    assert r.status_code == 200
    new_root = PARENT / "改名之后"
    assert r.json()["root_path"] == str(new_root)
    assert (new_root / "初遇.md").is_file()
    assert not root.exists()


def test_delete_chapter_renumbers():
    p = _create("删除测试")
    pid, root = p["id"], Path(p["root_path"])
    for _ in range(3):
        _full_chapter(pid)
    client.patch(f"/project/{pid}/chapter/3", json={"title": "终章"})

    r = client.delete(f"/project/{pid}/chapter/2")
    assert r.status_code == 200, r.text
    assert r.json()["current_chapter"] == 2
    chapters = client.get(f"/project/{pid}/chapters").json()
    assert [(c["no"], c["title"]) for c in chapters] == [(1, "第1章"), (2, "终章")]
    assert (root / "第1章.md").is_file() and (root / "终章.md").is_file()
    assert not (root / "第2章.md").exists()
    assert not (root / ".novel-gm" / "chapters" / "0003.json").exists()


def test_clear_chapter_content():
    p = _create("清空测试")
    pid, root = p["id"], Path(p["root_path"])
    n = _full_chapter(pid)
    r = client.post(f"/project/{pid}/chapter/{n}/clear")
    assert r.status_code == 200
    assert r.json()["body_text"] == ""
    assert r.json()["status"] == "planned"
    assert (root / "第1章.md").read_text(encoding="utf-8") == ""
    assert client.get(f"/project/{pid}").json()["phase"] == "PLAN"


def test_lore_inherits_to_next_chapter():
    p = _create("继承测试")
    pid = p["id"]
    r = client.put(
        f"/project/{pid}/lore",
        json={"entries": [{"category": "world", "content": "初始前提"}], "characters": [CHAR], "threads": []},
    )
    assert r.status_code == 200, r.text
    cid = r.json()["characters"][0]["id"]

    ch1 = client.post(f"/project/{pid}/plan", json={"user_notes": ""}).json()
    assert ch1["lore"]["characters"][0]["name"] == "林烬"

    changed = dict(CHAR, id=cid, recent_thoughts="怀疑有人跟踪")
    thread = {"id": "", "name": "跟踪疑云", "kind": "mystery", "state": "seed", "user_directive": "慢慢揭开"}
    # 人物公共资料走人物接口（可带章号），章节设定保存只负责名单/背景/脉络
    r = client.post(
        f"/project/{pid}/characters",
        json=dict(changed, chapter_no=1),
    )
    assert r.status_code == 200, r.text
    r = client.put(
        f"/project/{pid}/lore?chapter=1",
        json={"entries": [{"category": "world", "content": "初始前提"}], "characters": [changed], "threads": [thread]},
    )
    assert r.status_code == 200, r.text

    _full_chapter(pid)
    ch2 = client.post(f"/project/{pid}/plan", json={"user_notes": ""}).json()
    assert ch2["no"] == 2
    assert ch2["lore"]["characters"][0]["recent_thoughts"] == "怀疑有人跟踪"
    assert [t["name"] for t in ch2["lore"]["threads"]] == ["跟踪疑云"]

    # 用更早章节的旧快照保存人物，不得把主档资料改回去
    old = dict(CHAR, id=cid, recent_thoughts="回忆往事")
    client.put(
        f"/project/{pid}/lore?chapter=1",
        json={"entries": [{"category": "world", "content": "初始前提"}], "characters": [old], "threads": []},
    )
    assert client.get(f"/project/{pid}").json()["characters"][0]["recent_thoughts"] == "怀疑有人跟踪"

    bad = client.put(
        f"/project/{pid}/lore", json={"entries": [], "characters": [{"id": "", "name": ""}], "threads": []}
    )
    assert bad.status_code == 400


def test_open_folder_restores_work():
    p = _create("打开测试")
    pid, root = p["id"], p["root_path"]
    _full_chapter(pid)
    before = client.get(f"/project/{pid}/chapters").json()

    assert client.delete(f"/project/{pid}").status_code == 200
    assert client.get(f"/project/{pid}").status_code == 404

    r = client.post("/projects/open", json={"path": root})
    assert r.status_code == 200, r.text
    assert r.json()["id"] == pid
    after = client.get(f"/project/{pid}/chapters").json()
    assert [c["body_text"] for c in after] == [c["body_text"] for c in before]
    assert after[0]["status"] == before[0]["status"]

    bad = client.post("/projects/open", json={"path": str(PARENT)})
    assert bad.status_code == 400


def test_migrate_legacy_project():
    legacy = create_project(ProjectCreate(title="旧作品"))
    pid = legacy.id
    client.patch(f"/project/{pid}/settings", json={"provider": "mock"})
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    body = client.get(f"/project/{pid}/chapter/1").json()["body_text"]

    r = client.post(f"/project/{pid}/migrate", json={"parent_dir": str(PARENT)})
    assert r.status_code == 200, r.text
    root = PARENT / "旧作品"
    assert r.json()["root_path"] == str(root)
    assert (root / "作品.json").is_file()
    assert (root / "第1章.md").read_text(encoding="utf-8") == body
    assert client.get(f"/project/{pid}/chapter/1").json()["body_text"] == body


RICH = '<Section xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Paragraph><Run FontWeight="Bold">雨</Run></Paragraph></Section>'


def test_save_body_keeps_rich_and_rewrite_clears_it():
    p = _create("格式测试")
    pid, root = p["id"], Path(p["root_path"])
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})

    r = client.put(f"/project/{pid}/chapter/1/body", json={"body_text": "雨\r\n夜", "body_rich": RICH})
    assert r.status_code == 200, r.text
    assert r.json()["body_text"] == "雨\n夜"
    assert r.json()["word_count"] == 3
    assert (root / "第1章.md").read_text(encoding="utf-8") == "雨\n夜"
    assert client.get(f"/project/{pid}/chapter/1").json()["body_rich"] == RICH

    # 删库后从文件夹打开，格式仍在
    client.delete(f"/project/{pid}")
    client.post("/projects/open", json={"path": str(root)})
    assert client.get(f"/project/{pid}/chapter/1").json()["body_rich"] == RICH

    client.patch(f"/project/{pid}/settings", json={"provider": "mock"})
    regen = client.post(f"/project/{pid}/chapter/1/style-rewrite")
    assert regen.status_code == 200, regen.text
    assert regen.json()["body_rich"] == ""
    assert client.get(f"/project/{pid}/chapter/1").json()["body_rich"] == ""


def test_finalize_automatically_extracts_and_manual_refresh_remains_available():
    p = _create("拆分测试")
    pid = p["id"]
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})

    fin = client.post(f"/project/{pid}/chapter/1/finalize").json()
    assert fin["status"] == "final"
    assert fin["summary"] is not None
    assert fin["summary_status"] == "ready"
    assert client.get(f"/project/{pid}").json()["phase"] == "REFINE"

    ext = client.post(f"/project/{pid}/chapter/1/extract")
    assert ext.status_code == 200, ext.text
    assert ext.json()["summary"] is not None
    assert ext.json()["status"] == "final"


def test_handwritten_chapter_then_generate_continues():
    p = _create("手写续写")
    pid = p["id"]
    r = client.post(f"/project/{pid}/chapters", json={"body_text": "夜雨敲窗。", "body_rich": ""})
    assert r.status_code == 200, r.text
    assert r.json()["no"] == 1 and r.json()["status"] == "drafted" and r.json()["plan"] is None
    assert client.get(f"/project/{pid}").json()["current_chapter"] == 1

    gen = client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    assert gen.status_code == 200, gen.text
    assert gen.json()["plan"] is not None
    first = gen.json()["body_text"]
    assert first.startswith("夜雨敲窗。") and len(first) > len("夜雨敲窗。")

    again = client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    assert again.status_code == 200, again.text
    assert again.json()["body_text"].startswith(first)


def test_refine_after_planning_next_keeps_current_chapter():
    p = _create("先规划后细修")
    pid = p["id"]
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    assert client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True}).status_code == 200
    assert client.post(f"/project/{pid}/chapter/1/finalize").status_code == 200
    assert client.post(f"/project/{pid}/plan", json={"user_notes": ""}).json()["no"] == 2

    ref = client.post(
        f"/project/{pid}/chapter/1/refine",
        json={"chapter_no": 1, "user_annotations": "", "next_chapter_intent": "来客自报姓名", "thread_updates": [], "bible_edits": [], "constraints_for_next": []},
    )
    assert ref.status_code == 200, ref.text
    assert ref.json()["current_chapter"] == 2

    # 「创作下一章」随后的规划复用空着的第 2 章，而不是新建第 3 章
    assert client.post(f"/project/{pid}/plan", json={"user_notes": ""}).json()["no"] == 2
    gen = client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    assert gen.status_code == 200, gen.text
    assert gen.json()["no"] == 2
    assert client.get(f"/project/{pid}/chapter/1").json()["status"] == "final"


REFINE = {"user_annotations": "", "thread_updates": [], "bible_edits": [], "constraints_for_next": []}


def test_create_chapter_with_plan_then_edit_and_generate():
    p = _create("规划区建章")
    pid = p["id"]
    r = client.post(f"/project/{pid}/chapters", json={"plan": {"free_text": "主角在雨夜收留来客"}})
    assert r.status_code == 200, r.text
    ch = r.json()
    assert ch["no"] == 1 and ch["status"] == "planned" and ch["plan"]["free_text"] == "主角在雨夜收留来客"
    assert ch["lore_from"] is None

    plan = dict(ch["plan"], scene_goal="收留来客", must_include=["斗笠"])
    saved = client.put(f"/project/{pid}/chapter/1/plan", json=plan)
    assert saved.status_code == 200, saved.text
    assert saved.json()["plan"]["scene_goal"] == "收留来客"

    gen = client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    assert gen.status_code == 200, gen.text
    # 表单已填，生成不应覆盖用户计划
    assert gen.json()["plan"]["scene_goal"] == "收留来客"
    assert gen.json()["plan"]["free_text"] == "主角在雨夜收留来客"


def test_generate_with_only_free_text_fills_form_and_keeps_text():
    pid = _create("仅自由规划")["id"]
    client.post(f"/project/{pid}/chapters", json={"plan": {"free_text": "只写了几句想法"}})
    gen = client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    assert gen.status_code == 200, gen.text
    assert gen.json()["plan"]["scene_goal"]
    assert gen.json()["plan"]["free_text"] == "只写了几句想法"

    replan = client.post(f"/project/{pid}/chapter/1/replan")
    assert replan.status_code == 200, replan.text
    assert replan.json()["plan"]["free_text"] == "只写了几句想法"
    assert replan.json()["status"] == "drafted"


def test_plan_fields_and_free_text_reach_prompt():
    from services.core.context import build_layered_context
    from services.core.models import ChapterPlan

    proj = client.get(f"/project/{_create('提示词')['id']}").json()
    from services.core.models import Project

    layers = build_layered_context(
        Project(**proj),
        ChapterPlan(scene_goal="目标", must_include=["斗笠"], open_decision_for_user="是否开门", free_text="自由想法"),
        [],
    )
    assert "必须写到：['斗笠']" in layers["extra"]
    assert layers["extra"].index("二、具体内容") < layers["extra"].index("三、写作要求")
    assert "是否开门" in layers["extra"]
    assert "用户自由规划：\n自由想法" in layers["extra"]


def test_review_draft_saved_and_cleared_on_refine():
    pid = _create("评注草稿")["id"]
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    r = client.put(f"/project/{pid}/chapter/1/review", json={"user_annotations": "节奏慢", "next_chapter_intent": "开门"})
    assert r.status_code == 200, r.text
    assert r.json()["review"] == {"user_annotations": "节奏慢", "next_chapter_intent": "开门"}
    assert client.get(f"/project/{pid}/chapter/1").json()["review"]["next_chapter_intent"] == "开门"

    ref = client.post(f"/project/{pid}/chapter/1/refine", json=dict(REFINE, chapter_no=1, next_chapter_intent="开门"))
    assert ref.status_code == 200, ref.text
    assert client.get(f"/project/{pid}/chapter/1").json()["review"] is None


def test_refine_without_intent_plans_next_chapter():
    pid = _create("无意图细修")["id"]
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    ref = client.post(f"/project/{pid}/chapter/1/refine", json={"chapter_no": 1, "user_annotations": "节奏慢"})
    assert ref.status_code == 200, ref.text
    nxt = client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    assert nxt.status_code == 200, nxt.text
    assert nxt.json()["no"] == 2


def test_refinement_without_intent_omits_intent_line():
    from services.core.context import build_layered_context
    from services.core.models import Project, Refinement

    proj = Project(**client.get(f"/project/{_create('空意图')['id']}").json())
    layers = build_layered_context(proj, None, [], Refinement(chapter_no=1, user_annotations="节奏慢"))
    assert "用户细修意图" not in layers["extra"]
    assert "评注：节奏慢" in layers["extra"]


def test_inherit_lore_from_earlier_chapter():
    pid = _create("继承设定")["id"]
    client.put(
        f"/project/{pid}/lore",
        json={"entries": [{"category": "world", "content": "初版梗概"}], "characters": [CHAR], "threads": []},
    )
    for i in (1, 2):
        client.post(f"/project/{pid}/plan", json={"user_notes": ""})
        client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
        client.post(f"/project/{pid}/chapter/{i}/refine", json=dict(REFINE, chapter_no=i, next_chapter_intent="继续"))
    ch3 = client.post(f"/project/{pid}/plan", json={"user_notes": ""}).json()
    assert ch3["no"] == 3 and ch3["lore_from"] == 2

    client.put(
        f"/project/{pid}/lore?chapter=2",
        json={"entries": [{"category": "world", "content": "第二章改过"}], "characters": [CHAR], "threads": []},
    )
    client.put(
        f"/project/{pid}/lore?chapter=3",
        json={"entries": [{"category": "world", "content": "第三章改过"}], "characters": [CHAR], "threads": []},
    )

    r = client.post(f"/project/{pid}/chapter/3/lore/inherit?from=1")
    assert r.status_code == 200, r.text
    assert r.json()["lore_from"] == 1 and r.json()["lore"]["entries"][0]["content"] == "初版梗概"
    # 第 3 章是最新章，作品级设定同步
    assert client.get(f"/project/{pid}").json()["entries"][0]["content"] == "初版梗概"

    edited = client.put(
        f"/project/{pid}/lore?chapter=3",
        json={"entries": [{"category": "world", "content": "继承后再改"}], "characters": [CHAR], "threads": []},
    )
    assert edited.status_code == 200
    assert client.get(f"/project/{pid}/chapter/3").json()["lore_from"] == 1

    assert client.post(f"/project/{pid}/chapter/2/lore/inherit?from=3").status_code == 400


def test_portrait_copied_into_folder():
    p = _create("画像测试")
    src = PARENT / "src.png"
    src.write_bytes(b"\x89PNG\r\n\x1a\n")
    r = client.post(f"/project/{p['id']}/portrait", json={"character_id": "c1", "source_path": str(src)})
    assert r.status_code == 200, r.text
    assert r.json()["portrait"] == "人物画像/c1.png"
    assert (Path(p["root_path"]) / "人物画像" / "c1.png").is_file()


def test_project_provider_inherits_global_setting():
    from services.core.providers import resolve_for_project
    from services.core.repository import get_project

    r = client.post(
        "/project",
        json={"title": "生成方式测试", "entries": [], "characters": [], "style": {}, "threads": [], "parent_dir": str(PARENT)},
    )
    pid = r.json()["id"]
    assert r.json()["provider"] == "inherit"
    try:
        s = client.post("/settings", json={"provider": "mock"}).json()
        assert s["provider"] == "mock" and s["offline_mode"] is False
        assert resolve_for_project(get_project(pid))[1] == "mock"

        # 作品单独设置优先于全局；只能选已添加的模型配置
        assert client.patch(f"/project/{pid}/settings", json={"provider": "bogus"}).status_code == 400
        local = client.post("/providers", json={"preset": "local", "name": "作品测试用本地"}).json()["id"]
        client.patch(f"/project/{pid}/settings", json={"provider": local})
        assert get_project(pid).provider == local
        assert resolve_for_project(get_project(pid))[1] == local
        client.patch(f"/project/{pid}/settings", json={"provider": "inherit"})
        assert resolve_for_project(get_project(pid))[1] == "mock"

        assert client.post("/settings", json={"provider": "bogus"}).status_code == 400
        client.delete(f"/providers/{local}")
    finally:
        client.post("/settings", json={"provider": "mock", "offline_mode": False})


def _chapter_files(pid: str) -> dict[int, str]:
    return {c["no"]: client.get(f"/project/{pid}/chapter/{c['no']}/file").json()["path"]
            for c in client.get(f"/project/{pid}/chapters").json()}


def test_insert_chapter_shifts_later_chapters():
    pid = _create("插入章节")["id"]
    _full_chapter(pid)
    _full_chapter(pid)
    client.patch(f"/project/{pid}/chapter/2", json={"title": "雨夜"})

    r = client.post(f"/project/{pid}/chapter/1/insert-after")
    assert r.status_code == 200, r.text
    new = r.json()
    assert new["no"] == 2 and new["title"] == "第2章" and new["status"] == "planned"
    assert new["lore_from"] == 1

    chapters = {c["no"]: c for c in client.get(f"/project/{pid}/chapters").json()}
    assert sorted(chapters) == [1, 2, 3]
    assert chapters[3]["title"] == "雨夜" and chapters[3]["body_text"]
    files = _chapter_files(pid)
    assert all(Path(p).is_file() for p in files.values())
    assert Path(files[3]).name == "雨夜.md"
    assert client.get(f"/project/{pid}").json()["current_chapter"] == 3

    # 插在中间的空白章不会挡住后面章节的生成
    nxt = client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    assert nxt.status_code == 200, nxt.text
    assert nxt.json()["no"] == 4
    gen = client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    assert gen.status_code == 200, gen.text


def test_insert_after_latest_appends_as_current():
    pid = _create("末尾插入")["id"]
    _full_chapter(pid)
    new = client.post(f"/project/{pid}/chapter/1/insert-after").json()
    assert new["no"] == 2 and new["refinement"] is None
    proj = client.get(f"/project/{pid}").json()
    assert proj["current_chapter"] == 2 and proj["phase"] == "PLAN"


def test_unfinalize_returns_to_drafted():
    pid = _create("取消定稿")["id"]
    client.post(f"/project/{pid}/plan", json={"user_notes": ""})
    client.post(f"/project/{pid}/chapter", json={"prefix": "", "confirm_plan": True})
    assert client.post(f"/project/{pid}/chapter/1/unfinalize").status_code == 409
    client.post(f"/project/{pid}/chapter/1/finalize")
    r = client.post(f"/project/{pid}/chapter/1/unfinalize")
    assert r.status_code == 200, r.text
    assert r.json()["status"] == "drafted" and r.json()["style_report"] is None
    assert client.get(f"/project/{pid}").json()["phase"] == "GENERATE"


def test_import_chapters_appends_in_order():
    pid = _create("导入章节")["id"]
    _full_chapter(pid)
    r = client.post(f"/project/{pid}/import", json={"chapters": [
        {"title": "第二章 来客", "body_text": "门响了。\r\n\r\n他没开。"},
        {"title": "", "body_text": "第三段正文"},
    ]})
    assert r.status_code == 200, r.text
    assert [(c["no"], c["title"]) for c in r.json()] == [(2, "第二章 来客"), (3, "第3章")]
    ch2 = client.get(f"/project/{pid}/chapter/2").json()
    assert ch2["body_text"] == "门响了。\n\n他没开。" and ch2["status"] == "drafted"
    assert client.get(f"/project/{pid}").json()["current_chapter"] == 3
    assert client.post(f"/project/{pid}/import", json={"chapters": []}).status_code == 400


def test_backup_zips_folder_without_exports():
    import zipfile

    proj = _create("备份作品")
    pid = proj["id"]
    _full_chapter(pid)
    client.get(f"/project/{pid}/export?fmt=markdown")
    r = client.post(f"/project/{pid}/backup")
    assert r.status_code == 200, r.text
    path = Path(r.json()["path"])
    assert path.parent.name == "导出" and path.suffix == ".zip"
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
    assert "备份作品/作品.json" in names
    assert not any("/导出/" in n for n in names)
    assert any(n.endswith(".md") for n in names)
