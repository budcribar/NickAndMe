#!/usr/bin/env python3
"""
Run prompts/stage1_scene_bible.txt against the book via xAI Grok (chat/responses).

Produces projects/<active>/nickandme.scenes.json (or --out).

Requires XAI_API_KEY.

Usage (repo root):
  set XAI_API_KEY=...
  python scripts/two_stage_adaptation/run_stage1_from_book.py
  python scripts/two_stage_adaptation/run_stage1_from_book.py --book projects/NickAndMe/source/book_full.txt --chunk-pages 20
  python scripts/two_stage_adaptation/run_stage1_from_book.py --resume   # continue from next_scene_number
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

ROOT = Path(__file__).resolve().parents[2]
XAI_API_BASE = "https://api.x.ai/v1"
PROMPT_PATH = ROOT / "prompts" / "stage1_scene_bible.txt"
SCHEMA_PATH = ROOT / "prompts" / "stage1_scene_bible.schema.json"


def _project_dir(project_id: Optional[str]) -> Path:
    if project_id:
        return ROOT / "projects" / project_id
    ws = ROOT / "projects" / "workspace.json"
    pid = "NickAndMe"
    if ws.is_file():
        try:
            pid = json.loads(ws.read_text(encoding="utf-8")).get("active_project") or pid
        except (json.JSONDecodeError, OSError):
            pass
    return ROOT / "projects" / str(pid)


def _xai_request(payload: Dict[str, Any], timeout: int = 600) -> Dict[str, Any]:
    api_key = os.environ.get("XAI_API_KEY")
    if not api_key:
        raise SystemExit(
            "XAI_API_KEY is not set. Export it then re-run:\n"
            "  set XAI_API_KEY=your_key   (Windows)\n"
            "  export XAI_API_KEY=your_key  (bash)"
        )
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        f"{XAI_API_BASE}/chat/completions",
        data=data,
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        body = e.read().decode(errors="ignore") if hasattr(e, "read") else ""
        raise SystemExit(f"xAI HTTP {e.code}: {body[:800]}")
    except Exception as e:
        raise SystemExit(f"xAI request failed: {e}")
    return json.loads(raw)


def _extract_message_text(result: Dict[str, Any]) -> str:
    choices = result.get("choices") or []
    if choices:
        msg = choices[0].get("message") or {}
        content = msg.get("content")
        if isinstance(content, str):
            return content
        if isinstance(content, list):
            parts = []
            for c in content:
                if isinstance(c, dict) and c.get("type") in ("text", "output_text"):
                    parts.append(c.get("text") or "")
                elif isinstance(c, str):
                    parts.append(c)
            return "\n".join(parts)
    # responses API shape
    if "output_text" in result:
        return str(result["output_text"])
    return json.dumps(result)[:2000]


def _parse_json_object(text: str) -> Dict[str, Any]:
    text = text.strip()
    # strip fences
    if text.startswith("```"):
        text = re.sub(r"^```(?:json)?\s*", "", text)
        text = re.sub(r"\s*```$", "", text)
    # find outermost object
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end <= start:
        raise ValueError("No JSON object in model output")
    blob = text[start : end + 1]
    return json.loads(blob)


def load_book(path: Path) -> str:
    return path.read_text(encoding="utf-8", errors="ignore")


def chunk_book_by_pages(book: str, pages_per_chunk: int) -> List[str]:
    parts = re.split(r"(?=--- PAGE \d+ ---)", book)
    parts = [p for p in parts if p.strip()]
    if not parts:
        # no page markers — chunk by chars
        size = 12000
        return [book[i : i + size] for i in range(0, len(book), size)]
    chunks: List[str] = []
    for i in range(0, len(parts), pages_per_chunk):
        chunks.append("".join(parts[i : i + pages_per_chunk]).strip())
    return chunks


def build_user_message(
    *,
    book_chunk: str,
    chunk_index: int,
    chunk_total: int,
    total_minutes: int,
    prior: Optional[Dict[str, Any]],
    resume_scene: Optional[int],
) -> str:
    lines = [
        f"TOTAL_RUNTIME_MINUTES = {total_minutes}",
        f"BOOK_CHUNK {chunk_index + 1}/{chunk_total}",
        "",
        "Return ONLY valid Stage 1 JSON (schema_version stage1.v1).",
        "Include location_seed_tokens and per-scene location_ids[] as required by the system prompt.",
        "Phase 1: multi-place scenes may list multiple location_ids; do not invent plot to force splits.",
        "Character_P = Nick's younger brother (director label); given name withheld until late book.",
        "Do NOT emit veo_clips, visual_prompt, timestamps, or continuation flags.",
        "",
    ]
    if prior and resume_scene:
        lines += [
            f"RESUME: Continue from scene_number >= {resume_scene}.",
            "Copy character_seed_tokens and location_seed_tokens from PRIOR_PARTIAL (extend if new people/places appear).",
            "Return a FULL Stage 1 document that includes ALL prior scenes plus new ones for this chunk,",
            "OR return only NEW scenes in scenes[] with next_scene_number set — prefer FULL merged document.",
            "",
            "PRIOR_PARTIAL_JSON:",
            json.dumps(
                {
                    "schema_version": prior.get("schema_version"),
                    "movie_title": prior.get("movie_title"),
                    "global_production_variables": prior.get("global_production_variables"),
                    "scene_count": len(prior.get("scenes") or []),
                    "last_scene_number": max(
                        (int(s.get("scene_number") or 0) for s in prior.get("scenes") or []),
                        default=0,
                    ),
                    "scenes_tail": (prior.get("scenes") or [])[-3:],
                },
                ensure_ascii=False,
                indent=2,
            )[:80000],
            "",
        ]
    lines += ["BOOK_TEXT:", book_chunk]
    return "\n".join(lines)


def merge_stage1(base: Optional[Dict[str, Any]], new: Dict[str, Any]) -> Dict[str, Any]:
    if not base:
        return new
    out = copy_dict(base)
    # merge seeds
    gpv = out.setdefault("global_production_variables", {})
    ng = new.get("global_production_variables") or {}
    for key in ("character_seed_tokens", "location_seed_tokens"):
        old_s = gpv.get(key) or {}
        new_s = ng.get(key) or {}
        if isinstance(old_s, dict) and isinstance(new_s, dict):
            merged = dict(old_s)
            merged.update(new_s)
            gpv[key] = merged
    for k, v in ng.items():
        if k not in ("character_seed_tokens", "location_seed_tokens"):
            gpv.setdefault(k, v)
    # merge scenes by number
    by_n: Dict[int, Dict[str, Any]] = {}
    for s in out.get("scenes") or []:
        by_n[int(s.get("scene_number") or 0)] = s
    for s in new.get("scenes") or []:
        by_n[int(s.get("scene_number") or 0)] = s
    out["scenes"] = [by_n[k] for k in sorted(by_n) if k > 0]
    if new.get("movie_title"):
        out["movie_title"] = new["movie_title"]
    if new.get("source_book_title"):
        out["source_book_title"] = new["source_book_title"]
    if new.get("adaptation_notes"):
        out["adaptation_notes"] = new["adaptation_notes"]
    total = sum(int(s.get("duration_target_seconds") or 0) for s in out["scenes"])
    out["cumulative_duration_target_seconds"] = total
    out["next_scene_number"] = new.get("next_scene_number")
    out["schema_version"] = "stage1.v1"
    return out


def copy_dict(d: Dict[str, Any]) -> Dict[str, Any]:
    return json.loads(json.dumps(d))


def validate_stage1(data: Dict[str, Any]) -> List[str]:
    errs: List[str] = []
    if data.get("schema_version") != "stage1.v1":
        errs.append(f"schema_version={data.get('schema_version')!r} expected stage1.v1")
    if "scenes" not in data or not isinstance(data["scenes"], list) or not data["scenes"]:
        errs.append("missing/empty scenes[]")
    gpv = data.get("global_production_variables") or {}
    if not (gpv.get("character_seed_tokens") or {}):
        errs.append("missing character_seed_tokens")
    # location recommended
    loc_seeds = gpv.get("location_seed_tokens") or {}
    scenes = data.get("scenes") or []
    nums = []
    for s in scenes:
        sn = s.get("scene_number")
        nums.append(sn)
        if not s.get("story_beats"):
            errs.append(f"S{sn}: no story_beats")
        if "setting" not in s:
            errs.append(f"S{sn}: no setting")
        # forbid stage2 fields
        if s.get("veo_clips"):
            errs.append(f"S{sn}: has veo_clips (Stage 2 leak)")
        for b in s.get("story_beats") or []:
            if isinstance(b, dict) and b.get("visual_prompt"):
                errs.append(f"S{sn}: beat has visual_prompt (Stage 2 leak)")
        lids = s.get("location_ids") or []
        if lids and loc_seeds:
            for lid in lids:
                if lid not in loc_seeds and lid != "Loc_Unknown":
                    errs.append(f"S{sn}: location_id {lid} not in location_seed_tokens")
    # contiguous scene numbers soft check
    ints = [int(n) for n in nums if isinstance(n, int) or str(n).isdigit()]
    if ints and sorted(ints) != list(range(min(ints), max(ints) + 1)):
        errs.append(f"scene_number gaps or non-contiguous: {sorted(ints)[:20]}…")
    try:
        from jsonschema import Draft202012Validator

        schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
        v = Draft202012Validator(schema)
        for e in list(v.iter_errors(data))[:30]:
            path = "/".join(str(p) for p in e.path) or "(root)"
            errs.append(f"schema {path}: {e.message[:120]}")
    except ImportError:
        errs.append("(jsonschema not installed — skipped formal schema)")
    except Exception as e:
        errs.append(f"schema validation error: {e}")
    return errs


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--project", default=None)
    ap.add_argument("--book", default=None, help="Path to book_full.txt")
    ap.add_argument("--out", default=None)
    ap.add_argument("--model", default=os.environ.get("STAGE1_MODEL", "grok-4.5"))
    ap.add_argument("--chunk-pages", type=int, default=15)
    ap.add_argument("--total-minutes", type=int, default=90)
    ap.add_argument("--resume", action="store_true", help="Load existing --out and continue")
    ap.add_argument("--max-chunks", type=int, default=0, help="0 = all chunks")
    ap.add_argument("--temperature", type=float, default=0.2)
    args = ap.parse_args()

    project = _project_dir(args.project)
    book_path = Path(args.book) if args.book else project / "source" / "book_full.txt"
    if not book_path.is_file():
        # fallback: concatenate page files
        src = project / "source"
        files = sorted(src.glob("book_text_pages*.txt"))
        if not files:
            raise SystemExit(f"No book text at {book_path}")
        book = "\n\n".join(f.read_text(encoding="utf-8", errors="ignore") for f in files)
        print(f"[Info] Using concatenated page files ({len(files)})")
    else:
        book = load_book(book_path)
        print(f"[Info] Book: {book_path} ({len(book)} chars)")

    out_path = Path(args.out) if args.out else project / "nickandme.scenes.json"
    system_prompt = PROMPT_PATH.read_text(encoding="utf-8")
    system_prompt = system_prompt.replace("{{TOTAL_RUNTIME_MINUTES}}", str(args.total_minutes))

    chunks = chunk_book_by_pages(book, max(5, args.chunk_pages))
    if args.max_chunks and args.max_chunks > 0:
        chunks = chunks[: args.max_chunks]
    print(f"[Info] Chunks: {len(chunks)} (pages/chunk≈{args.chunk_pages})")

    partial: Optional[Dict[str, Any]] = None
    if args.resume and out_path.is_file():
        partial = json.loads(out_path.read_text(encoding="utf-8"))
        print(f"[Info] Resume from {out_path} ({len(partial.get('scenes') or [])} scenes)")

    for i, chunk in enumerate(chunks):
        resume_scene = None
        if partial and partial.get("scenes"):
            resume_scene = max(int(s.get("scene_number") or 0) for s in partial["scenes"]) + 1
        user = build_user_message(
            book_chunk=chunk,
            chunk_index=i,
            chunk_total=len(chunks),
            total_minutes=args.total_minutes,
            prior=partial,
            resume_scene=resume_scene,
        )
        print(f"[Grok] Stage1 chunk {i+1}/{len(chunks)} model={args.model} …", flush=True)
        payload = {
            "model": args.model,
            "temperature": args.temperature,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user},
            ],
        }
        t0 = time.time()
        result = _xai_request(payload, timeout=900)
        text = _extract_message_text(result)
        print(
            f"[Grok] chunk {i+1} done in {time.time()-t0:.1f}s, output {len(text)} chars",
            flush=True,
        )
        try:
            parsed = _parse_json_object(text)
        except Exception as e:
            dump = project / f"stage1_raw_chunk_{i+1}.txt"
            dump.write_text(text, encoding="utf-8")
            raise SystemExit(f"Failed to parse JSON for chunk {i+1}: {e}\nRaw: {dump}")
        partial = merge_stage1(partial, parsed)
        # checkpoint
        ck = project / f"nickandme.scenes.partial_chunk{i+1}.json"
        ck.write_text(json.dumps(partial, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"[Info] Checkpoint {ck.name}: {len(partial.get('scenes') or [])} scenes")

    if not partial:
        raise SystemExit("No Stage 1 output produced")

    partial["schema_version"] = "stage1.v1"
    partial.setdefault("movie_title", "Nick and Me")
    partial.setdefault("source_book_title", "Nick and Me")
    partial["generation"] = {
        "method": "run_stage1_from_book.py",
        "model": args.model,
        "book": str(book_path),
        "chunk_pages": args.chunk_pages,
        "chunks": len(chunks),
        "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
    }

    errs = validate_stage1(partial)
    hard = [e for e in errs if not e.startswith("(jsonschema") and "skipped" not in e]
    print(f"[Verify] {len(errs)} issue(s); hard={len(hard)}")
    for e in errs[:40]:
        print(" ", e)

    if out_path.is_file():
        bak = out_path.with_suffix(out_path.suffix + f".bak_stage1_{time.strftime('%Y%m%d_%H%M%S')}")
        shutil.copy2(out_path, bak)
        print(f"[Info] Backup {bak.name}")

    out_path.write_text(json.dumps(partial, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"[Success] Wrote {out_path}")
    print(
        f"  scenes={len(partial.get('scenes') or [])} "
        f"chars={len((partial.get('global_production_variables') or {}).get('character_seed_tokens') or {})} "
        f"locs={len((partial.get('global_production_variables') or {}).get('location_seed_tokens') or {})} "
        f"runtime={partial.get('cumulative_duration_target_seconds')}s"
    )

    if hard:
        print("[Warn] Output written but verification found issues — review before Stage 2.")
        return 2
    print("[Success] Verification clean enough to proceed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
