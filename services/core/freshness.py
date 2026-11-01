"""Body revisions and the derived results that belong to them."""
from hashlib import sha256

from .models import Chapter

RESULTS = ("summary", "state_delta", "consistency_report", "plan_check", "style_report")


def body_hash(text: str) -> str:
    return sha256(text.replace("\r\n", "\n").encode("utf-8")).hexdigest()


def invalidate(chapter: Chapter) -> None:
    for key in RESULTS:
        value = getattr(chapter, key)
        if value is not None:
            chapter.stale_results[key] = value.model_dump(mode="json")
        setattr(chapter, key, None)
    chapter.derived_hashes = {}
    chapter.summary_status = "stale" if chapter.body_text else "pending"
    chapter.summary_error = ""


def stamp(chapter: Chapter, *keys: str) -> None:
    revision = body_hash(chapter.body_text)
    chapter.body_hash = revision
    for key in keys:
        chapter.derived_hashes[key] = revision
        chapter.stale_results.pop(key, None)
    if "summary" in keys:
        chapter.summary_status = "ready"
        chapter.summary_error = ""


def summary_valid(chapter: Chapter) -> bool:
    return (chapter.summary is not None and not chapter.upstream_changed
            and (not chapter.body_hash or chapter.summary_status not in ("stale", "pending"))
            and chapter.derived_hashes.get("summary", body_hash(chapter.body_text)) == body_hash(chapter.body_text))
