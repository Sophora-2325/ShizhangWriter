"""共享人物主档、本章行动与 AI 上下文。

覆盖本轮契约：
- 人物只有姓名必填，新增性别/爱好/组织/思维习惯；
- 公共资料跨章一致，章节旧快照不能把主档改回去；
- 本章行动按章独立，省略=不改、空串=清空，插入/删除/重编号/重开作品后仍跟随人物 id；
- 旧字段（recent_thoughts/public_face/known_facts/status）不因新卡片保存丢失；
- 目标章行动只进入该章作者计划上下文；
- context_revision 能检测公共资料与行动变化。
"""

from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db

db.STORAGE = ROOT / "storage" / "test_character_actions"
db.DB_PATH = db.STORAGE / "test_character_actions.db"
db.PROJECTS_DIR = db.STORAGE / "projects"
db.STORAGE.mkdir(parents=True, exist_ok=True)
if db.DB_PATH.exists():
    db.DB_PATH.unlink()

from services.core.context import build_layered_context
from services.core.db import init_db
from services.core.main import app
from services.core.models import (
    Chapter,
    ChapterLore,
    ChapterPlan,
    Character,
    Project,
)
from services.core.repository import (
    context_revision,
    get_chapter,
    get_project,
    lore_view,
    save_project,
    upsert_chapter,
)

init_db()
client = TestClient(app)
PARENT = Path(tempfile.mkdtemp(prefix="novelgm_char_actions_"))


def _create(title: str, characters: list[dict] | None = None) -> dict:
    r = client.post(
        "/project",
        json={
            "title": title,
            "entries": [{"category": "world", "content": "前提"}],
            "characters": characters or [],
            "style": {},
            "threads": [
                {
                    "id": "main",
                    "name": "主线",
                    "kind": "main",
                    "state": "active",
                    "user_directive": "x",
                    "next_milestone": "",
                    "priority": 1,
                }
            ],
            "parent_dir": str(PARENT),
        },
    )
    assert r.status_code == 200, r.text
    return r.json()


def _chapters(pid: str) -> dict[int, dict]:
    return {c["no"]: c for c in client.get(f"/project/{pid}/chapters").json()}


def _chapter(pid: str, no: int) -> dict:
    r = client.get(f"/project/{pid}/chapter/{no}")
    assert r.status_code == 200, r.text
    return r.json()


def _add_chapter(pid: str, body: str = "") -> dict:
    r = client.post(f"/project/{pid}/chapters", json={"body_text": body})
    assert r.status_code == 200, r.text
    return r.json()


def _char(pid: str, **fields) -> dict:
    r = client.post(f"/project/{pid}/characters", json=fields)
    assert r.status_code == 200, r.text
    return r.json()


# ---------------------------------------------------------------- 人物资料


def test_new_card_fields_roundtrip_and_old_fields_preserved():
    pid = _create("新卡片字段")["id"]
    saved = _char(
        pid,
        name="林烬",
        age="28",
        personality="沉默",
        appearance="高瘦",
        outfit="旧风衣",
        recent_thoughts="想安稳度日",
        public_face="大学讲师",
        known_facts=["见过凶案现场"],
        status={"injury": "左臂"},
    )
    cid = saved["id"]
    assert saved["gender"] == "" and saved["hobbies"] == ""

    updated = _char(
        pid,
        id=cid,
        name="林烬",
        gender="男",
        hobbies="修钟表",
        organization="市立大学",
        thinking_habits="先记下时间再开口",
    )
    assert updated["gender"] == "男"
    assert updated["hobbies"] == "修钟表"
    assert updated["organization"] == "市立大学"
    assert updated["thinking_habits"] == "先记下时间再开口"
    # 新卡片布局没有的旧字段不能被空值抹掉
    assert updated["recent_thoughts"] == "想安稳度日"
    assert updated["public_face"] == "大学讲师"
    assert updated["known_facts"] == ["见过凶案现场"]
    assert updated["status"] == {"injury": "左臂"}

    stored = next(c for c in client.get(f"/project/{pid}/characters").json() if c["id"] == cid)
    assert stored["organization"] == "市立大学"


def test_action_requires_chapter_and_chapter_must_exist():
    pid = _create("行动校验")["id"]
    r = client.post(f"/project/{pid}/characters", json={"name": "甲", "chapter_action": "去开门"})
    assert r.status_code == 400
    assert r.json()["detail"]["code"] == "CHAPTER_REQUIRED"

    r = client.post(
        f"/project/{pid}/characters",
        json={"name": "甲", "chapter_no": 9, "chapter_action": "去开门"},
    )
    assert r.status_code == 400
    assert r.json()["detail"]["code"] == "CHAPTER_NOT_FOUND"

    _add_chapter(pid)
    r = client.post(f"/project/{pid}/characters", json={"name": "甲", "chapter_no": 1})
    assert r.status_code == 200, r.text
    assert _chapter(pid, 1)["character_actions"] == {}


def test_actions_are_per_chapter_omit_keeps_and_empty_clears():
    pid = _create("按章行动")["id"]
    _add_chapter(pid)
    _add_chapter(pid)
    cid = _char(pid, name="甲", chapter_no=1, chapter_action="去井边看水")["id"]

    ch1, ch2 = _chapter(pid, 1), _chapter(pid, 2)
    assert ch1["character_actions"] == {cid: "去井边看水"}
    assert ch2["character_actions"] == {}
    assert [c["id"] for c in ch1["lore"]["characters"]] == [cid]

    # 省略 chapter_action：只改公共资料，行动原样保留
    _char(pid, id=cid, name="甲", gender="女", chapter_no=1)
    assert _chapter(pid, 1)["character_actions"] == {cid: "去井边看水"}
    assert _chapter(pid, 1)["lore"]["characters"][0]["gender"] == "女"

    # 空串：明确清空
    _char(pid, id=cid, name="甲", chapter_no=1, chapter_action="")
    assert _chapter(pid, 1)["character_actions"] == {}

    # 另一章单独写入，不影响本章
    _char(pid, id=cid, name="甲", chapter_no=2, chapter_action="在门外等")
    assert _chapter(pid, 1)["character_actions"] == {}
    assert _chapter(pid, 2)["character_actions"] == {cid: "在门外等"}
    assert [c["id"] for c in _chapter(pid, 2)["lore"]["characters"]] == [cid]

    # 新章节默认不带行动
    added = _add_chapter(pid)
    assert added["character_actions"] == {}
    assert _chapter(pid, added["no"])["character_actions"] == {}


def test_master_profile_is_shared_and_old_snapshot_cannot_revert_it():
    pid = _create("共享资料", characters=[{"id": "c1", "name": "林烬", "gender": ""}])["id"]
    _add_chapter(pid)
    _add_chapter(pid)

    _char(pid, id="c1", name="林烬", gender="女", organization="影卫", chapter_no=1)
    assert get_project(pid).characters[0].gender == "女"

    # 章节 1 的快照随人物保存刷新；章节 2 的旧快照通过 lore_view 读取主档
    assert _chapter(pid, 1)["lore"]["characters"][0]["gender"] == "女"
    proj = get_project(pid)
    view = lore_view(proj, get_chapter(pid, 2).lore)
    shared = next(c for c in view.characters if c.id == "c1")
    assert shared.gender == "女" and shared.organization == "影卫"

    # 用更早章节的旧快照保存：不得把主档改回去，章节快照也刷新成主档资料
    stale = {"id": "c1", "name": "林烬", "gender": "", "organization": ""}
    r = client.put(
        f"/project/{pid}/lore?chapter=2",
        json={"entries": [], "characters": [stale], "threads": []},
    )
    assert r.status_code == 200, r.text
    assert get_project(pid).characters[0].gender == "女"
    assert _chapter(pid, 2)["lore"]["characters"][0]["gender"] == "女"


def test_chapter_lore_save_creates_missing_master_record():
    pid = _create("章节建主档")["id"]
    _add_chapter(pid)
    r = client.put(
        f"/project/{pid}/lore?chapter=1",
        json={
            "entries": [{"category": "world", "content": "前提"}],
            "characters": [{"id": "", "name": "周岚", "gender": "女", "thinking_habits": "记账"}],
            "threads": [],
        },
    )
    assert r.status_code == 200, r.text
    chars = client.get(f"/project/{pid}/characters").json()
    assert len(chars) == 1 and chars[0]["name"] == "周岚"
    assert chars[0]["gender"] == "女"
    assert _chapter(pid, 1)["lore"]["characters"][0]["id"] == chars[0]["id"]

    next_ch = _add_chapter(pid)
    assert next_ch["lore"]["characters"][0]["id"] == chars[0]["id"]


def test_master_missing_uses_chapter_snapshot_for_compat():
    pid = _create("旧章兼容")["id"]
    _add_chapter(pid)
    # 模拟旧数据：人物只存在于章节 lore，作品主档里没有
    proj = get_project(pid)
    proj.characters = []
    save_project(proj)
    ch = get_chapter(pid, 1)
    ch.lore = ChapterLore(
        entries=[],
        characters=[
            Character(
                id="legacy-1",
                name="旧人",
                recent_thoughts="旧念头",
                public_face="旧身份",
                known_facts=["旧事实"],
                status={"dead": "false"},
            )
        ],
        threads=[],
    )
    upsert_chapter(pid, ch)

    saved = _char(pid, id="legacy-1", name="旧人", gender="男", chapter_no=1)
    assert saved["recent_thoughts"] == "旧念头"
    assert saved["public_face"] == "旧身份"
    assert saved["known_facts"] == ["旧事实"]
    assert saved["status"] == {"dead": "false"}

    master = next(c for c in get_project(pid).characters if c.id == "legacy-1")
    assert master.gender == "男"
    assert _chapter(pid, 1)["lore"]["characters"][0]["gender"] == "男"


def test_delete_character_cleans_all_chapter_rosters_and_actions():
    pid = _create("删除清理")["id"]
    _add_chapter(pid)
    _add_chapter(pid)
    cid = _char(pid, name="甲", chapter_no=1, chapter_action="第一章行动")["id"]
    _char(pid, id=cid, name="甲", chapter_no=2, chapter_action="第二章行动")

    r = client.delete(f"/project/{pid}/characters/{cid}?chapter_no=1")
    assert r.status_code == 200, r.text
    assert r.json()["characters"] == []
    for no in (1, 2):
        ch = _chapter(pid, no)
        assert all(c["id"] != cid for c in ch["lore"]["characters"])
        assert cid not in ch["character_actions"]
    assert all(c.id != cid for c in get_project(pid).characters)


# ---------------------------------------------------------------- 章节操作与持久化


def test_actions_follow_insert_delete_renumber_and_reopen():
    pid = _create("行动跟着章节走")["id"]
    for _ in range(3):
        _add_chapter(pid)
    client.patch(f"/project/{pid}/chapter/2", json={"title": "第二章"})
    client.patch(f"/project/{pid}/chapter/3", json={"title": "第三章"})
    cid = _char(pid, name="甲", chapter_no=2, chapter_action="去码头")["id"]
    _char(pid, id=cid, name="甲", chapter_no=3, chapter_action="留在城里")
    root = Path(client.get(f"/project/{pid}").json()["root_path"])

    # 插入新第 2 章：原第 2 章顺延为第 3 章，行动跟着走
    r = client.post(f"/project/{pid}/chapter/1/insert-after")
    assert r.status_code == 200, r.text
    chapters = _chapters(pid)
    assert chapters[2]["character_actions"] == {}
    assert chapters[3]["character_actions"] == {cid: "去码头"}
    assert chapters[4]["character_actions"] == {cid: "留在城里"}

    # 删除插入的空白第 2 章：行动跟着章节回移到第 2 章
    assert client.delete(f"/project/{pid}/chapter/2").status_code == 200
    chapters = _chapters(pid)
    assert chapters[2]["character_actions"] == {cid: "去码头"}
    assert chapters[3]["character_actions"] == {cid: "留在城里"}
    assert chapters[1]["character_actions"] == {}

    # 重新打开作品后行动仍在
    assert client.delete(f"/project/{pid}").status_code == 200
    r = client.post("/projects/open", json={"path": str(root)})
    assert r.status_code == 200, r.text
    reopened = _chapters(pid)
    assert reopened[2]["character_actions"] == {cid: "去码头"}
    assert reopened[3]["character_actions"] == {cid: "留在城里"}

    # 动作不串章：改动第 2 章不碰第 3 章
    _char(pid, id=cid, name="甲", chapter_no=2, chapter_action="改去车站")
    assert _chapter(pid, 2)["character_actions"] == {cid: "改去车站"}
    assert _chapter(pid, 3)["character_actions"] == {cid: "留在城里"}


def test_old_meta_without_actions_and_old_character_fields_open_cleanly():
    pid = _create("旧数据打开")["id"]
    _add_chapter(pid)
    root = Path(client.get(f"/project/{pid}").json()["root_path"])

    meta_file = root / ".novel-gm" / "chapters" / "0001.json"
    meta = json.loads(meta_file.read_text(encoding="utf-8"))
    meta.pop("character_actions", None)
    meta_file.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

    proj_file = root / "作品.json"
    data = json.loads(proj_file.read_text(encoding="utf-8"))
    data["characters"] = [{"id": "old", "name": "旧人"}]
    proj_file.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")

    assert client.delete(f"/project/{pid}").status_code == 200
    r = client.post("/projects/open", json={"path": str(root)})
    assert r.status_code == 200, r.text
    ch = _chapter(pid, 1)
    assert ch["character_actions"] == {}
    old = next(c for c in client.get(f"/project/{pid}/characters").json() if c["id"] == "old")
    assert old["gender"] == "" and old["thinking_habits"] == ""


# ---------------------------------------------------------------- AI 上下文


def test_lore_view_prefers_master_profile():
    proj = Project(
        id="p-view",
        title="视图",
        created_at="2026-01-01T00:00:00+00:00",
        characters=[Character(id="c1", name="甲", gender="女", organization="影卫")],
    )
    lore = ChapterLore(
        characters=[
            Character(id="c1", name="甲", gender="", organization=""),
            Character(id="c2", name="乙", gender="男"),
        ]
    )
    view = lore_view(proj, lore)
    by_id = {c.id: c for c in view.characters}
    assert by_id["c1"].gender == "女" and by_id["c1"].organization == "影卫"
    assert by_id["c2"].gender == "男"  # 主档没有才用章节快照
    # 不修改原快照
    assert lore.characters[0].gender == ""


def test_ai_context_includes_new_fields_and_only_target_chapter_actions():
    pid = _create("AI 上下文")["id"]
    proj = Project(**client.get(f"/project/{pid}").json())
    proj.characters = [
        Character(
            id="c1",
            name="甲",
            gender="女",
            organization="影卫",
            thinking_habits="先观察再开口",
        )
    ]
    ch1 = Chapter(
        no=1,
        status="drafted",
        character_actions={"c1": "第一章旧行动", "c2": "别人的旧行动"},
    )
    ch2 = Chapter(no=2, status="planned", character_actions={"c1": "第二章：去开门"})
    ch3 = Chapter(no=3, status="planned", character_actions={"c1": "第三章：不要出现"})
    chapters = [ch1, ch2, ch3]

    layers = build_layered_context(proj, ChapterPlan(), chapters, target_chapter=2)
    assert "性别=女" in layers["L1"]
    assert "所属组织=影卫" in layers["L1"]
    assert "思维习惯=先观察再开口" in layers["L1"]
    assert "爱好=无" in layers["L1"]  # 空白字段如实写「无」，不编造
    assert "第二章：去开门" in layers["extra"]
    assert "待执行意图" in layers["extra"]
    assert "不是已发生事实" in layers["extra"]
    assert "不得写成已经发生/完成" in layers["extra"]
    assert "第一章旧行动" not in layers["extra"]
    assert "别人的旧行动" not in layers["extra"]
    assert "第三章：不要出现" not in layers["extra"]

    # 换目标章只读那一章的 action
    layers3 = build_layered_context(proj, ChapterPlan(), chapters, target_chapter=3)
    assert "第三章：不要出现" in layers3["extra"]
    assert "第二章：去开门" not in layers3["extra"]

    # 没有目标章时不泄露任何章节行动
    layers0 = build_layered_context(proj, ChapterPlan(), [], target_chapter=None)
    assert "待执行意图" not in layers0["extra"]


def test_context_revision_tracks_master_profile_and_chapter_actions():
    pid = _create("修订号")["id"]
    _add_chapter(pid)
    _add_chapter(pid)
    cid = _char(pid, name="甲")["id"]

    rev0 = context_revision(pid, 1)
    _char(pid, id=cid, name="甲", gender="女")
    rev1 = context_revision(pid, 1)
    assert rev1 != rev0

    _char(pid, id=cid, name="甲", chapter_no=1, chapter_action="去开门")
    rev2 = context_revision(pid, 1)
    assert rev2 != rev1

    # 第 2 章的行动不影响第 1 章的上下文修订号
    _char(pid, id=cid, name="甲", chapter_no=2, chapter_action="第二章行动")
    rev3 = context_revision(pid, 1)
    assert rev3 == rev2
    assert context_revision(pid, 2) != rev3
