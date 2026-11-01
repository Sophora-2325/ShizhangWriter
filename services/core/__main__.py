"""CLI entry: python -m services.core  (from novel-gm root)."""
from __future__ import annotations

import os


def main() -> None:
    host = os.environ.get("NOVEL_GM_HOST", "127.0.0.1")
    port = int(os.environ.get("NOVEL_GM_PORT", "18765"))
    import uvicorn

    uvicorn.run(
        "services.core.main:app",
        host=host,
        port=port,
        reload=False,
    )


if __name__ == "__main__":
    main()
