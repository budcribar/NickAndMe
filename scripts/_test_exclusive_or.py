#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from scripts.two_stage_adaptation.stage2_plan_grok import (  # noqa: E402
    _exclusive_or_environment_lock,
    _split_or_alternatives,
)


def main() -> None:
    alts = _split_or_alternatives(
        "A dream of neon city streets, Or quiet beach waves at dawn"
    )
    print("alts", alts)
    assert len(alts) >= 2

    clause, bans = _exclusive_or_environment_lock(
        {
            "visual_event": "Soft dream: quiet beach waves at dawn, character walks on sand",
            "dialogue": "A dream of neon city streets, Or quiet beach waves at dawn",
            "intent": "dream choice",
            "action_class": "montage",
        },
        {"summary": "dreams"},
    )
    print("clause", clause)
    print("bans", bans)
    assert "EXCLUSIVE" in clause
    # Rejected city side should appear in bans or exclude text
    blob = (clause + " " + " ".join(bans)).lower()
    assert "city" in blob or "neon" in blob or "street" in blob

    c2, b2 = _exclusive_or_environment_lock(
        {
            "visual_event": "Buster chases a brown bunny across a green yard lawn",
            "dialogue": "He'll have a dream of fluffy snows, Or bunnies running 'cross the yard",
            "intent": "dream",
        },
        {},
    )
    print("story-example", c2, b2)
    blob2 = (c2 + " " + " ".join(b2)).lower()
    assert "yard" in blob2 or "bunn" in blob2
    assert "snow" in blob2  # rejected alternative called out

    c3, _b3 = _exclusive_or_environment_lock(
        {
            "visual_event": "snow and bunnies together in one field",
            "dialogue": "snows Or bunnies in the yard",
        },
        {},
    )
    print("mixed ve", c3[:140])
    assert "EXCLUSIVE" in c3
    # Must NOT hardcode a preferred domain name like "green grass yard"
    assert "green grass yard under clear sky" not in c3.lower()
    print("PASS")


if __name__ == "__main__":
    main()
