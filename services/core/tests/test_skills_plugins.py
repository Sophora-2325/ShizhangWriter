from __future__ import annotations

import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import services.core.db as db
from services.core import skills
from services.core.context import assemble_messages
from services.core.main import app
from services.core.models import ChapterPlan
from services.core.skills import parse_skill_md

client = TestClient(app)
STRINGS = ROOT.parent / "apps" / "desktop-wpf" / "Strings"
X_KEY = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"

LAYERS = {"L0": "L0", "L1": "L1", "L2": "", "L3": "", "extra": "", "samples": ""}


@pytest.fixture(autouse=True)
def storage(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path)
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "t.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "projects")
    db.init_db()
    return tmp_path


def _skill(name="冷峻文风", applies_to="generate", instructions="句子要短。"):
    r = client.post("/skills", json={"name": name, "description": "说明", "applies_to": applies_to, "instructions": instructions})
    assert r.status_code == 200, r.text
    return r.json()


# ---- skills ----

def test_builtins_seeded_once_and_disabled(storage):
    first = client.get("/skills").json()
    assert {s["id"] for s in first} == set(skills.builtin_ids())
    assert {"de-ai", "chapter-frame", "narrative-order", "style-action", "dialogue-natural"} <= {s["id"] for s in first}
    assert not any(s["enabled"] for s in first)
    action = next(s for s in first if s["id"] == "style-action")
    assert (action["applies_to"], action["scenes"]) == ("generate", ["action"])
    for s in first:
        client.delete(f"/skills/{s['id']}")
    assert client.get("/skills").json() == []


def test_upgrade_adds_only_new_builtins(storage):
    with db.connect() as conn:
        db.set_setting(conn, "skills_seeded", "1")
    (storage / "skills" / "chapter-hook").mkdir(parents=True)
    (storage / "skills" / "chapter-hook" / "SKILL.md").write_text("---\nname: 我改过的\n---\n\n自定义", encoding="utf-8")
    ids = {s["id"] for s in client.get("/skills").json()}
    assert "dialogue-natural" not in ids  # 旧示例用户已删掉，不恢复
    assert "de-ai" in ids and "style-sorrow" in ids
    assert next(s for s in client.get("/skills").json() if s["id"] == "chapter-hook")["name"] == "我改过的"

    client.delete("/skills/de-ai")
    assert "de-ai" not in {s["id"] for s in client.get("/skills").json()}


def test_scene_skills_only_apply_to_matching_chapters():
    for s in client.get("/skills").json():
        client.patch(f"/skills/{s['id']}", json={"enabled": s["id"] in ("de-ai", "style-action", "style-romance", "style-suspense")})

    system = assemble_messages(LAYERS, "task", purpose="generate", scenes=["action", "感情"])[0]["content"]
    assert "## 去 AI 腔" in system and "## 文风·打斗" in system and "## 文风·感情" in system
    assert "## 文风·悬疑" not in system

    plain = assemble_messages(LAYERS, "task", purpose="generate")[0]["content"]
    assert "## 去 AI 腔" in plain and "## 文风·打斗" not in plain


def test_skill_scenes_round_trip_and_validation():
    s = _skill()
    r = client.put(f"/skills/{s['id']}", json={"name": "n", "instructions": "x", "scenes": ["Sorrow", "sorrow", "daily"]})
    assert r.json()["scenes"] == ["sorrow", "daily"]
    md = (db.STORAGE / "skills" / s["id"] / "SKILL.md").read_text(encoding="utf-8")
    assert "scenes: sorrow, daily" in md
    bad = client.put(f"/skills/{s['id']}", json={"name": "n", "instructions": "x", "scenes": ["war"]}, headers={"Accept-Language": "en"})
    assert bad.status_code == 400
    assert bad.json()["detail"]["message"] == "Unknown scene type: war"


def test_normalize_scenes_and_plan_field():
    assert skills.normalize_scenes(["打斗", "ROMANCE", "unknown", "action"]) == ["action", "romance"]
    assert ChapterPlan.model_validate({"scene_types": ["daily"]}).scene_types == ["daily"]


def test_every_scene_has_client_strings():
    for lang in ("zh-CN", "en-US", "ja-JP"):
        root = ET.parse(STRINGS / f"{lang}.xaml").getroot()
        keys = {el.get(X_KEY) for el in root}
        missing = [s for s in skills.SCENES if f"Scene.{s}" not in keys]
        assert not missing, f"{lang} 缺少 {missing}"


def test_skill_crud_and_toggle(storage):
    s = _skill()
    assert s["enabled"] is False
    md = (storage / "skills" / s["id"] / "SKILL.md").read_text(encoding="utf-8")
    meta, body = parse_skill_md(md)
    assert meta == {"name": "冷峻文风", "description": "说明", "applies_to": "generate"}
    assert body == "句子要短。"

    r = client.put(f"/skills/{s['id']}", json={"name": "冷峻", "description": "", "applies_to": "both", "instructions": "少用形容词。"})
    assert r.json()["applies_to"] == "both"
    assert client.patch(f"/skills/{s['id']}", json={"enabled": True}).json()["enabled"] is True
    assert [x["enabled"] for x in client.get("/skills").json() if x["id"] == s["id"]] == [True]

    assert client.delete(f"/skills/{s['id']}").status_code == 200
    assert client.patch(f"/skills/{s['id']}", json={"enabled": True}).status_code == 404


def test_skill_validation_is_translated():
    r = client.post("/skills", json={"name": " ", "instructions": "x"}, headers={"Accept-Language": "en"})
    assert r.status_code == 400
    assert r.json()["detail"]["message"] == "The skill name cannot be empty"
    r = client.post("/skills", json={"name": "a", "instructions": "x", "applies_to": "all"})
    assert r.json()["detail"]["message"] == "适用范围只能是 plan、generate 或 both"
    assert client.put("/skills/..", json={"name": "a", "instructions": "x"}).status_code == 404


def test_skill_import_file_and_folder(tmp_path):
    src = tmp_path / "src"
    src.mkdir()
    md = src / "伏笔.md"
    md.write_text("---\nname: 伏笔回收\napplies_to: plan   # plan | generate | both\n---\n\n每章回收一个伏笔。\n", encoding="utf-8")
    s = client.post("/skills/import", json={"path": str(md)}).json()
    assert (s["name"], s["applies_to"], s["instructions"]) == ("伏笔回收", "plan", "每章回收一个伏笔。")

    folder = tmp_path / "pack"
    folder.mkdir()
    (folder / "SKILL.md").write_text("只有正文，没有 frontmatter。", encoding="utf-8")
    (folder / "ref.txt").write_text("参考资料", encoding="utf-8")
    s2 = client.post("/skills/import", json={"path": str(folder)}).json()
    assert (s2["name"], s2["applies_to"]) == ("pack", "both")
    assert (db.STORAGE / "skills" / s2["id"] / "ref.txt").is_file()

    assert client.post("/skills/import", json={"path": str(tmp_path / "none")}).status_code == 400
    assert client.post("/skills/import", json={"path": str(tmp_path)}).json()["detail"]["message"] == "文件夹里没有 SKILL.md"
    (tmp_path / "a.txt").write_text("x", encoding="utf-8")
    r = client.post("/skills/import", json={"path": str(tmp_path / "a.txt")}, headers={"Accept-Language": "ja"})
    assert r.json()["detail"]["message"] == "インポートできるのは .md ファイルか、SKILL.md を含むフォルダーだけです"


def test_enabled_skills_go_into_system_message_by_purpose():
    for s in client.get("/skills").json():
        client.delete(f"/skills/{s['id']}")
    gen = _skill("冷峻文风", "generate", "句子要短。")
    plan = _skill("伏笔规划", "plan", "每章埋一个伏笔。")
    both = _skill("章末悬念", "both", "结尾留钩子。")
    _skill("未启用", "both", "不应出现。")
    for s in (gen, plan, both):
        client.patch(f"/skills/{s['id']}", json={"enabled": True})

    system_gen = assemble_messages(LAYERS, "task", purpose="generate")[0]["content"]
    assert "写作技能" in system_gen and "句子要短。" in system_gen and "结尾留钩子。" in system_gen
    assert "每章埋一个伏笔。" not in system_gen and "不应出现。" not in system_gen

    system_plan = assemble_messages(LAYERS, "task", purpose="plan")[0]["content"]
    assert "每章埋一个伏笔。" in system_plan and "句子要短。" not in system_plan

    assert assemble_messages(LAYERS, "task")[0]["content"] == "L0\n\nL1"


def _builtin_text(sid: str) -> str:
    return (skills.BUILTIN_DIR / sid / "SKILL.md").read_text(encoding="utf-8")


def _user_file(sid: str) -> Path:
    return db.STORAGE / "skills" / sid / "SKILL.md"


def _hashes() -> dict:
    with db.connect() as conn:
        return json.loads(db.get_setting(conn, "skills_builtin_hashes", "{}") or "{}")


def test_builtins_have_source_and_record_hashes():
    listed = client.get("/skills").json()
    assert all(s["builtin"] and not s["update_available"] for s in listed)
    assert all(s["source"] for s in listed)
    assert _hashes() == {sid: skills.content_hash(_builtin_text(sid)) for sid in skills.builtin_ids()}


def test_unmodified_builtin_is_updated_automatically():
    client.get("/skills")
    old = "---\nname: 去 AI 腔\n---\n\n旧版指令"
    _user_file("de-ai").write_text(old, encoding="utf-8")
    with db.connect() as conn:
        db.set_setting(conn, "skills_builtin_hashes", json.dumps({**_hashes(), "de-ai": skills.content_hash(old)}))
    client.patch("/skills/de-ai", json={"enabled": True})
    s = next(x for x in client.get("/skills").json() if x["id"] == "de-ai")
    assert _user_file("de-ai").read_text(encoding="utf-8") == _builtin_text("de-ai")
    assert s["enabled"] is True and s["update_available"] is False
    assert _hashes()["de-ai"] == skills.content_hash(_builtin_text("de-ai"))


def test_legacy_v1_copy_is_updated_without_recorded_hash(monkeypatch):
    client.get("/skills")
    old = "---\nname: 章末钩子\n---\n\n第一版"
    _user_file("chapter-hook").write_bytes(("\ufeff" + old.replace("\n", "\r\n")).encode("utf-8"))
    with db.connect() as conn:
        db.set_setting(conn, "skills_builtin_hashes", "")
    monkeypatch.setitem(skills._V1_HASHES, "chapter-hook", skills.content_hash(old))
    client.get("/skills")
    assert _user_file("chapter-hook").read_text(encoding="utf-8") == _builtin_text("chapter-hook")


def test_modified_builtin_is_kept_and_can_be_reset():
    client.get("/skills")
    old = "---\nname: 旧内置\n---\n\n旧版"
    with db.connect() as conn:
        db.set_setting(conn, "skills_builtin_hashes", json.dumps({**_hashes(), "style-sorrow": skills.content_hash(old)}))
    r = client.put("/skills/style-sorrow", json={"name": "我的悲情", "instructions": "我自己的写法"})
    assert r.json()["update_available"] is True
    s = next(x for x in client.get("/skills").json() if x["id"] == "style-sorrow")
    assert (s["name"], s["builtin"], s["update_available"]) == ("我的悲情", True, True)

    r = client.post("/skills/style-sorrow/reset").json()
    assert r["update_available"] is False and r["name"] != "我的悲情"
    assert _user_file("style-sorrow").read_text(encoding="utf-8") == _builtin_text("style-sorrow")

    # 已经采用过当前版本、之后又改过的副本不提示更新
    client.put("/skills/style-sorrow", json={"name": "再改", "instructions": "x"})
    assert next(x for x in client.get("/skills").json() if x["id"] == "style-sorrow")["update_available"] is False

    mine = _skill()
    r = client.post(f"/skills/{mine['id']}/reset", headers={"Accept-Language": "en"})
    assert r.status_code == 400 and r.json()["detail"]["message"] == "Only built-in skills can be restored to the built-in version"


def test_skill_source_round_trip():
    r = client.post("/skills", json={"name": "来源", "instructions": "x", "source": "https://a.example/p#frag ; 《某书》 #2"})
    s = r.json()
    assert s["source"] == "https://a.example/p#frag ; 《某书》#2"
    md = _user_file(s["id"]).read_text(encoding="utf-8")
    meta, _ = parse_skill_md(md)
    assert meta["source"] == s["source"]
    assert next(x for x in client.get("/skills").json() if x["id"] == s["id"])["source"] == s["source"]
    assert s["builtin"] is False


# ---- plugins ----

def _plugin_folder(tmp_path: Path, manifest: dict | str, name="plug") -> Path:
    d = tmp_path / name
    d.mkdir()
    text = manifest if isinstance(manifest, str) else json.dumps(manifest, ensure_ascii=False)
    (d / "plugin.json").write_text(text, encoding="utf-8")
    (d / "main.py").write_text("raise SystemExit('must not run')", encoding="utf-8")
    return d


def test_plugin_import_toggle_delete(tmp_path):
    assert client.get("/plugins").json() == []
    folder = _plugin_folder(tmp_path, {"id": "word-count", "name": "字数统计", "version": "1.0.0", "author": "me", "hooks": ["after_generate"]})
    p = client.post("/plugins/import", json={"path": str(folder)}).json()
    assert (p["id"], p["name"], p["version"], p["author"], p["hooks"], p["enabled"]) == (
        "word-count", "字数统计", "1.0.0", "me", ["after_generate"], False)
    assert (db.STORAGE / "plugins" / "word-count" / "main.py").is_file()

    dup = client.post("/plugins/import", json={"path": str(folder / "plugin.json")}, headers={"Accept-Language": "en"})
    assert dup.json()["detail"]["message"] == "A plugin with the id word-count already exists"

    assert client.patch("/plugins/word-count", json={"enabled": True}).json()["enabled"] is True
    assert client.get("/plugins").json()[0]["enabled"] is True
    assert client.patch("/plugins/word-count", json={"enabled": False}).json()["enabled"] is False
    assert client.delete("/plugins/word-count").status_code == 200
    assert client.get("/plugins").json() == []
    assert client.delete("/plugins/word-count").json()["detail"]["message"] == "插件不存在"


def test_plugin_manifest_validation(tmp_path):
    def err(folder, lang="zh"):
        r = client.post("/plugins/import", json={"path": str(folder)}, headers={"Accept-Language": lang})
        assert r.status_code == 400
        return r.json()["detail"]["message"]

    assert err(tmp_path) == "文件夹里没有 plugin.json"
    assert err(_plugin_folder(tmp_path, "{oops", "bad")).startswith("plugin.json 不是有效的 JSON：")
    missing = _plugin_folder(tmp_path, {"id": "x"}, "missing")
    assert err(missing) == "plugin.json 缺少必填字段：name, version"
    assert err(missing, "en") == "plugin.json is missing required fields: name, version"
    assert err(_plugin_folder(tmp_path, {"id": "../x", "name": "n", "version": "1"}, "badid")) == "插件 id 只能包含字母、数字、点、下划线和连字符"
