#!/usr/bin/env python3
"""
Automatically prepare project source for Stage 1.

Decides the best path so the user does not have to:
  1) Extract PDF text + images (if PDF present)
  2) Score text quality
  3) If good → keep embedded text
  4) If sparse/poor → Grok vision on page images (when XAI_API_KEY set)
     else try Tesseract OCR via extract, else leave text + clear needs
  5) Write extract_meta.json with suggested Stage 1 runtime/chunks
  6) Mark ready_for_stage1

Usage (repo root):
  python scripts/two_stage_adaptation/prepare_book_source.py
  python scripts/two_stage_adaptation/prepare_book_source.py --project BusterTheNoodleheadDog --force
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import extract_book_source as extract_mod  # noqa: E402
import transcribe_book_pages_grok as vision_mod  # noqa: E402


def _project_dir(project_id: Optional[str]) -> Path:
    return extract_mod._project_dir(project_id)


def _has_xai_key() -> bool:
    return bool((os.environ.get("XAI_API_KEY") or "").strip())


def _write_meta(source: Path, meta: Dict[str, Any]) -> Path:
    path = source / "extract_meta.json"
    path.write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return path


def decide_text_strategy(
    analysis: Dict[str, Any],
    *,
    has_images: bool,
    has_xai: bool,
) -> Dict[str, Any]:
    """
    Pure decision: what to do with book text before Stage 1.

    Picture books with page images: prefer Grok vision whenever the API key is
    set — embedded PDF text is often OCR soup that invents wrong character names
    (e.g. "Duster" instead of "Buster").
    """
    quality = str(analysis.get("text_quality") or "unknown")
    density = str(analysis.get("text_density") or "normal")
    kind = str(analysis.get("book_kind") or "unknown")
    words = int(analysis.get("text_words") or 0)
    garbage = float(analysis.get("garbage_score") or 0)

    # Picture book + plates → vision (when key present) unless text is clearly clean
    picture = kind == "picture_book" or density == "sparse"
    text_clearly_clean = (
        quality == "good" and garbage < 0.2 and words >= 80 and density != "sparse"
    )
    if picture and has_images and has_xai and not text_clearly_clean:
        return {
            "action": "grok_vision_transcribe",
            "reason": (
                f"Picture book / sparse text (quality={quality}, garbage={garbage:.2f}). "
                "Rebuilding book_full.txt with Grok vision from page images so character "
                "names and dialogue match the art."
            ),
            "ready_for_stage1": False,
            "needs_user": False,
        }

    if picture and has_images and not has_xai and not text_clearly_clean:
        return {
            "action": "need_xai_for_vision",
            "reason": (
                "Picture book page images are ready, but embedded PDF text is unreliable. "
                "Set XAI_API_KEY and re-import so vision can rebuild book_full.txt "
                "(required for correct character names)."
            ),
            "ready_for_stage1": False,
            "needs_user": True,
            "user_hint": (
                "export XAI_API_KEY=… then Re-import book "
                "(or paste a clean transcript into source/book_full.txt)."
            ),
        }

    if quality == "good" and garbage < 0.25:
        note = "Text looks clean enough for Stage 1."
        if density == "sparse" or kind == "picture_book":
            note = (
                f"Picture-book layout with clean wording — ready for Stage 1 "
                f"(~{analysis.get('suggested_total_minutes')} min)."
            )
        return {
            "action": "use_embedded_text",
            "reason": note,
            "ready_for_stage1": True,
            "needs_user": False,
        }

    needs_better = quality in ("poor", "empty", "sparse") or garbage >= 0.25
    if not needs_better:
        return {
            "action": "use_embedded_text",
            "reason": f"Text quality '{quality}' is acceptable for Stage 1.",
            "ready_for_stage1": True,
            "needs_user": False,
        }

    if has_images and has_xai:
        return {
            "action": "grok_vision_transcribe",
            "reason": (
                f"Text quality is '{quality}' ({kind}). "
                "Page images exist and XAI_API_KEY is set — "
                "will rebuild book_full.txt with Grok vision."
            ),
            "ready_for_stage1": False,
            "needs_user": False,
        }

    if has_images and not has_xai:
        return {
            "action": "need_xai_for_vision",
            "reason": (
                f"Text quality is '{quality}'. Page images are ready, but "
                "XAI_API_KEY is not set. Set the key and re-run prepare "
                "(or paste a clean transcript into book_full.txt)."
            ),
            "ready_for_stage1": False,
            "needs_user": True,
            "user_hint": (
                "export XAI_API_KEY=… then re-import "
                "(or paste clean text into source/book_full.txt)."
            ),
        }

    return {
        "action": "manual_or_ocr",
        "reason": (
            f"Text quality is '{quality}' and no page images found. "
            "Re-extract the PDF with images, install Tesseract for OCR, "
            "or paste a clean transcript."
        ),
        "ready_for_stage1": False,
        "needs_user": True,
        "user_hint": "Upload PDF → Import book (extract images) or paste book_full.txt.",
    }


def prepare_book_source(
    *,
    project_id: Optional[str] = None,
    force_extract: bool = True,
    force_vision: bool = False,
    render_pages: str = "cover,sparse",
    vision_model: str = "grok-4.5",
    auto_vision: bool = True,
    progress_cb: Optional[Callable[[Dict[str, Any]], None]] = None,
) -> Dict[str, Any]:
    """
    Full auto pipeline. Returns summary for UI/CLI.
    """
    def progress(event: str, **kwargs: Any) -> None:
        if progress_cb:
            progress_cb({"event": event, **kwargs})
        else:
            print(f"[{event}] {kwargs.get('message') or event}", flush=True)

    project = _project_dir(project_id)
    source = project / "source"
    source.mkdir(parents=True, exist_ok=True)
    steps: List[Dict[str, Any]] = []
    summary: Dict[str, Any] = {
        "ok": False,
        "project": project.name,
        "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "steps": steps,
    }

    pdf = extract_mod.find_pdf(source)
    book_txt = source / "book_full.txt"
    has_xai = _has_xai_key()
    summary["has_xai_key"] = has_xai
    summary["pdf"] = str(pdf) if pdf else None

    # --- Step 1: extract if PDF ---
    extract_summary: Dict[str, Any] = {}
    if pdf is not None:
        progress("extract_start", message=f"Extracting {pdf.name}…", chunk=0, total=3)
        modes = [m.strip() for m in render_pages.split(",") if m.strip() and m.strip() != "none"]
        extract_summary = extract_mod.extract_book_source(
            project_id=project.name,
            pdf_path=pdf,
            write_text=True,
            extract_images=True,
            render_modes=modes,
            force=force_extract,
        )
        steps.append(
            {
                "step": "extract",
                "ok": bool(extract_summary.get("ok")),
                "pages": extract_summary.get("pages"),
                "text_quality": extract_summary.get("text_quality"),
                "images": extract_summary.get("images"),
                "engine": extract_summary.get("text_engine"),
            }
        )
        progress(
            "extract_done",
            message=(
                f"Extract OK: pages={extract_summary.get('pages')} "
                f"quality={extract_summary.get('text_quality')} "
                f"images={extract_summary.get('images')}"
            ),
            chunk=1,
            total=3,
        )
    elif book_txt.is_file():
        raw = book_txt.read_text(encoding="utf-8", errors="ignore")
        analysis = extract_mod.analyze_book_text(raw)
        extract_summary = {
            "ok": True,
            "pages": analysis.get("pages"),
            "text_chars": analysis.get("text_chars"),
            "text_words": analysis.get("text_words"),
            "text_quality": analysis.get("text_quality"),
            "book_kind": analysis.get("book_kind"),
            "suggested_total_minutes": analysis.get("suggested_total_minutes"),
            "suggested_chunk_pages": analysis.get("suggested_chunk_pages"),
            "analysis": analysis,
            "text_engine": "existing_book_full",
        }
        steps.append({"step": "use_existing_text", "ok": True, **analysis})
        progress("extract_done", message="Using existing book_full.txt", chunk=1, total=3)
    else:
        summary["error"] = f"No PDF and no book_full.txt under {source}"
        progress("error", message=summary["error"])
        return summary

    analysis = extract_summary.get("analysis") or {}
    if not analysis and book_txt.is_file():
        analysis = extract_mod.analyze_book_text(
            book_txt.read_text(encoding="utf-8", errors="ignore"),
            pages_hint=extract_summary.get("pages"),
        )

    page_images = vision_mod.collect_page_images(source)
    has_images = len(page_images) > 0
    strategy = decide_text_strategy(analysis, has_images=has_images, has_xai=has_xai)
    if force_vision and has_images and has_xai:
        strategy = {
            "action": "grok_vision_transcribe",
            "reason": "Forced Grok vision transcription.",
            "ready_for_stage1": False,
            "needs_user": False,
        }
    if not auto_vision and strategy["action"] == "grok_vision_transcribe":
        strategy = {
            "action": "vision_skipped",
            "reason": "Auto vision disabled; keeping extract text (may be garbled).",
            "ready_for_stage1": analysis.get("text_quality") == "good",
            "needs_user": analysis.get("text_quality") != "good",
            "user_hint": "Enable auto vision or paste clean book_full.txt.",
        }

    summary["strategy"] = strategy
    steps.append({"step": "decide", **strategy, "has_images": has_images, "image_count": len(page_images)})
    progress("decide", message=strategy["reason"], chunk=2, total=3)

    # --- Step 2: vision if needed ---
    vision_summary: Optional[Dict[str, Any]] = None
    if strategy["action"] == "grok_vision_transcribe":
        progress(
            "vision_start",
            message=f"Grok vision on {len(page_images)} page(s)…",
            chunk=2,
            total=3,
        )
        vision_summary = vision_mod.transcribe_book_pages(
            project_id=project.name,
            model=vision_model,
            force=True,
            progress_cb=progress_cb,
        )
        steps.append(
            {
                "step": "grok_vision",
                "ok": bool(vision_summary.get("ok")),
                "pages": vision_summary.get("pages"),
                "text_chars": vision_summary.get("text_chars"),
                "failed_pages": vision_summary.get("failed_pages"),
                "model": vision_summary.get("model"),
            }
        )
        # re-analyze clean text
        raw = book_txt.read_text(encoding="utf-8", errors="ignore")
        analysis = extract_mod.analyze_book_text(
            raw, pages_hint=vision_summary.get("pages")
        )
        # Vision text is intentional transcription — treat as good enough for stage1
        # even if short (picture book), unless total failure
        failed = int(vision_summary.get("failed_pages") or 0)
        total_p = int(vision_summary.get("pages") or 1)
        if failed >= total_p:
            strategy = {
                "action": "vision_failed",
                "reason": "Grok vision failed on all pages.",
                "ready_for_stage1": False,
                "needs_user": True,
                "user_hint": "Check XAI_API_KEY / model, or paste book text manually.",
            }
        else:
            analysis["text_quality"] = (
                "good" if failed == 0 else analysis.get("text_quality") or "sparse"
            )
            analysis["text_source"] = "grok_vision"
            strategy = {
                "action": "grok_vision_done",
                "reason": (
                    f"Rebuilt book_full.txt via Grok vision "
                    f"({vision_summary.get('text_words')} words, "
                    f"{failed} page error(s))."
                ),
                "ready_for_stage1": True,
                "needs_user": False,
            }
        summary["strategy"] = strategy
        progress("vision_done", message=strategy["reason"], chunk=3, total=3)

    # --- Step 3: meta + defaults ---
    ready = bool(strategy.get("ready_for_stage1"))
    meta = {
        "schema_version": "extract_meta.v1",
        "prepared_at": summary["ts"],
        "pdf": pdf.name if pdf else None,
        "text_engine": (
            (vision_summary or {}).get("method")
            or extract_summary.get("text_engine")
            or analysis.get("text_engine")
        ),
        "pages": analysis.get("pages") or extract_summary.get("pages"),
        "text_chars": analysis.get("text_chars") or extract_summary.get("text_chars"),
        "text_words": analysis.get("text_words") or extract_summary.get("text_words"),
        "text_quality": analysis.get("text_quality"),
        "book_kind": analysis.get("book_kind"),
        "suggested_total_minutes": analysis.get("suggested_total_minutes"),
        "suggested_chunk_pages": analysis.get("suggested_chunk_pages"),
        "strategy": strategy,
        "ready_for_stage1": ready,
        "has_page_images": has_images,
        "page_image_count": len(page_images),
        "auto_prepared": True,
        "notes": list(analysis.get("notes") or []) + [strategy.get("reason") or ""],
        "analysis": analysis,
        "vision": {
            "ran": vision_summary is not None,
            "model": (vision_summary or {}).get("model"),
            "failed_pages": (vision_summary or {}).get("failed_pages"),
        },
    }
    meta_path = _write_meta(source, meta)

    summary.update(
        {
            "ok": True,
            "ready_for_stage1": ready,
            "text_quality": meta["text_quality"],
            "book_kind": meta["book_kind"],
            "suggested_total_minutes": meta["suggested_total_minutes"],
            "suggested_chunk_pages": meta["suggested_chunk_pages"],
            "book_full": str(book_txt),
            "extract_meta": str(meta_path),
            "needs_user": bool(strategy.get("needs_user")),
            "user_hint": strategy.get("user_hint"),
            "action": strategy.get("action"),
            "message": strategy.get("reason"),
            "extract": extract_summary,
            "vision": vision_summary,
            "analysis": analysis,
        }
    )
    progress(
        "done",
        message=(
            f"Prepare complete: action={summary['action']} "
            f"ready={ready} runtime≈{summary.get('suggested_total_minutes')}min"
        ),
        chunk=3,
        total=3,
    )
    return summary


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--project", default=None)
    ap.add_argument("--force", action="store_true", help="Force re-extract PDF")
    ap.add_argument("--force-vision", action="store_true", help="Always run Grok vision")
    ap.add_argument("--no-auto-vision", action="store_true")
    ap.add_argument("--render-pages", default="cover,sparse")
    ap.add_argument("--model", default=os.environ.get("STAGE1_MODEL", "grok-4.5"))
    args = ap.parse_args()

    summary = prepare_book_source(
        project_id=args.project,
        force_extract=args.force,
        force_vision=args.force_vision,
        render_pages=args.render_pages,
        vision_model=args.model,
        auto_vision=not args.no_auto_vision,
    )
    if not summary.get("ok"):
        print(f"[Error] {summary.get('error')}")
        return 1
    print(f"[Success] project={summary.get('project')}")
    print(f"  action={summary.get('action')}")
    print(f"  ready_for_stage1={summary.get('ready_for_stage1')}")
    print(
        f"  quality={summary.get('text_quality')} kind={summary.get('book_kind')} "
        f"suggest={summary.get('suggested_total_minutes')}min"
    )
    if summary.get("needs_user"):
        print(f"  needs_user: {summary.get('user_hint') or summary.get('message')}")
    print(f"  meta={summary.get('extract_meta')}")
    return 0 if summary.get("ready_for_stage1") or not summary.get("needs_user") else 2


if __name__ == "__main__":
    raise SystemExit(main())
