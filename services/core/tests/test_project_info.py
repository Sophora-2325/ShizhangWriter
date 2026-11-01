from __future__ import annotations

import base64
import shutil
import sys
import zipfile
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

import services.core.db as db
from services.core import workspace as ws
from services.core.main import app
from services.core.models import ProjectCreate
from services.core.repository import create_project, get_project, import_folder


@pytest.fixture
def work(tmp_path, monkeypatch):
    storage = tmp_path / "storage"
    storage.mkdir()
    monkeypatch.setattr(db, "STORAGE", storage)
    monkeypatch.setattr(db, "DB_PATH", storage / "test.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", storage / "projects")
    db.init_db()
    project = create_project(ProjectCreate(title="封面测试", parent_dir=str(tmp_path)))
    root = Path(project.root_path)
    (root / "插图").mkdir()
    image = root / "插图" / "sample.png"
    image.write_bytes(base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jAuoAAAAASUVORK5CYII="))
    return TestClient(app), project, root


def test_legacy_work_has_empty_cover_and_metadata_patch_preserves_settings(work):
    client, project, root = work
    before = get_project(project.id)
    response = client.patch(f"/project/{project.id}/info", json={"genre": [" 奇幻 ", "奇幻", "冒险"], "tone": " 温暖 ", "audience": "青年"})
    assert response.status_code == 200, response.text
    saved = response.json()
    assert saved["genre"] == ["奇幻", "冒险"]
    assert saved["tone"] == "温暖"
    assert saved["cover"] == {"preview": "", "original": ""}
    assert saved["style"] == before.style.model_dump()
    assert saved["threads"] == [t.model_dump() for t in before.threads]
    assert ws.read_project_file(root)["audience"] == "青年"


def test_cover_survives_rename_backup_and_reopen_in_new_location(work, tmp_path):
    client, project, root = work
    cover = {"preview": "插图/sample.png", "original": "插图/sample.png"}
    response = client.patch(f"/project/{project.id}/info", json={"cover": cover})
    assert response.status_code == 200, response.text
    renamed = client.patch(f"/project/{project.id}", json={"title": "改名后的作品"})
    assert renamed.status_code == 200, renamed.text
    new_root = Path(renamed.json()["root_path"])
    assert (new_root / cover["preview"]).is_file()
    backup = ws.backup_zip(new_root, "封面备份")
    with zipfile.ZipFile(backup) as archive:
        assert any(name.endswith("插图/sample.png") for name in archive.namelist())
    moved = tmp_path / "重新打开"
    shutil.copytree(new_root, moved)
    restored = import_folder(str(moved))
    assert restored.cover.model_dump() == cover
    assert restored.root_path == str(moved)
    assert (moved / restored.cover.original).is_file()


def test_cover_removal_does_not_delete_shared_image(work):
    client, project, root = work
    url = f"/project/{project.id}/info"
    cover = {"preview": "插图/sample.png", "original": "插图/sample.png"}
    assert client.patch(url, json={"cover": cover}).status_code == 200
    # An unchanged cover must not be revalidated when other metadata is edited.
    assert client.patch(url, json={"tone": "轻松"}).json()["cover"] == cover
    assert client.patch(url, json={"cover": {}}).json()["cover"] == {"preview": "", "original": ""}
    assert (root / "插图/sample.png").is_file()


@pytest.mark.parametrize("path", ["../outside.png", "C:/outside.png", "/outside.png", "插图/missing.png", "作品.json"])
def test_invalid_cover_rejects_entire_metadata_update(work, path):
    client, project, _ = work
    response = client.patch(f"/project/{project.id}/info", json={"tone": "不可部分保存", "cover": {"preview": path, "original": path}})
    assert response.status_code == 400
    assert get_project(project.id).tone == ""
    assert get_project(project.id).cover.preview == ""


def test_incomplete_cover_is_rejected(work):
    client, project, _ = work
    response = client.patch(f"/project/{project.id}/info", json={"cover": {"preview": "插图/sample.png"}})
    assert response.status_code == 400


def test_missing_old_cover_does_not_block_metadata_changes(work):
    client, project, root = work
    url = f"/project/{project.id}/info"
    cover = {"preview": "插图/sample.png", "original": "插图/sample.png"}
    assert client.patch(url, json={"cover": cover}).status_code == 200
    (root / "插图/sample.png").unlink()
    response = client.patch(url, json={"audience": "成年读者"})
    assert response.status_code == 200
    assert response.json()["cover"] == cover


def test_failed_project_file_write_keeps_previous_metadata(work, monkeypatch):
    client, project, root = work
    previous = (root / "作品.json").read_bytes()
    def fail(*_args):
        raise OSError("simulated disk failure")
    monkeypatch.setattr(ws, "write_project_file", fail)
    with pytest.raises(OSError):
        client.patch(f"/project/{project.id}/info", json={"tone": "不能只写入数据库"})
    assert get_project(project.id).tone == ""
    assert (root / "作品.json").read_bytes() == previous
