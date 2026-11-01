from datetime import datetime, timedelta, timezone

import pytest
from fastapi.testclient import TestClient

from services.core import db, repository
from services.core.main import app
from services.core.usage import session_usage


@pytest.fixture(autouse=True)
def isolated_storage(tmp_path, monkeypatch):
    monkeypatch.setattr(db, "STORAGE", tmp_path)
    monkeypatch.setattr(db, "DB_PATH", tmp_path / "usage.db")
    monkeypatch.setattr(db, "PROJECTS_DIR", tmp_path / "projects")
    db.init_db()


def record(provider="mock", model="mock", cost=0.1):
    repository.add_usage(None, provider, model, 100, 25, 40, cost, 10, "generate")


def test_startup_boundary_excludes_old_calls_and_includes_all_works():
    record()
    since = db.utcnow()
    for _ in range(3):
        record()
    summary = session_usage(since)
    assert summary["calls"] == 3
    assert summary["input_tokens"] == 300
    assert summary["output_tokens"] == 75
    assert summary["cached_tokens"] == 120
    assert summary["cost_usd"] == pytest.approx(0.3)
    assert summary["last"]["purpose"] == "generate"


def test_totals_and_unknown_prices_cover_more_than_the_200_recent_calls():
    since = db.utcnow()
    record("deleted-cloud-provider", "unpriced-model", 0)
    for _ in range(205):
        record()
    value = session_usage(since)
    assert value["calls"] == 206
    assert value["input_tokens"] + value["output_tokens"] == 25750
    assert value["cached_tokens"] == 8240  # A subset of input; not added to total tokens.
    assert value["cost_usd"] == pytest.approx(20.5)
    assert value["unknown_price_calls"] == 1
    assert value["last"]["price_known"] is True


def test_empty_and_local_zero_cost_are_distinct_from_unknown_prices():
    since = db.utcnow()
    assert session_usage(since)["last"] is None
    record("local", "anything", 0)
    value = session_usage(since)
    assert value["unknown_price_calls"] == 0
    assert value["cost_usd"] == 0
    record("removed-cloud", "unpriced", 0)
    assert session_usage(since)["last"]["price_known"] is False


def test_api_validates_boundary_and_normalizes_timezone():
    client = TestClient(app)
    assert client.get("/usage/session").status_code == 422
    assert client.get("/usage/session", params={"since": "not-a-date"}).status_code == 422
    since = db.utcnow() - timedelta(seconds=1)
    record()
    response = client.get("/usage/session", params={"since": since.astimezone(timezone(timedelta(hours=8))).isoformat()})
    assert response.status_code == 200
    assert response.json()["calls"] == 1
    assert response.json()["input_tokens"] == 100
    assert session_usage(datetime.now(timezone.utc) + timedelta(seconds=1))["calls"] == 0
    assert client.get("/cost").json()["total_cost_usd"] == pytest.approx(0.1)
