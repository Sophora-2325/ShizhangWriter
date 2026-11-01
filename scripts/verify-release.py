"""Exercise the exact bundled backend/runtime using disposable data, never author storage."""
import argparse
import json
import os
from pathlib import Path
import sys
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument("bundle", type=Path)
parser.add_argument("version")
parser.add_argument("smoke_root", type=Path)
parser.add_argument("--startup-only", action="store_true", help="Only check packaged startup and version for non-behavioral changes")
args = parser.parse_args()
bundle = args.bundle.resolve()
args.smoke_root.mkdir(parents=True, exist_ok=True)

with tempfile.TemporaryDirectory(prefix="bundle-", dir=args.smoke_root) as temporary:
    os.environ["NOVEL_GM_ROOT"] = str(bundle)
    os.environ["NOVEL_GM_DATA"] = str(Path(temporary) / "data")
    os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(bundle))
    import anthropic
    import google.genai
    import openai
    import lxml.html
    import httpx
    import keyring
    from fastapi.testclient import TestClient
    from services.core import main
    assert Path(main.__file__).resolve().is_relative_to(bundle), main.__file__
    with TestClient(main.app) as client:
        health = client.get("/health")
        assert health.status_code == 200, health.text
        assert Path(health.json()["paths"]["root"]).resolve() == bundle
        schema = client.get("/openapi.json").json()
        assert schema["info"]["version"] == args.version
        if args.startup_only:
            print(f"BUNDLE OK: {args.version}; SDK imports, isolated startup and version only; no feature/full tests")
            sys.exit(0)
        # Session usage endpoint: one focused packaged check, independent of existing smoke flows below.
        from services.core.db import utcnow
        from services.core.repository import add_usage
        since = utcnow().isoformat()
        add_usage(None, "mock", "mock", 100, 25, 40, 0, 1, "generate")
        usage = client.get("/usage/session", params={"since": since})
        assert usage.status_code == 200, usage.text
        assert usage.json()["input_tokens"] == 100 and usage.json()["output_tokens"] == 25
        assert usage.json()["cached_tokens"] == 40 and usage.json()["unknown_price_calls"] == 0
        assert usage.json()["last"]["price_known"] is True
        assert client.get("/usage/session", params={"since": "bad-date"}).status_code == 422
        assert "/project/{project_id}/chapter/{n}/stacked-constraints" in schema["paths"]
        for route in ("/providers", "/providers/presets", "/skills"):
            assert client.get(route).status_code == 200, route
        response = client.post("/project", json={"title": "Release verification", "parent_dir": temporary,
            "entries": [], "characters": [], "threads": [], "style": {}})
        assert response.status_code == 200, response.text
        pid = response.json()["id"]
        response = client.put(f"/project/{pid}/lore", json={
            "entries": [{"category": "item", "title": "Old key", "content": "Opens the north tower"}],
            "characters": [], "threads": response.json()["threads"]})
        assert response.status_code == 200, response.text
        assert response.json()["entries"][0]["category"] == "item"
        response = client.post(f"/project/{pid}/chapters", json={"plan": {"misc": "Author notes"}})
        assert response.status_code == 200, response.text
        assert response.json()["lore"]["entries"][0]["category"] == "item"
        endpoint = f"/project/{pid}/chapter/1/stacked-constraints"
        payload = {"tracks": [{"id": "release-track", "domain": "scene", "element_id": "suspense", "weight": 0.1,
            "segments": [{"id": "opening", "start": 0, "end": 25, "directive": "Keep the sender secret"},
                         {"id": "ending", "start": 75, "end": 100, "directive": "Retain the clue"}]}]}
        response = client.put(endpoint, json=payload)
        assert response.status_code == 200, response.text
        chapter = response.json()
        assert len(chapter["stacked_constraints"]["tracks"][0]["segments"]) == 2
        assert chapter["plan"]["scene_types"] == ["suspense"]
        assert chapter["plan"]["misc"] == "Author notes"
        payload["tracks"][0]["weight"] = 0
        assert client.put(endpoint, json=payload).status_code == 422
        payload["tracks"][0]["weight"] = 1
        assert client.put(endpoint, json=payload).status_code == 200
        assert client.patch(f"/project/{pid}/settings", json={"provider": "mock"}).status_code == 200
        from services.core.repository import get_chapter, get_project
        from services.core.rhythm_alignment import check_alignment
        from services.core.stacked_constraints import compile_direction
        chapter_model = get_chapter(pid, 1)
        fused = compile_direction(chapter_model.plan, chapter_model.stacked_constraints, get_project(pid))
        assert fused.index("一、节奏骨架") < fused.index("二、具体内容") < fused.index("三、写作要求")
        assert check_alignment(get_project(pid), chapter_model) == []
        from services.core import db, rhythm_alignment
        from types import SimpleNamespace
        from unittest.mock import patch
        # Exercise old JSON with the bundled validator, not only the dev runtime.
        payload["tracks"][0]["weight"] = 0.05
        with db.connect() as conn:
            conn.execute("UPDATE chapters SET stacked_constraints_json=? WHERE project_id=? AND no=1", (json.dumps(payload), pid))
        migrated = client.get(f"/project/{pid}/chapter/1").json()
        assert migrated["stacked_constraints"]["tracks"][0]["weight"] == 0.1
        assert migrated["stacked_weight_migration_pending"]
        assert not client.post(endpoint + "/migration/ack").json()["stacked_weight_migration_pending"]
        mismatch = {"status": "mismatch", "issues": [{"kind": "direction", "plan_field": "misc",
            "plan_quote": "Author notes", "track_id": "release-track", "segment_id": "opening", "reason": "Simulated conflict"}]}
        provider = SimpleNamespace(name="mock", chat=lambda request: {"content": json.dumps(mismatch)})
        rhythm_alignment._cache.clear()
        with patch.object(rhythm_alignment, "resolve_for_project", return_value=(provider, "mock")):
            blocked = client.post(f"/project/{pid}/chapter/1/replan")
            assert blocked.status_code == 409, blocked.text
            assert blocked.json()["detail"]["code"] == "PLAN_RHYTHM_MISMATCH"
            assert blocked.json()["detail"]["issues"][0]["segment_id"] == "opening"
        assert client.get(f"/project/{pid}/chapter/1").json()["plan"]["misc"] == "Author notes"
        assert client.put(f"/project/{pid}/chapter/1/plan", json={}).status_code == 409
        assert client.put(endpoint, json={"tracks": []}).status_code == 200
        from uuid import uuid4
        proposal_endpoint = f"/project/{pid}/chapter/1/proposal"
        def proposal_command():
            current = client.get(proposal_endpoint).json()
            return {"chapter_uid": current["chapter"]["chapter_uid"], "expected_revision": current["workspace"]["revision"],
                    "input_token": current["input_token"], "request_id": uuid4().hex}
        original_plan = client.get(f"/project/{pid}/chapter/1").json()["plan"]
        command = proposal_command()
        response = client.post(proposal_endpoint, json=command)
        assert response.status_code == 200, response.text
        draft = response.json()["workspace"]["draft"]
        assert response.json()["chapter"]["plan"] == original_plan
        assert len(draft["questions"]) <= 3 and all(not q["answer"] for q in draft["questions"])
        assert client.post(proposal_endpoint, json=command).status_code == 200
        response = client.post(proposal_endpoint + "/confirm", json=proposal_command())
        assert response.status_code == 200, response.text
        assert response.json()["adopted_status"]["state"] == "adopted"
        assert response.json()["workspace"]["draft"] is None
        adopted_id = response.json()["workspace"]["adopted"]["id"]
        response = client.post(proposal_endpoint, json=proposal_command())
        assert response.status_code == 200, response.text
        assert response.json()["workspace"]["adopted"]["id"] == adopted_id
        assert response.json()["adopted_status"]["state"] == "adopted"
        records = client.get(f"/project/{pid}/chapter/1/plans").json()
        assert len(records) >= 3 and sum(record["protected"] for record in records) == 2
        from services.core.repository import import_folder
        root_path = get_project(pid).root_path
        uid = get_chapter(pid, 1).chapter_uid
        import_folder(root_path)
        assert get_chapter(pid, 1).chapter_uid == uid
        assert get_chapter(pid, 1).proposal.adopted.id == adopted_id
        assert client.get(proposal_endpoint).json()["adopted_status"]["state"] == "adopted"
        # Verify adoption's future lore with the actual bundled runtime too.
        from services.core.models import Character, PlotThread
        from services.core.repository import save_project, upsert_chapter
        project = get_project(pid)
        project.characters.append(Character(id="bundle-new-character", name="New messenger"))
        project.threads.append(PlotThread(id="bundle-new-thread", name="Missing letter"))
        save_project(project)
        chapter = get_chapter(pid, 1)
        assert all(c.id != "bundle-new-character" for c in chapter.lore.characters)
        chapter.proposal.draft.proposed_plan.present_character_ids.append("bundle-new-character")
        chapter.proposal.draft.proposed_plan.required_threads.append("bundle-new-thread")
        upsert_chapter(pid, chapter, proposal_write=True)
        response = client.post(proposal_endpoint + "/confirm", json=proposal_command())
        assert response.status_code == 200, response.text
        assert response.json()["adopted_status"]["state"] == "adopted", response.text
        adopted = response.json()["workspace"]["adopted"]
        assert "New messenger" in adopted["source_manifest"]["layers"]["L1"]
        assert "Missing letter" in adopted["source_manifest"]["layers"]["L1"]
        assert client.get(proposal_endpoint).json()["adopted_status"]["state"] == "adopted"
    print(f"BUNDLE OK: {args.version}; SDKs, startup, item, layered rhythm, preflight, stable UID, draft/adopt isolation, proof, history, portable reopen, idempotence, new-association adoption")
