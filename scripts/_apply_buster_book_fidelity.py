#!/usr/bin/env python3
"""Apply book-fidelity Stage 1 fixes from WIP review (metaphor PJs, exclusive dream)."""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
p = ROOT / "projects" / "Buster" / "scenes.json"
d = json.loads(p.read_text(encoding="utf-8"))
b = d["global_production_variables"]["character_seed_tokens"]["Character_Buster"]
b["description"] = (
    "Small black-and-white dog (never brown/tan redesign), compact playful build, bright curious eyes, "
    "floppy ears, short fur with clear black patches on white. Slightly goofy noodle-head expression—sweet, not mean. "
    "Coat IS the black-and-white look (book text pajamas is coat metaphor unless art shows real sleepwear). "
    "ALWAYS signature silly noodle-head hat. Day and night: natural bare coat; tucks under quilts bare-furred."
)
b["visual_lock"] = (
    "Always small black-and-white dog only—never brown, tan, gray, or solid-color redesign. "
    "Always signature silly noodle-head hat (never bare-headed). Natural bare coat markings only—no physical "
    "pajama garment that blends into fur (text pajamas = coat pattern metaphor per book art). "
    "Never bipedal humanoid redesign."
)
b["wardrobe_always"] = ["signature silly noodle-head hat"]

for s in d.get("scenes") or []:
    sn = int(s.get("scene_number") or 0)
    wbc = dict(s.get("wardrobe_by_character") or {})
    for tok, items in list(wbc.items()):
        if not isinstance(items, list):
            continue
        cleaned = [
            i
            for i in items
            if "pajama" not in str(i).lower() and "pjs" not in str(i).lower()
        ]
        if tok == "Character_Buster" and not any("hat" in str(i).lower() for i in cleaned):
            cleaned = ["signature silly noodle-head hat"] + cleaned
        wbc[tok] = cleaned
    if sn >= 1 and "Character_Buster" in (s.get("characters_on_screen") or []):
        wbc.setdefault("Character_Buster", ["signature silly noodle-head hat"])
        if not wbc["Character_Buster"]:
            wbc["Character_Buster"] = ["signature silly noodle-head hat"]
    s["wardrobe_by_character"] = wbc

    notes = str(s.get("wardrobe_notes") or "")
    if "pajama" in notes.lower():
        s["wardrobe_notes"] = (
            "Buster: natural black-and-white coat only "
            '(book "pajamas" = coat metaphor; no physical sleepwear). '
            "Always signature hat. Mom/Daddy as locked."
        )

    for beat in s.get("story_beats") or []:
        if not isinstance(beat, dict):
            continue
        put = beat.get("wardrobe_put_on")
        if put:
            put2 = [x for x in put if "pajama" not in str(x).lower()]
            if put2:
                beat["wardrobe_put_on"] = put2
            else:
                beat.pop("wardrobe_put_on", None)
        ve = str(beat.get("visual_event") or "")
        if "pajama" in ve.lower():
            ve = re.sub(
                r"(?:is helped into |wears |in )black-and-white dog pajamas[^.]*",
                "shows off his natural black-and-white coat markings "
                "(no separate pajama garment—coat is the black-and-white look) ",
                ve,
                flags=re.I,
            )
            ve = re.sub(r"pajama-clad\s*", "", ve, flags=re.I)
            ve = re.sub(
                r"\bin pajamas\b",
                "with clear black-and-white coat patches",
                ve,
                flags=re.I,
            )
            beat["visual_event"] = re.sub(r"\s+", " ", ve).strip()
        mn = list(beat.get("must_not") or [])
        mn2 = []
        for m in mn:
            ml = str(m).lower()
            if "without pajamas" in ml or "pajamas already" in ml:
                mn2.append(
                    "physical pajama garment that blends into black-and-white fur"
                )
            elif "wrong solid-color pajamas" in ml:
                mn2.append("recolored coat or solid-color redesign")
            else:
                mn2.append(m)
        if str(beat.get("action_class") or "").lower() == "big_action":
            if not any("hat" in str(x).lower() for x in mn2):
                mn2.append("losing signature hat mid-motion / bare-headed")
        if mn2:
            beat["must_not"] = mn2

    if sn == 6:
        for beat in s.get("story_beats") or []:
            if beat.get("beat_id") == "b16":
                beat["intent"] = (
                    "Dream exclusive: bunnies across the green yard "
                    "(book art; not snow mashup)"
                )
                beat["visual_event"] = (
                    "Soft dream insert DREAMSCAPE: daytime vibrant green lawn under clear sky; "
                    "a brown bunny runs across the yard; Character_Buster wearing "
                    "(signature silly noodle-head hat) securely chases playfully across the grass — "
                    "natural black-and-white bare coat only (no pajama garment); "
                    "NO snow, NO winter landscape, NO white snow-bunnies mashup."
                )
                beat["ambient_or_sfx"] = "soft dream whoosh, light yard breeze"
                mn = list(beat.get("must_not") or [])
                for x in (
                    "snowy winter landscape",
                    "fluffy snow ground with bunnies",
                    "physical pajamas blending into fur",
                    "violent hunt",
                    "blood",
                ):
                    if x not in mn:
                        mn.append(x)
                beat["must_not"] = mn
        s["summary"] = (
            "Buster drifts to sleep and dreams of bunnies running across a green yard "
            "(book art exclusive Or-choice, not a snow mashup); falls asleep warm under "
            "the quilt while Mom pets him; loved for keeps; zzz."
        )
        s["wardrobe_notes"] = (
            "Bare black-and-white coat under quilt (no physical pajamas). "
            "Always hat when face is visible."
        )

p.write_text(json.dumps(d, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("saved", p)
