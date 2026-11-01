"""Mode A contracts. All model calls use a mock or a capturing spy."""
from copy import deepcopy
from pathlib import Path
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient

from services.core import db, main, proposals, proposal_inputs, workspace as ws
from services.core.models import Chapter, ChapterPlan, ProposalOutput, ProposalCommand
from services.core.repository import get_chapter, get_project, list_chapters, upsert_chapter
from services.core.providers.mock import MockProvider

client = TestClient(main.app)


@pytest.fixture(autouse=True)
def storage(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path / "storage")
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "storage" / "t.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "storage" / "projects")
    db.init_db()


def work(tmp_path, plan=None):
    r = client.post("/project", json={"title": "方案测试", "parent_dir": str(tmp_path), "style": {},
        "characters": [], "threads": [], "entries": []})
    assert r.status_code == 200, r.text
    pid = r.json()["id"]
    client.patch(f"/project/{pid}/settings", json={"provider": "mock"})
    r = client.post(f"/project/{pid}/chapters", json={"plan": plan or {"free_text": "作者原文", "misc": "补充"}})
    assert r.status_code == 200
    return pid


def state(pid, no=1):
    r = client.get(f"/project/{pid}/chapter/{no}/proposal")
    assert r.status_code == 200, r.text
    return r.json()


def command(pid, no=1, **kwargs):
    s = state(pid, no)
    return {"chapter_uid": s["chapter"]["chapter_uid"], "expected_revision": s["workspace"]["revision"],
            "input_token": s["input_token"], "request_id": uuid4().hex, **kwargs}


def generate(pid, no=1, **kwargs):
    r = client.post(f"/project/{pid}/chapter/{no}/proposal", json=command(pid, no, **kwargs))
    assert r.status_code == 200, r.text
    return r.json()


def adopt(pid, no=1):
    r = client.post(f"/project/{pid}/chapter/{no}/proposal/confirm", json=command(pid, no))
    assert r.status_code == 200, r.text
    return r.json()


def answer(pid, value, qid=None):
    qid = qid or (state(pid)["workspace"]["draft"] or state(pid)["workspace"]["adopted"])["questions"][0]["id"]
    return client.put(f"/project/{pid}/chapter/1/proposal/answers", json=command(pid, answers={qid: value}))


@pytest.mark.parametrize("plan", [ChapterPlan(present_character_ids=["c"]), ChapterPlan(required_threads=["t"]),
    ChapterPlan(scene_types=["daily"]), ChapterPlan(target_word_count=6000), ChapterPlan(free_text="手写", misc="补充")])
def test_reference_and_word_count_are_not_plot(plan):
    assert main._plan_form_empty(plan)


def test_uid_roundtrip_import_insert_delete_and_legacy_save(tmp_path):
    pid = work(tmp_path)
    uid = get_chapter(pid, 1).chapter_uid
    generate(pid)
    old = Chapter(no=1, plan=ChapterPlan(scene_goal="旧客户端保存"))
    upsert_chapter(pid, old)
    assert get_chapter(pid, 1).proposal.draft
    assert get_chapter(pid, 1).chapter_uid == uid
    from services.core.repository import import_folder, insert_chapter, delete_chapter
    root = get_project(pid).root_path
    import_folder(root)
    assert get_chapter(pid, 1).chapter_uid == uid
    second = insert_chapter(pid, 1)
    assert second.chapter_uid != uid and second.proposal.revision == 0
    middle = insert_chapter(pid, 1)
    assert get_chapter(pid, 3).chapter_uid == second.chapter_uid
    delete_chapter(pid, 2)
    assert get_chapter(pid, 2).chapter_uid == second.chapter_uid


def test_save_answers_can_edit_plot_outline_without_touching_adopted(tmp_path):
    pid = work(tmp_path)
    generate(pid)
    old = state(pid)["workspace"]["draft"]["plot_outline"]
    r = client.put(f"/project/{pid}/chapter/1/proposal/answers",
                   json=command(pid, plot_outline="作者手改的剧情大纲"))
    assert r.status_code == 200, r.text
    assert r.json()["workspace"]["draft"]["plot_outline"] == "作者手改的剧情大纲"
    assert old != "作者手改的剧情大纲"
    r = client.put(f"/project/{pid}/chapter/1/proposal/answers", json=command(pid, plot_outline=""))
    assert r.status_code == 200, r.text
    assert r.json()["workspace"]["draft"]["plot_outline"] == "作者手改的剧情大纲"
    adopt(pid)
    original = get_chapter(pid, 1).proposal.adopted.plot_outline
    r = client.put(f"/project/{pid}/chapter/1/proposal/answers",
                   json=command(pid, plot_outline="从已采用稿改出的草稿"))
    assert r.status_code == 200, r.text
    assert get_chapter(pid, 1).proposal.adopted.plot_outline == original
    assert r.json()["workspace"]["draft"]["plot_outline"] == "从已采用稿改出的草稿"


def test_generation_answers_and_restore_protect_author_data(tmp_path):
    pid = work(tmp_path, {"scene_goal": "作者目标", "free_text": "不要改", "misc": "手写杂项", "must_not": ["旧禁止"]})
    before = get_chapter(pid, 1)
    s = generate(pid)
    assert get_chapter(pid, 1).plan == before.plan
    assert answer(pid, "保留这个回答").status_code == 200
    assert get_chapter(pid, 1).plan == before.plan
    s = adopt(pid)
    assert s["workspace"]["draft"] is None
    assert s["adopted_status"]["state"] == "adopted", s
    ch = get_chapter(pid, 1)
    assert (ch.plan.free_text, ch.plan.misc, ch.plan.must_not) == ("不要改", "手写杂项", ["旧禁止"])
    record = ch.proposal.adopted.active_history_id
    original_adopted = ch.proposal.adopted.model_dump(mode="json")
    assert answer(pid, "新草稿答案").status_code == 200
    assert get_chapter(pid, 1).proposal.adopted.model_dump(mode="json") == original_adopted
    r = client.post(f"/project/{pid}/chapter/1/plans/{record}/restore", json=command(pid))
    assert r.status_code == 200, r.text
    assert r.json()["workspace"]["draft"]["id"] != original_adopted["id"]
    assert r.json()["workspace"]["draft"]["validation"] is None
    assert r.json()["adopted_status"]["state"] == "adopted"


def test_new_draft_and_answers_do_not_enter_body_prompt_or_recheck(tmp_path, monkeypatch):
    pid = work(tmp_path)
    generate(pid); answer(pid, "已采用筹码"); adopt(pid)
    generate(pid); answer(pid, "绝不能泄漏的新答案")
    purposes, prompts = [], []
    chat = MockProvider.chat
    def spy(self, request):
        purposes.append(request.get("purpose"))
        prompts.append(str(request["messages"]))
        return chat(self, request)
    monkeypatch.setattr(MockProvider, "chat", spy)
    r = client.post(f"/project/{pid}/chapter", json={"confirm_plan": True})
    assert r.status_code == 200, r.text
    prompt = prompts[purposes.index("generate")]
    assert "已采用筹码" in prompt and "绝不能泄漏的新答案" not in prompt
    assert "proposal_check" not in purposes and "rhythm_alignment" not in purposes
    assert state(pid)["adopted_status"]["state"] == "adopted"


def test_unadopted_draft_never_enters_ordinary_generation(tmp_path, monkeypatch):
    pid = work(tmp_path, {"scene_goal": "当前有效规划"})
    generate(pid); answer(pid, "未采用答案XYZ")
    seen = []
    chat = MockProvider.chat
    def spy(self, req):
        if req.get("purpose") == "generate": seen.append(str(req["messages"]))
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    r = client.post(f"/project/{pid}/chapter", json={})
    assert r.status_code == 200, r.text
    assert "当前有效规划" in seen[0] and "未采用答案XYZ" not in seen[0]
    assert "作者已采用的具体剧情方案" not in seen[0]


@pytest.mark.parametrize("count", [0, 1, 3])
def test_question_bounds_and_model_cannot_answer(tmp_path, monkeypatch, count):
    import json
    pid = work(tmp_path)
    chat = MockProvider.chat
    def spy(self, req):
        if req.get("purpose") == "proposal":
            return {"content": json.dumps({"plot_outline": "剧情", "proposed_plan": {}, "questions": [
                {"question": f"问题{i}", "answer": "模型偷偷作答", "options": []} for i in range(count)]})}
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    d = generate(pid)["workspace"]["draft"]
    assert len(d["questions"]) == count
    assert all(q["answer"] == "" and q["id"] for q in d["questions"])
    assert adopt(pid)["adopted_status"]["state"] == "adopted"


@pytest.mark.parametrize("bad", [{"questions": [{"question": "q"}] * 4},
    {"questions": [{"question": "q" * 121}]}, {"questions": [{"question": "q", "why": "w" * 301}]},
    {"questions": [{"question": "q", "options": ["1", "2", "3", "4"]}]},
    {"proposed_plan": {"free_text": "危险覆盖"}}])
def test_output_schema_rejects_invalid(bad):
    with pytest.raises(ValueError): ProposalOutput(**({"plot_outline": "剧情"} | bad))


def test_clear_unknown_id_and_regenerate_inherits_decisions(tmp_path, monkeypatch):
    pid = work(tmp_path); generate(pid)
    assert answer(pid, "", "unknown").status_code == 422
    assert answer(pid, "a" * 4001).status_code == 422
    assert answer(pid, "作者选择").status_code == 200
    old_q = get_chapter(pid, 1).proposal.draft.questions[0].id
    s = generate(pid, mode="answers")
    draft = s["workspace"]["draft"]
    assert draft["author_decisions"][0]["question_id"] == old_q
    assert draft["questions"][0]["id"] != old_q and draft["questions"][0]["answer"] == ""
    did = draft["author_decisions"][0]["id"]
    r = client.put(f"/project/{pid}/chapter/1/proposal/answers", json=command(pid, decisions={did: ""}))
    assert r.status_code == 200 and r.json()["workspace"]["draft"]["author_decisions"][0]["answer"] == ""


def test_idempotent_paid_calls_revision_and_payload_conflict(tmp_path, monkeypatch):
    pid = work(tmp_path); calls = []
    chat = MockProvider.chat
    def spy(self, req):
        calls.append(req.get("purpose")); return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    cmd = command(pid)
    first = client.post(f"/project/{pid}/chapter/1/proposal", json=cmd)
    assert first.status_code == 200, first.text
    # Receipt survives reinitialization and ignores obsolete expected_revision on replay.
    db.init_db()
    assert client.post(f"/project/{pid}/chapter/1/proposal", json=cmd).json() == first.json()
    assert calls.count("proposal") == 1
    assert client.post(f"/project/{pid}/chapter/1/proposal", json=cmd | {"mode": "answers"}).status_code == 409
    assert client.post(f"/project/{pid}/chapter/1/proposal", json=cmd | {"request_id": uuid4().hex}).status_code == 409
    answer(pid, "后续保存")
    replay = client.post(f"/project/{pid}/chapter/1/proposal", json=cmd)
    assert replay.json()["workspace"]["revision"] == state(pid)["workspace"]["revision"]
    assert replay.json()["workspace"]["draft"]["questions"][0]["answer"] == "后续保存"
    assert calls.count("proposal") == 1


def test_history_protection_trim_and_uid_delete(tmp_path):
    pid = work(tmp_path); generate(pid); adopted = adopt(pid)["workspace"]["adopted"]
    for _ in range(32): generate(pid)
    rows = client.get(f"/project/{pid}/chapter/1/plans").json()
    assert len(rows) == 30
    assert adopted["active_history_id"] in {r["record_id"] for r in rows}
    for row in rows:
        if row["protected"]:
            assert client.request("DELETE", f"/project/{pid}/chapter/1/plans/{row['record_id']}", json=command(pid)).status_code == 409
    from services.core.repository import delete_chapter
    directory = Path(get_project(pid).root_path) / ".novel-gm" / "plans" / get_chapter(pid, 1).chapter_uid
    delete_chapter(pid, 1)
    assert not directory.exists()


def test_interruption_journal_recovers_files_meta_db_and_receipt(tmp_path, monkeypatch):
    pid = work(tmp_path); cmd = command(pid)
    original = proposals.apply_history_files
    def fail(*args): raise OSError("模拟断电")
    monkeypatch.setattr(proposals, "apply_history_files", fail)
    assert client.post(f"/project/{pid}/chapter/1/proposal", json=cmd).status_code == 503
    root = Path(get_project(pid).root_path)
    assert (root / ".novel-gm" / "pending-save.json").is_file()
    body_file = root / ws.read_json(ws.meta_path(root, 1))["file"]
    body_file.write_text("中断之后，外部刚保存的新正文", encoding="utf-8")
    monkeypatch.setattr(proposals, "apply_history_files", original)
    s = state(pid)
    assert s["workspace"]["draft"]
    assert get_chapter(pid, 1).body_text == "中断之后，外部刚保存的新正文"
    assert not (root / ".novel-gm" / "pending-save.json").exists()
    assert ws.read_json(ws.meta_path(root, 1))["proposal"] == s["workspace"]
    replay = client.post(f"/project/{pid}/chapter/1/proposal", json=cmd)
    assert replay.status_code == 200 and replay.json()["workspace"] == s["workspace"]


def test_interrupted_model_request_not_replayed(tmp_path):
    pid = work(tmp_path); cmd = command(pid)
    with db.connect() as conn:
        conn.execute("INSERT INTO proposal_receipts VALUES(?,?,?,?,?,?,?)", (pid, cmd["chapter_uid"], cmd["request_id"],
            proposal_inputs.digest({"action": "generate", "command": ProposalCommand(**cmd)}), "running", None, db.utcnow().isoformat()))
    proposals.recover_requests()
    r = client.post(f"/project/{pid}/chapter/1/proposal", json=cmd)
    assert r.status_code == 409 and r.json()["detail"]["code"] == "REQUEST_INTERRUPTED"


def test_model_result_rejected_after_author_input_changes(tmp_path, monkeypatch):
    pid = work(tmp_path); chat = MockProvider.chat
    def spy(self, req):
        if req.get("purpose") == "proposal":
            ch = get_chapter(pid, 1); ch.plan.misc = "模型期间编辑"; upsert_chapter(pid, ch)
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    r = client.post(f"/project/{pid}/chapter/1/proposal", json=command(pid))
    assert r.status_code == 409 and state(pid)["workspace"]["draft"] is None
    assert get_chapter(pid, 1).plan.misc == "模型期间编辑"


def test_new_draft_during_body_generation_is_preserved_and_not_a_conflict(tmp_path, monkeypatch):
    pid = work(tmp_path); generate(pid); adopt(pid)
    from services.core import generation
    chat = MockProvider.chat
    def spy(self, req):
        if req.get("purpose") == "generate": generate(pid)
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    r = client.post(f"/project/{pid}/chapter", json={})
    assert r.status_code == 200, r.text
    assert get_chapter(pid, 1).proposal.draft


def test_invalid_or_unsupported_check_never_adopts(tmp_path, monkeypatch):
    pid = work(tmp_path); generate(pid); before = get_chapter(pid, 1).plan
    chat = MockProvider.chat
    def spy(self, req):
        if req.get("purpose") == "proposal_check": return {"content": '{"status":"passed"}'}
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    r = client.post(f"/project/{pid}/chapter/1/proposal/confirm", json=command(pid))
    assert r.status_code == 503 and r.json()["detail"]["code"] == "PROPOSAL_CHECK_FAILED"
    assert get_chapter(pid, 1).proposal.adopted is None and get_chapter(pid, 1).plan == before


def test_evidence_mismatch_has_both_locatable_quotes(tmp_path, monkeypatch):
    import json
    pid = work(tmp_path, {"scene_goal": "必须等待日落", "free_text": "必须等待日落"}); generate(pid)
    chat = MockProvider.chat
    def spy(self, req):
        if req.get("purpose") == "proposal_check":
            return {"content": json.dumps({"status": "mismatch", "issues": [{"left_id": "plan.free_text",
                "left_quote": "必须等待日落", "right_id": "proposal.plot_outline", "right_quote": "会面与对峙", "reason": "明确时机冲突"}]})}
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    r = client.post(f"/project/{pid}/chapter/1/proposal/confirm", json=command(pid))
    assert r.status_code == 409, r.text
    issue = r.json()["detail"]["issues"][0]
    assert issue["left"]["field"] == "free_text" and issue["right"]["kind"] == "proposal"


def test_author_change_invalidates_but_body_growth_does_not(tmp_path):
    pid = work(tmp_path); generate(pid); adopt(pid)
    ch = get_chapter(pid, 1); ch.body_text = "续写正文"; upsert_chapter(pid, ch)
    assert state(pid)["adopted_status"]["state"] == "adopted"
    ch = get_chapter(pid, 1); ch.plan.misc += "改动"; upsert_chapter(pid, ch)
    assert state(pid)["adopted_status"]["state"] == "stale"


def test_missing_directory_readable_but_no_new_proposal_writes(tmp_path):
    pid = work(tmp_path); generate(pid)
    root = Path(get_project(pid).root_path)
    root.rename(root.with_name(root.name + "-moved"))
    assert state(pid)["workspace"]["draft"]
    assert not state(pid)["writable"]
    r = client.post(f"/project/{pid}/chapter/1/proposal", json=command(pid))
    assert r.status_code == 409


def test_effective_lore_rules_skills_rhythm_and_vertical_fingerprints(tmp_path, monkeypatch):
    from services.core.models import Character, LoreEntry, StackedConstraints
    from services.core.repository import save_project
    from services.core import skills
    pid = work(tmp_path, {"scene_goal": "等待来信", "present_character_ids": ["c"]})
    project = get_project(pid)
    project.characters = [Character(id="c", name="林舟", personality="谨慎")]
    save_project(project)
    ch = get_chapter(pid, 1)
    ch.lore.characters = [project.characters[0].model_copy(deep=True)]
    ch.lore.entries = [LoreEntry(id="hard-rule", category="rule", hard=True, title="世界规则", content="不能复活")]
    ch.stacked_constraints = StackedConstraints(tracks=[{"id": "track", "domain": "character", "element_id": "c", "name": "林舟", "weight": .5,
        "segments": [{"id": "seg", "start": 10, "end": 40, "directive": "只展示来信，不揭晓身份"}]}])
    upsert_chapter(pid, ch); generate(pid); adopt(pid)
    original = get_chapter(pid, 1)
    before = state(pid)["input_token"]
    ch = original.model_copy(deep=True); ch.stacked_constraints.tracks[0].segments[0].top = 50
    ch.stacked_constraints.tracks[0].segments[0].bottom = 95
    upsert_chapter(pid, ch)
    assert state(pid)["adopted_status"]["state"] == "adopted" and state(pid)["input_token"] == before
    for mutate in (lambda c: setattr(c.stacked_constraints.tracks[0], "weight", 1),
                   lambda c: setattr(c.stacked_constraints.tracks[0].segments[0], "start", 20),
                   lambda c: setattr(c.stacked_constraints.tracks[0].segments[0], "directive", "改为揭晓身份"),
                   lambda c: setattr(c.lore.entries[0], "content", "可以复活"),
                   lambda c: c.character_actions.update({"c": "立即离开"})):
        ch = original.model_copy(deep=True); mutate(ch); upsert_chapter(pid, ch)
        assert state(pid)["adopted_status"]["state"] == "stale"
    upsert_chapter(pid, original)
    assert state(pid)["adopted_status"]["state"] == "adopted"
    project.characters[0].personality = "冲动"; save_project(project)
    assert "lore_changed" in state(pid)["adopted_status"]["reasons"]
    project.characters[0].personality = "谨慎"; save_project(project)
    monkeypatch.setattr(skills, "skills_prompt", lambda *args, **kwargs: "新增的实际生效写作技能")
    assert state(pid)["adopted_status"]["state"] == "stale"


def test_actual_previous_chapter_dependencies_are_hashed(tmp_path):
    pid = work(tmp_path)
    ch = get_chapter(pid, 1); ch.body_text = "开头" + "中" * 1500 + "旧结尾"; ch.status = "drafted"
    # Assignment uses enums in normal routes.
    from services.core.models import ChapterStatus
    ch.status = ChapterStatus.drafted; upsert_chapter(pid, ch)
    assert client.post(f"/project/{pid}/chapters", json={"plan": {"scene_goal": "新章"}}).status_code == 200
    generate(pid, 2); adopt(pid, 2)
    assert state(pid, 2)["adopted_status"]["state"] == "adopted"
    ch = get_chapter(pid, 1); ch.body_text = ch.body_text[:500] + "改" + ch.body_text[501:]; upsert_chapter(pid, ch)
    assert state(pid, 2)["adopted_status"]["state"] == "adopted"  # unchanged actual fallback/ending
    ch = get_chapter(pid, 1); ch.body_text += "新的衔接原文"; upsert_chapter(pid, ch)
    assert "history_changed" in state(pid, 2)["adopted_status"]["reasons"]


def test_renumbered_or_deleted_model_target_never_writes_other_chapter(tmp_path, monkeypatch):
    from services.core.repository import insert_chapter
    pid = work(tmp_path)
    client.post(f"/project/{pid}/chapters", json={})
    second_uid = get_chapter(pid, 2).chapter_uid
    chat = MockProvider.chat
    def spy(self, req):
        if req.get("purpose") == "proposal": insert_chapter(pid, 1)
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    r = client.post(f"/project/{pid}/chapter/2/proposal", json=command(pid, 2))
    assert r.status_code == 409
    assert get_chapter(pid, 3).chapter_uid == second_uid and get_chapter(pid, 3).proposal.draft is None
    assert get_chapter(pid, 2).proposal.draft is None


def test_merge_preview_and_commit_share_union_and_author_value_rules(tmp_path, monkeypatch):
    from services.core.models import ProposalPlanPatch
    from services.core.repository import save_project
    pid = work(tmp_path, {"scene_goal": "旧目标", "must_include": ["先保留原要求"], "must_not": ["不得泄密"],
                         "scene_types": ["daily"], "free_text": "作者原文", "misc": "手写补充", "open_decision_for_user": "是否离开"})
    s = generate(pid); draft = get_chapter(pid, 1).proposal.draft
    draft.proposed_plan = ProposalPlanPatch(scene_goal="新目标", must_include=["先保留原要求", "再加入事件"], must_not=["不得泄密"], scene_types=["suspense"])
    ch = get_chapter(pid, 1); ch.proposal.draft = draft
    upsert_chapter(pid, ch, proposal_write=True)
    before_diff = state(pid)["diff"]
    adopted = adopt(pid)
    plan = adopted["chapter"]["plan"]
    assert plan["must_include"] == ["先保留原要求", "再加入事件"] and plan["must_not"] == ["不得泄密"]
    assert plan["open_decision_for_user"] == "是否离开"  # no author answer to release it
    assert plan["scene_types"] == ["daily", "suspense"]
    assert all(plan[d["field"]] == d["after"] for d in before_diff)
    assert (plan["free_text"], plan["misc"]) == ("作者原文", "手写补充")


def test_checks_audit_and_generation_receive_full_adopted_plot_answers_and_ending(tmp_path, monkeypatch):
    from services.core.audit import run_plan_check
    pid = work(tmp_path); generate(pid); answer(pid, "作者采用回答"); adopt(pid)
    generate(pid); answer(pid, "未采用的秘密回答")
    ch = get_chapter(pid, 1); ch.body_text = "开场" + "中" * 8100 + "章末必须核对的证据XYZ"; upsert_chapter(pid, ch)
    seen = []; chat = MockProvider.chat
    def spy(self, req):
        seen.append(req); return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    result = run_plan_check(get_project(pid), get_chapter(pid, 1), list_chapters(pid), semantic=True)
    prompt = str(next(r for r in seen if r["purpose"] == "audit")["messages"])
    assert "作者采用回答" in prompt and "未采用的秘密回答" not in prompt
    assert "章末必须核对的证据XYZ" in prompt


def test_adoption_after_dependency_change_requires_a_fresh_final_input_check(tmp_path, monkeypatch):
    pid = work(tmp_path); generate(pid)
    ch = get_chapter(pid, 1); ch.plan.misc = "新增作者要求"; upsert_chapter(pid, ch)
    assert state(pid)["draft_status"]["state"] == "stale"
    seen=[]; chat=MockProvider.chat
    def spy(self, req):
        if req["purpose"] == "proposal_check": seen.append(str(req["messages"]))
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    assert adopt(pid)["adopted_status"]["state"] == "adopted"
    assert "新增作者要求" in seen[0]


def test_skills_change_during_body_call_rejects_old_result_and_corrupt_snapshot_is_stale(tmp_path, monkeypatch):
    from services.core import skills
    pid = work(tmp_path); generate(pid); adopt(pid)
    original = get_chapter(pid, 1)
    broken = original.model_copy(deep=True); broken.proposal.adopted.final_plan.scene_goal = "错误的快照内容"
    upsert_chapter(pid, broken, proposal_write=True)
    assert state(pid)["adopted_status"]["state"] == "stale"
    upsert_chapter(pid, original, proposal_write=True)
    chat = MockProvider.chat
    def spy(self, req):
        if req["purpose"] == "generate": monkeypatch.setattr(skills, "skills_prompt", lambda *a, **kw: "运行期间修改的写作技能")
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    r = client.post(f"/project/{pid}/chapter", json={})
    assert r.status_code == 409 and get_chapter(pid, 1).body_text == original.body_text


def test_read_and_answer_save_never_call_models_even_with_remote_embeddings_configured(tmp_path, monkeypatch):
    from services.core import retrieval
    from services.core.models import ChapterSummary, ChapterStatus
    from services.core.repository import add_fact
    from services.core.freshness import stamp
    pid = work(tmp_path)
    ch = get_chapter(pid, 1); ch.body_text = "来信已经到了"; ch.status = ChapterStatus.drafted; upsert_chapter(pid, ch)
    ch = get_chapter(pid, 1); ch.summary = ChapterSummary(one_line="来信已经到了"); stamp(ch, "summary"); upsert_chapter(pid, ch)
    add_fact(pid, 1, "来信在桌上", [])
    client.post(f"/project/{pid}/chapters", json={"plan": {"scene_goal": "来信"}})
    monkeypatch.setenv("NOVEL_GM_EMBEDDING_URL", "http://127.0.0.1:9999/v1")
    monkeypatch.setenv("NOVEL_GM_EMBEDDING_MODEL", "never-call-for-view")
    def forbidden(*args, **kwargs): raise AssertionError("read-only proposal inputs must not call embedding models")
    monkeypatch.setattr(retrieval, "_embed", forbidden)
    generated = generate(pid, 2)
    assert "来信在桌上" in generated["workspace"]["draft"]["source_manifest"]["layers"]["L3"]
    monkeypatch.setattr(MockProvider, "chat", forbidden)
    read = state(pid, 2)
    qid = read["workspace"]["draft"]["questions"][0]["id"]
    r = client.put(f"/project/{pid}/chapter/2/proposal/answers", json=command(pid, 2, answers={qid: "手写回答"}))
    assert r.status_code == 200, r.text


def test_damaged_history_can_be_viewed_as_error_without_blocking_current_draft_backup(tmp_path):
    pid = work(tmp_path); generated = generate(pid)
    draft = generated["workspace"]["draft"]
    path = Path(get_project(pid).root_path) / ".novel-gm" / "plans" / get_chapter(pid, 1).chapter_uid / (draft["active_history_id"] + ".json")
    path.write_text("{broken", encoding="utf-8")
    assert client.get(f"/project/{pid}/chapter/1/plans/{draft['active_history_id']}").status_code == 409
    generate(pid)
    assert len(client.get(f"/project/{pid}/chapter/1/plans").json()) >= 2


def test_confirmation_sees_unanswered_choices_and_actor_policy(tmp_path, monkeypatch):
    pid = work(tmp_path); generate(pid)
    seen = []; chat = MockProvider.chat
    def spy(self, req):
        if req["purpose"] == "proposal_check": seen.append(str(req["messages"]))
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    adopt(pid)
    assert "交易的筹码是什么" in seen[0] and "未回答，保持开放" in seen[0]
    assert "policy.actor_choice" in seen[0]


@pytest.mark.parametrize("field, collection", [("present_character_ids", "characters"), ("required_threads", "threads")])
def test_adopting_new_master_association_checks_saved_lore_and_stays_valid(tmp_path, monkeypatch, field, collection):
    import json
    from services.core.models import Character, PlotThread
    from services.core.repository import save_project
    pid = work(tmp_path)
    project = get_project(pid)
    member = (Character(id="new-master", name="迟来的信使", personality="新增人物的谨慎性格")
              if collection == "characters" else PlotThread(id="new-master", name="遗失信件", user_directive="新增脉络的真实走向"))
    setattr(project, collection, [member]); save_project(project)
    assert all(m.id != member.id for m in getattr(get_chapter(pid, 1).lore, collection))
    seen = []; chat = MockProvider.chat
    def spy(self, req):
        if req["purpose"] == "proposal":
            return {"content": json.dumps({"plot_outline": "信使送来遗失的信件", "proposed_plan": {field: [member.id]}, "questions": []})}
        if req["purpose"] in ("proposal_check", "generate"): seen.append((req["purpose"], str(req["messages"])))
        return chat(self, req)
    monkeypatch.setattr(MockProvider, "chat", spy)
    generate(pid)
    adopted = adopt(pid)
    assert adopted["adopted_status"]["state"] == "adopted", adopted["adopted_status"]
    assert member.name in next(prompt for purpose, prompt in seen if purpose == "proposal_check")
    assert any(m.id == member.id for m in getattr(get_chapter(pid, 1).lore, collection))
    assert client.post(f"/project/{pid}/chapter", json={}).status_code == 200
    assert member.name in next(prompt for purpose, prompt in seen if purpose == "generate")
    assert state(pid)["adopted_status"]["state"] == "adopted"
