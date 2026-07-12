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
        raise RuntimeError(
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
        raise RuntimeError(f"xAI HTTP {e.code}: {body[:800]}") from e
    except Exception as e:
        raise RuntimeError(f"xAI request failed: {e}") from e
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
    book_images_inventory: str = "",
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
        "HARD TYPE REMINDERS:",
        '- story_day must be a STRING (e.g. "Day 1"), never a number',
        '- location_type ONLY: int | ext | mixed | flashback | dream | montage',
        "- frame_rate integer 24 (not \"24fps\")",
        "- music_intent.style_description required string on every scene",
        "- source_excerpts objects {source, excerpt} only — or omit",
        "- omit optional keys instead of null",
        "- always include full global_production_variables required fields",
        "- for on-screen characters set source_image_pages from AVAILABLE_BOOK_IMAGES when listed",
        "",
    ]
    if book_images_inventory:
        lines += [book_images_inventory, ""]
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


def run_stage1_job(
    *,
    project_id: Optional[str] = None,
    book_path: Optional[Path] = None,
    out_path: Optional[Path] = None,
    model: str = "grok-4.5",
    chunk_pages: int = 10,
    total_minutes: int = 90,
    resume: bool = False,
    max_chunks: int = 0,
    temperature: float = 0.2,
    normalize: bool = True,
    progress_cb: Optional[Any] = None,
) -> Dict[str, Any]:
    """
    Library entry for CLI and Streamlit.
    progress_cb(event: dict) optional — events:
      start, chunk_start, chunk_done, normalize, verify, done, error
    Returns summary dict with paths and counts.
    """
    def progress(event: str, **kwargs: Any) -> None:
        if progress_cb:
            progress_cb({"event": event, **kwargs})
        else:
            msg = kwargs.get("message") or event
            print(f"[{event}] {msg}", flush=True)

    project = _project_dir(project_id)
    book_p = Path(book_path) if book_path else project / "source" / "book_full.txt"
    src = project / "source"

    # Book text policy:
    # - If book_full.txt already looks clean (e.g. after Adaptation re-import + vision),
    #   USE IT — do NOT re-extract PDF OCR (that overwrites good vision text with garbage).
    # - Only run prepare (extract/vision) when text is missing or still garbled.
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import prepare_book_source as prep_mod  # type: ignore
        import extract_book_source as book_src  # type: ignore

        need_prepare = True
        if book_p.is_file():
            analysis0 = book_src.analyze_book_text(
                book_p.read_text(encoding="utf-8", errors="ignore")
            )
            if analysis0.get("text_quality") == "good" and float(
                analysis0.get("garbage_score") or 0
            ) < 0.45:
                need_prepare = False
                progress(
                    "start",
                    message=(
                        "Using existing clean book_full.txt "
                        f"({analysis0.get('text_words')} words) — skip re-extract"
                    ),
                    chunk=0,
                    total=0,
                )

        if need_prepare:
            progress(
                "start",
                message="Book text missing/garbled — prepare (extract + vision if needed)…",
                chunk=0,
                total=0,
            )
            prep = prep_mod.prepare_book_source(
                project_id=project.name,
                force_extract=True,
                force_vision=False,
                render_pages="cover,sparse",
                vision_model=model,
                auto_vision=True,
                progress_cb=progress_cb,
            )
            progress(
                "start",
                message=(
                    f"Prepare: action={prep.get('action')} quality={prep.get('text_quality')} "
                    f"ready={prep.get('ready_for_stage1')} xai={prep.get('has_xai_key')}"
                ),
                chunk=0,
                total=0,
            )
            if prep.get("needs_user") and not prep.get("ready_for_stage1"):
                raise RuntimeError(
                    (prep.get("message") or "Book text is not ready for Stage 1.")
                    + "\n"
                    + (prep.get("user_hint") or "")
                    + "\n\nGarbled PDF OCR will invent wrong character names. "
                    "Set XAI_API_KEY and re-import so vision rebuilds book_full.txt."
                )

        if book_p.is_file():
            analysis = book_src.analyze_book_text(
                book_p.read_text(encoding="utf-8", errors="ignore")
            )
            if analysis.get("text_quality") in ("poor", "empty") or float(
                analysis.get("garbage_score") or 0
            ) >= 0.45:
                raise RuntimeError(
                    "book_full.txt is still garbled OCR. "
                    "Do not rely on Stage 1 to fix this — "
                    "Adaptation → Re-import book (with XAI_API_KEY) first."
                )
    except RuntimeError:
        raise
    except Exception as e:
        progress("start", message=f"Book prepare check failed: {e}", chunk=0, total=0)

    if not book_p.is_file():
        files = sorted(src.glob("book_text_pages*.txt"))
        if not files:
            raise FileNotFoundError(
                f"No book text at {book_p} and no usable PDF under {src}"
            )
        book = "\n\n".join(f.read_text(encoding="utf-8", errors="ignore") for f in files)
        progress("start", message=f"Using concatenated page files ({len(files)})", chunk=0, total=0)
    else:
        book = load_book(book_p)
        progress("start", message=f"Book {book_p} ({len(book)} chars)", chunk=0, total=0)

    out_p = Path(out_path) if out_path else project / "nickandme.scenes.json"
    system_prompt = PROMPT_PATH.read_text(encoding="utf-8")
    system_prompt = system_prompt.replace("{{TOTAL_RUNTIME_MINUTES}}", str(total_minutes))

    chunks = chunk_book_by_pages(book, max(5, chunk_pages))
    if max_chunks and max_chunks > 0:
        chunks = chunks[:max_chunks]
    n_chunks = len(chunks)
    progress("start", message=f"Chunks: {n_chunks} (pages/chunk≈{chunk_pages})", chunk=0, total=n_chunks)

    # Book page inventory for character likeness (from PDF extract)
    book_images_inventory = ""
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import attach_character_images as char_imgs  # type: ignore

        book_images_inventory = char_imgs.inventory_summary_for_prompt(project)
        progress(
            "start",
            message="Book images inventory attached to Stage 1 prompts",
            chunk=0,
            total=n_chunks,
        )
    except Exception as e:
        progress(
            "start",
            message=f"Book images inventory unavailable: {e}",
            chunk=0,
            total=n_chunks,
        )

    partial: Optional[Dict[str, Any]] = None
    if resume and out_p.is_file():
        partial = json.loads(out_p.read_text(encoding="utf-8"))
        progress(
            "start",
            message=f"Resume from {out_p.name} ({len(partial.get('scenes') or [])} scenes)",
            chunk=0,
            total=n_chunks,
        )

    for i, chunk in enumerate(chunks):
        progress(
            "chunk_start",
            message=f"Stage1 chunk {i+1}/{n_chunks} model={model}",
            chunk=i + 1,
            total=n_chunks,
            scenes=len((partial or {}).get("scenes") or []),
        )
        resume_scene = None
        if partial and partial.get("scenes"):
            resume_scene = max(int(s.get("scene_number") or 0) for s in partial["scenes"]) + 1
        user = build_user_message(
            book_chunk=chunk,
            chunk_index=i,
            chunk_total=n_chunks,
            total_minutes=total_minutes,
            prior=partial,
            resume_scene=resume_scene,
            book_images_inventory=book_images_inventory,
        )
        payload = {
            "model": model,
            "temperature": temperature,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user},
            ],
        }
        t0 = time.time()
        result = _xai_request(payload, timeout=900)
        text = _extract_message_text(result)
        elapsed = time.time() - t0
        try:
            parsed = _parse_json_object(text)
        except Exception as e:
            dump = project / f"stage1_raw_chunk_{i+1}.txt"
            dump.write_text(text, encoding="utf-8")
            raise ValueError(f"Failed to parse JSON for chunk {i+1}: {e}\nRaw: {dump}") from e
        partial = merge_stage1(partial, parsed)
        ck = project / f"nickandme.scenes.partial_chunk{i+1}.json"
        ck.write_text(json.dumps(partial, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        progress(
            "chunk_done",
            message=f"chunk {i+1} done in {elapsed:.1f}s, {len(text)} chars, "
            f"{len(partial.get('scenes') or [])} scenes",
            chunk=i + 1,
            total=n_chunks,
            scenes=len(partial.get("scenes") or []),
            elapsed_sec=elapsed,
            output_chars=len(text),
            checkpoint=str(ck),
        )

    if not partial:
        raise RuntimeError("No Stage 1 output produced")

    partial["schema_version"] = "stage1.v1"
    # Prefer project title over legacy Nick-and-Me defaults
    default_title = project.name
    try:
        meta_p = project / "project.json"
        if meta_p.is_file():
            meta = json.loads(meta_p.read_text(encoding="utf-8"))
            default_title = (meta.get("title") or project.name).strip() or project.name
    except (json.JSONDecodeError, OSError):
        pass
    # If the model omitted title or left a wrong default, use the project title
    mt = (partial.get("movie_title") or "").strip()
    if not mt or mt in ("Nick and Me", "Untitled", project.name):
        # keep model title only when it looks intentional and not the old default
        if not mt or mt == "Nick and Me":
            partial["movie_title"] = default_title
    if not (partial.get("source_book_title") or "").strip() or partial.get(
        "source_book_title"
    ) == "Nick and Me":
        partial["source_book_title"] = partial.get("movie_title") or default_title
    partial["generation"] = {
        "method": "run_stage1_from_book.py",
        "model": model,
        "book": str(book_p),
        "chunk_pages": chunk_pages,
        "chunks": n_chunks,
        "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
    }

    if out_p.is_file():
        bak = out_p.with_suffix(out_p.suffix + f".bak_stage1_{time.strftime('%Y%m%d_%H%M%S')}")
        shutil.copy2(out_p, bak)
        progress("normalize", message=f"Backup {bak.name}", chunk=n_chunks, total=n_chunks)
    else:
        bak = None

    out_p.write_text(json.dumps(partial, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    if normalize:
        progress("normalize", message="Running normalize_stage1…", chunk=n_chunks, total=n_chunks)
        # Import sibling module
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import normalize_stage1 as norm  # type: ignore

        data = json.loads(out_p.read_text(encoding="utf-8"))
        fixed = norm.normalize(data)
        out_p.write_text(json.dumps(fixed, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        partial = fixed

    errs = validate_stage1(partial)
    hard = [e for e in errs if not e.startswith("(jsonschema") and "skipped" not in e]
    progress(
        "verify",
        message=f"{len(errs)} issue(s); hard={len(hard)}",
        chunk=n_chunks,
        total=n_chunks,
        errors=errs[:40],
    )

    # Pull character plates out of PDF extract → assets/characters + seed fields
    char_img_result: Dict[str, Any] = {}
    try:
        progress(
            "character_images",
            message="Attaching book/PDF images to character seeds…",
            chunk=n_chunks,
            total=n_chunks,
        )
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import attach_character_images as char_imgs  # type: ignore

        char_img_result = char_imgs.attach_character_images(
            project_id=project.name,
            force=True,
            copy_into_assets=True,
            update_blueprint=True,
        )
        # reload partial after attach rewrote scenes.json
        if out_p.is_file():
            partial = json.loads(out_p.read_text(encoding="utf-8"))
        progress(
            "character_images",
            message=(
                f"Character images: attached={char_img_result.get('count')} "
                f"ok={char_img_result.get('ok')}"
            ),
            chunk=n_chunks,
            total=n_chunks,
        )
    except Exception as e:
        char_img_result = {"ok": False, "error": str(e)}
        progress(
            "character_images",
            message=f"Character image attach skipped: {e}",
            chunk=n_chunks,
            total=n_chunks,
        )

    summary = {
        "out_path": str(out_p),
        "backup": str(bak) if bak else None,
        "scenes": len(partial.get("scenes") or []),
        "characters": len(
            (partial.get("global_production_variables") or {}).get("character_seed_tokens") or {}
        ),
        "locations": len(
            (partial.get("global_production_variables") or {}).get("location_seed_tokens") or {}
        ),
        "runtime_sec": partial.get("cumulative_duration_target_seconds"),
        "chunks": n_chunks,
        "verify_errors": errs,
        "hard_errors": hard,
        "character_images": char_img_result,
        "ok": len(hard) == 0,
    }
    progress("done", message=f"Wrote {out_p}", chunk=n_chunks, total=n_chunks, **summary)
    return summary


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--project", default=None)
    ap.add_argument("--book", default=None, help="Path to book_full.txt (auto-built from PDF if missing)")
    ap.add_argument("--out", default=None)
    ap.add_argument("--model", default=os.environ.get("STAGE1_MODEL", "grok-4.5"))
    ap.add_argument("--chunk-pages", type=int, default=10)
    ap.add_argument("--total-minutes", type=int, default=90)
    ap.add_argument("--resume", action="store_true", help="Load existing --out and continue")
    ap.add_argument("--max-chunks", type=int, default=0, help="0 = all chunks")
    ap.add_argument("--temperature", type=float, default=0.2)
    ap.add_argument("--no-normalize", action="store_true")
    args = ap.parse_args()

    try:
        summary = run_stage1_job(
            project_id=args.project,
            book_path=Path(args.book) if args.book else None,
            out_path=Path(args.out) if args.out else None,
            model=args.model,
            chunk_pages=args.chunk_pages,
            total_minutes=args.total_minutes,
            resume=args.resume,
            max_chunks=args.max_chunks,
            temperature=args.temperature,
            normalize=not args.no_normalize,
        )
    except Exception as e:
        print(f"[Error] {e}", flush=True)
        return 1

    print(
        f"[Success] scenes={summary['scenes']} chars={summary['characters']} "
        f"locs={summary['locations']} runtime={summary['runtime_sec']}s",
        flush=True,
    )
    if summary.get("hard_errors"):
        print("[Warn] Output written but verification found issues — review before Stage 2.")
        return 2
    print("[Success] Verification clean enough to proceed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
