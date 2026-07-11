"""
CLI frontend — interactive generate / regen loop.

From workspace root:
  python -m cli
"""
from __future__ import annotations

import sys
from pathlib import Path


def _ensure_workspace_on_path() -> Path:
    """Allow `python -m cli` from anywhere under the repo."""
    here = Path(__file__).resolve().parent  # .../cli
    workspace = here.parent
    if str(workspace) not in sys.path:
        sys.path.insert(0, str(workspace))
    return workspace


def main() -> int:
    _ensure_workspace_on_path()
    from renderer.engine import AgenticGenerationEngine, PipelineInterrupted

    print("=========================================================================")
    print("         FILM RENDERER (CLI)  —  V9.5                                      ")
    print("         Scene/clip select  |  WIP movie after approve  |  Ctrl+C save   ")
    print("=========================================================================")
    engine = None
    try:
        engine = AgenticGenerationEngine()
        engine.run_pipeline()
        return 0
    except SystemExit as e:
        code = e.code
        return int(code) if isinstance(code, int) else (0 if code is None else 1)
    except KeyboardInterrupt:
        if engine is not None:
            engine.graceful_stop("Interrupted by user (KeyboardInterrupt)")
        print("\n[Shutdown] Interrupted before engine init. Nothing to save.")
        return 130
    except PipelineInterrupted as e:
        if engine is not None:
            engine.graceful_stop(str(e))
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
