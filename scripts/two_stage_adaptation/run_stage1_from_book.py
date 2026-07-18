#!/usr/bin/env python3
"""
DEPRECATED — book → screenplay is Fountain-first in FilmStudio (C# host).

Use the Blazor Adaptation flow:
  Import book → Draft from book (prompts/book_to_fountain.txt) → edit → Approve.

There is no longer a book → stage1 JSON LLM path via prompts/stage1_scene_bible.txt
(that file has been removed).

If you only need a .fountain file offline, use the FilmStudio API:
  POST /api/projects/{id}/screenplay/from-book
"""
from __future__ import annotations

import sys


def main() -> int:
    print(
        "run_stage1_from_book.py is retired.\n"
        "Screenplay is book → Fountain (prompts/book_to_fountain.txt) in FilmStudio.\n"
        "Open Adaptation → Screenplay → Draft from book, then Approve.\n"
        "API: POST /api/projects/{id}/screenplay/from-book",
        file=sys.stderr,
    )
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
