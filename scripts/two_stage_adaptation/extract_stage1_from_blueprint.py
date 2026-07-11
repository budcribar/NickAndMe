#!/usr/bin/env python3
"""
Extract Stage 1 scene bible from a full clip-plan blueprint (e.g. nickandme.clips.grok.json)
and attach relevant book context from book_text_pages_*.txt (and optional PDF text).

Usage (from repo root):
  python scripts/two_stage_adaptation/extract_stage1_from_blueprint.py
  python scripts/two_stage_adaptation/extract_stage1_from_blueprint.py --blueprint nickandme.clips.grok.json --out nickandme.scenes.json
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

ROOT = Path(__file__).resolve().parents[2]


def _norm(s: str) -> str:
    s = s.lower()
    s = re.sub(r"[“”\"']", "", s)
    s = re.sub(r"\s+", " ", s)
    return s.strip()


def _tokens(s: str) -> set:
    return {t for t in re.findall(r"[a-z0-9']{3,}", _norm(s)) if t not in STOP}


STOP = {
    "the", "and", "that", "was", "with", "for", "his", "her", "she", "you",
    "had", "not", "but", "him", "they", "this", "from", "have", "were", "are",
    "been", "said", "just", "like", "what", "when", "there", "about", "into",
    "them", "then", "than", "some", "could", "would", "out", "all", "one",
}


# How much book text to keep per excerpt (full paragraphs preferred)
MAX_EXCERPT_CHARS = 3500
# Expand match by neighboring paragraphs for context
NEIGHBOR_PARAS = 2


def _split_paragraphs(text: str) -> List[str]:
    parts = re.split(r"\n\s*\n+", text)
    if len(parts) < 3:
        # line-based fallback for single-spaced dumps
        parts = [ln.strip() for ln in text.split("\n") if ln.strip()]
    else:
        parts = [p.strip() for p in parts if p.strip()]
    return parts


def _trim_at_sentence(text: str, max_chars: int) -> str:
    """Prefer ending on sentence boundary; never mid-word if possible."""
    text = re.sub(r"[ \t]+", " ", text.replace("\r\n", "\n")).strip()
    if len(text) <= max_chars:
        return text
    window = text[: max_chars + 1]
    # last sentence end in window
    ends = [m.end() for m in re.finditer(r"[.!?][\"')\]]?\s+", window)]
    if ends and ends[-1] >= max_chars // 3:
        return window[: ends[-1]].strip()
    # last newline / paragraph break
    nl = window.rfind("\n")
    if nl >= max_chars // 3:
        return window[:nl].strip()
    # last space
    sp = window.rfind(" ")
    if sp >= max_chars // 3:
        return window[:sp].rstrip() + "…"
    return window[:max_chars].rstrip() + "…"


def load_book_corpus(root: Path) -> List[Dict[str, Any]]:
    """
    Load book_text_pages_*.txt as paragraph units with full-file context
    so excerpts can expand to complete surrounding paragraphs (not hard-cut mid-sentence).
    """
    files = sorted(root.glob("book_text_pages*.txt"))
    corpus: List[Dict[str, Any]] = []
    for fp in files:
        try:
            text = fp.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        paras = _split_paragraphs(text)
        if not paras:
            continue
        # One indexable unit per paragraph (or short para merge for scoring)
        i = 0
        while i < len(paras):
            # Merge tiny lines into a scorable unit
            j = i
            block = [paras[i]]
            while j + 1 < len(paras) and sum(len(x) for x in block) < 200:
                j += 1
                block.append(paras[j])
            unit_text = "\n\n".join(block)
            corpus.append(
                {
                    "source": fp.name,
                    "para_start": i,
                    "para_end": j,
                    "paras": paras,  # full file paragraphs for expansion
                    "text": unit_text,
                    "norm": _norm(unit_text),
                    "tokens": _tokens(unit_text),
                }
            )
            i = j + 1
    return corpus


def score_chunk(query_tokens: set, query_phrases: List[str], chunk: Dict[str, Any]) -> float:
    if not query_tokens and not query_phrases:
        return 0.0
    ct = chunk["tokens"]
    overlap = len(query_tokens & ct) / max(1, len(query_tokens))
    phrase_hits = 0.0
    norm = chunk["norm"]
    for ph in query_phrases:
        phn = _norm(ph)
        if len(phn) < 12:
            continue
        if phn in norm:
            phrase_hits += 1.0
        else:
            # partial: first 48 chars of phrase
            if phn[:48] in norm:
                phrase_hits += 0.5
    return overlap + 1.5 * phrase_hits


def expand_excerpt(chunk: Dict[str, Any], max_chars: int = MAX_EXCERPT_CHARS) -> str:
    """Expand matched unit to neighboring paragraphs; trim only at sentence ends."""
    paras: List[str] = chunk.get("paras") or [chunk.get("text") or ""]
    start = int(chunk.get("para_start") or 0)
    end = int(chunk.get("para_end") or start)
    start = max(0, start - NEIGHBOR_PARAS)
    end = min(len(paras) - 1, end + NEIGHBOR_PARAS)
    full = "\n\n".join(paras[start : end + 1]).strip()
    return _trim_at_sentence(full, max_chars)


def find_book_context(
    corpus: List[Dict[str, Any]],
    dialogues: List[str],
    visual_bits: List[str],
    top_k: int = 3,
    max_excerpt_chars: int = MAX_EXCERPT_CHARS,
) -> Tuple[List[str], List[Dict[str, str]]]:
    """Return (source_book_refs, source_excerpts) with full-paragraph excerpts."""
    phrases = [d for d in dialogues if d and len(d.strip()) > 15][:12]
    blob = " ".join(dialogues + visual_bits)
    qtok = _tokens(blob)
    scored: List[Tuple[float, Dict[str, Any]]] = []
    for ch in corpus:
        sc = score_chunk(qtok, phrases, ch)
        if sc >= 0.12:
            scored.append((sc, ch))
    scored.sort(key=lambda x: -x[0])

    # Deduplicate overlapping expansions from same file
    refs: List[str] = []
    excerpts: List[Dict[str, str]] = []
    seen_src = set()
    seen_excerpt_norm: set = set()
    for sc, ch in scored:
        if len(excerpts) >= top_k:
            break
        excerpt = expand_excerpt(ch, max_chars=max_excerpt_chars)
        en = _norm(excerpt)[:160]
        if en in seen_excerpt_norm:
            continue
        # skip near-duplicates
        if any(en[:80] in prev or prev[:80] in en for prev in seen_excerpt_norm):
            continue
        seen_excerpt_norm.add(en)
        src = ch["source"]
        if src not in seen_src:
            refs.append(src)
            seen_src.add(src)
        excerpts.append(
            {
                "source": src,
                "score": f"{sc:.2f}",
                "excerpt": excerpt,
                "char_count": str(len(excerpt)),
            }
        )
    return refs, excerpts


def infer_location_type(setting: str, clips: List[Dict]) -> str:
    s = (setting or "").lower()
    blob = s + " " + " ".join((c.get("visual_prompt") or "")[:80] for c in clips[:3]).lower()
    if "flashback" in blob:
        return "flashback"
    if "dream" in blob:
        return "dream"
    if "montage" in blob:
        return "montage"
    if re.search(r"\bext\.|exterior|street|alley|outside|sidewalk", blob):
        return "ext"
    if re.search(r"\bint\.|interior|apartment|kitchen|bedroom|bar\b|library", blob):
        return "int"
    return "mixed"


def infer_story_day(setting: str) -> str:
    m = re.search(r"(Day\s*\d+|Night\s*\d+|Flashback[^\-–|]*)", setting or "", re.I)
    if m:
        return m.group(1).strip()
    return (setting or "")[:80]


def beat_from_clip(clip: Dict[str, Any], idx: int) -> Dict[str, Any]:
    vp = clip.get("visual_prompt") or ""
    ap = clip.get("audio_payload") or {}
    cont = (clip.get("veo_continuation_source") or "none").lower()
    delivery = ap.get("delivery") or "none"
    dialogue = (ap.get("dialogue") or "").strip()

    # action class heuristics
    action_class = "small_motion"
    if re.search(r"\b(kick|smash|punch|sprint|crash|explod|slam|throw|rocket)\b", vp, re.I):
        action_class = "big_action"
    elif re.search(r"\b(wide shot|establishing|aerial)\b", vp, re.I):
        action_class = "establishing"
    elif re.search(r"\bFLASHBACK\b", vp):
        action_class = "flashback_enter" if idx == 0 else "small_motion"
    elif re.search(r"\bBACK TO PRESENT\b", vp, re.I):
        action_class = "flashback_exit"
    elif delivery == "spoken_on_camera" or (dialogue and delivery != "none"):
        action_class = "dialogue"
    elif re.search(r"\b(stands|sits|looks|stares|holds)\b", vp, re.I) and not re.search(
        r"\b(walks|steps|runs)\b", vp, re.I
    ):
        action_class = "hold"

    continuity = (
        "continuous_from_previous_beat"
        if cont in ("extend_previous", "extend")
        else "new_setup"
    )
    if re.search(r"\bBACK TO PRESENT\b", vp, re.I):
        continuity = "return_to_present"

    # primary subject = first Character_*
    m = re.search(r"Character_[A-Za-z0-9_]+", vp)
    primary = m.group(0) if m else None

    shot = "ms"
    if re.search(r"\bCLOSE-UP|CU\b", vp, re.I):
        shot = "cu"
    elif re.search(r"\bWIDE|ESTABLISHING\b", vp, re.I):
        shot = "ws"
    elif re.search(r"\bOTS|over.?shoulder\b", vp, re.I):
        shot = "ots"
    elif re.search(r"\btwo-shot\b", vp, re.I):
        shot = "two_shot"

    # strip tech suffix for visual_event
    visual_event = re.sub(r"\s*/\s*\d+p.*$", "", vp).strip()
    if len(visual_event) > 400:
        visual_event = visual_event[:397] + "..."

    beat: Dict[str, Any] = {
        "beat_id": f"b{idx}",
        "intent": f"Clip {clip.get('clip_number')} beat (from legacy blueprint)",
        "visual_event": visual_event,
        "shot_scale_hint": shot,
        "action_class": action_class,
        "continuity": continuity,
        "time_weight": 1.0,
        "legacy_clip_number": clip.get("clip_number"),
        "legacy_timestamp": clip.get("timestamp"),
    }
    if primary:
        beat["primary_subject"] = primary
    audio = {
        "delivery": delivery if delivery in ("spoken_on_camera", "voiceover_internal", "none") else "none",
        "speaker": ap.get("speaker") or "none",
        "dialogue": dialogue,
    }
    beat["audio"] = audio
    return beat


def extract_characters_on_screen(clips: List[Dict]) -> List[str]:
    found = []
    seen = set()
    for c in clips:
        for m in re.findall(r"Character_[A-Za-z0-9_]+", c.get("visual_prompt") or ""):
            if m not in seen:
                seen.add(m)
                found.append(m)
    return found


def convert_scene(
    scene: Dict[str, Any],
    corpus: List[Dict[str, Any]],
    max_excerpt_chars: int = MAX_EXCERPT_CHARS,
) -> Dict[str, Any]:
    clips = scene.get("veo_clips") or []
    dialogues = []
    visuals = []
    for c in clips:
        ap = c.get("audio_payload") or {}
        if ap.get("dialogue"):
            dialogues.append(str(ap["dialogue"]))
        if c.get("visual_prompt"):
            visuals.append(str(c["visual_prompt"])[:200])

    refs, excerpts = find_book_context(
        corpus, dialogues, visuals, top_k=3, max_excerpt_chars=max_excerpt_chars
    )

    # summary from first dialogue + setting
    summary_parts = []
    if scene.get("setting"):
        summary_parts.append(str(scene["setting"]))
    if dialogues:
        summary_parts.append(dialogues[0][:200])
    summary = " — ".join(summary_parts) if summary_parts else f"Scene {scene.get('scene_number')}"

    mb = scene.get("music_bed") or {}
    music_intent = {
        "style_description": mb.get("style_description") or "cinematic underscore",
        "vocal_style": mb.get("vocal_style") or "",
        "mood_arc": "",
    }
    if mb.get("song_structure"):
        notes = []
        for sec in mb["song_structure"][:4]:
            lab = sec.get("section_label") or sec.get("section_type")
            if lab:
                notes.append(str(lab))
        if notes:
            music_intent["mood_arc"] = " → ".join(notes)

    beats = [beat_from_clip(c, i + 1) for i, c in enumerate(clips)]

    out: Dict[str, Any] = {
        "scene_number": scene.get("scene_number"),
        "scene_filename": scene.get("scene_filename")
        or f"Scene_{int(scene.get('scene_number') or 0):02d}",
        "setting": scene.get("setting") or "",
        "story_day": infer_story_day(scene.get("setting") or ""),
        "location_type": infer_location_type(scene.get("setting") or "", clips),
        "duration_target_seconds": int(
            scene.get("total_estimated_duration_seconds") or 8
        ),
        "dramatic_function": "",
        "summary": summary[:500],
        "source_book_refs": refs,
        "source_excerpts": excerpts,
        "characters_on_screen": extract_characters_on_screen(clips),
        "transition_type": scene.get("transition_type") or "cut",
        "lighting_continuity_token": scene.get("lighting_continuity_token") or "",
        "wardrobe_notes": "",
        "spoiler_constraints": [],
        "story_beats": beats,
        "music_intent": music_intent,
        "legacy_clip_count": len(clips),
    }

    # Spoiler: name policy if P present
    if any(x.startswith("Character_P") for x in out["characters_on_screen"]):
        out["spoiler_constraints"].append(
            "No name tags / personal names on clothing for Character_P (withheld_until_reveal)"
        )
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description="Extract Stage 1 scene bible + book context")
    ap.add_argument(
        "--blueprint",
        default=str(ROOT / "nickandme.clips.grok.json"),
        help="Full production blueprint path (Stage 2 clip plan)",
    )
    ap.add_argument(
        "--out",
        default=str(ROOT / "nickandme.scenes.json"),
        help="Output Stage 1 JSON path",
    )
    ap.add_argument(
        "--book-dir",
        default=str(ROOT),
        help="Directory containing book_text_pages_*.txt",
    )
    ap.add_argument(
        "--max-excerpt-chars",
        type=int,
        default=MAX_EXCERPT_CHARS,
        help=f"Max characters per book excerpt (default {MAX_EXCERPT_CHARS}); trims at sentence end",
    )
    args = ap.parse_args()

    bp_path = Path(args.blueprint)
    if not bp_path.is_file():
        print(f"[Error] Blueprint not found: {bp_path}", file=sys.stderr)
        return 1

    data = json.loads(bp_path.read_text(encoding="utf-8"))
    book_dir = Path(args.book_dir)
    corpus = load_book_corpus(book_dir)
    print(f"[Info] Loaded {len(corpus)} book chunks from {book_dir}")

    gpv = data.get("global_production_variables") or {}
    seeds = gpv.get("character_seed_tokens") or {}

    stage1: Dict[str, Any] = {
        "schema_version": "stage1.v1",
        "movie_title": data.get("movie_title") or "Untitled",
        "source_book_title": data.get("source_book_title") or "",
        "adaptation_notes": (
            "Extracted from full blueprint (legacy nickandme.clips.grok.json). "
            "story_beats derived 1:1 from veo_clips; refine dramatic_function/summary by hand. "
            "source_excerpts matched from book_text_pages_*.txt by dialogue/visual keyword overlap."
        ),
        "extraction": {
            "source_blueprint": str(bp_path.name),
            "book_chunk_count": len(corpus),
            "book_files": sorted({c["source"] for c in corpus}),
        },
        "global_production_variables": {
            "target_aspect_ratio": gpv.get("target_aspect_ratio", "16:9"),
            "resolution": gpv.get("resolution", "720p"),
            "frame_rate": gpv.get("frame_rate", 24),
            "directorial_treatment": gpv.get(
                "directorial_treatment", "cinematic lighting"
            ),
            "total_runtime_target_seconds": gpv.get(
                "total_runtime_target_seconds", 5400
            ),
            "character_seed_tokens": seeds,
        },
        "scenes": [],
    }

    max_ex = int(args.max_excerpt_chars)
    matched = 0
    for scene in data.get("scenes") or []:
        s1 = convert_scene(scene, corpus, max_excerpt_chars=max_ex)
        if s1.get("source_excerpts"):
            matched += 1
        stage1["scenes"].append(s1)

    total_dur = sum(int(s.get("duration_target_seconds") or 0) for s in stage1["scenes"])
    stage1["cumulative_duration_target_seconds"] = total_dur
    stage1["next_scene_number"] = None
    stage1["extraction"]["scenes_with_book_match"] = matched
    stage1["extraction"]["scene_count"] = len(stage1["scenes"])
    stage1["extraction"]["max_excerpt_chars"] = max_ex

    out_path = Path(args.out)
    out_path.write_text(
        json.dumps(stage1, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"[Success] Wrote {out_path}")
    print(
        f"  scenes={len(stage1['scenes'])} duration_sum={total_dur}s "
        f"with_book_match={matched} seeds={len(seeds)}"
    )
    # sample match quality
    for s in stage1["scenes"][:3]:
        print(
            f"  S{s['scene_number']}: refs={s.get('source_book_refs')} "
            f"excerpts={len(s.get('source_excerpts') or [])}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
