"""Stage 1 adaptation tools — PDF/book → scenes bible from the UI."""
from __future__ import annotations

import os
from pathlib import Path

import streamlit as st

from review_app import pipeline_api as api

st.title("📖 Adaptation")
st.caption("Import book → prepare → Stage 1 scene bible.")


def _apply_prepare_defaults(prep: dict) -> None:
    if prep.get("suggested_total_minutes"):
        st.session_state.stage1_total_minutes = max(
            3, min(180, int(prep["suggested_total_minutes"]))
        )
    if prep.get("suggested_chunk_pages"):
        st.session_state.stage1_chunk_pages = max(
            5, min(30, int(prep["suggested_chunk_pages"]))
        )


def _show_prepare_result(prep: dict) -> None:
    _apply_prepare_defaults(prep)
    ready = prep.get("ready_for_stage1")
    mins = prep.get("suggested_total_minutes")
    if ready:
        st.success(
            f"Book ready · suggested **{mins} min** film"
            + (f" · {prep.get('text_words')} words" if prep.get("text_words") else "")
        )
    elif prep.get("needs_user"):
        st.warning(prep.get("message") or "Book needs attention before Stage 1.")
        if prep.get("user_hint"):
            st.caption(prep["user_hint"])
    else:
        st.info(prep.get("message") or "Prepare finished.")


def _kind_label(kind: str | None) -> str:
    return {
        "picture_book": "picture book",
        "short": "short story",
        "novel": "novel",
    }.get(str(kind or ""), str(kind or "book"))


# ---- Status ----
try:
    status = api.stage1_status()
    img_status = api.book_images_status()
    book_meta = api.book_source_meta()
except Exception as e:
    st.error(str(e))
    st.stop()

paths = status.get("paths") or {}
pdf_ok = paths.get("pdf_exists") == "True"
book_ok = paths.get("book_exists") == "True"
key_ok = bool((os.environ.get("XAI_API_KEY") or "").strip())

_sc = status.get("scene_count") if status.get("present") else None
_img = img_status.get("count") or 0
_ch = status.get("characters") if status.get("present") else None
_bits = []
if _sc is not None:
    _bits.append(f"{_sc} scenes")
    if status.get("beat_count") is not None:
        _bits.append(f"{status.get('beat_count')} beats")
    if status.get("locations") is not None:
        _bits.append(f"{status.get('locations')} locations")
    if _ch is not None:
        _bits.append(f"{_ch} characters")
if _img:
    _bits.append(f"{_img} book images")
if _bits:
    st.caption(" · ".join(_bits))

if status.get("present") and (status.get("scene_count") or 0) > 0:
    runtime_sec = status.get("runtime_sec")
    runtime_txt = (
        f" · ~{int(runtime_sec) // 60} min runtime"
        if runtime_sec
        else ""
    )
    mtime = status.get("mtime") or ""
    cast_txt = ""
    try:
        seeds = api.stage1_character_seeds()
        if seeds:
            names = []
            for k, v in seeds.items():
                if isinstance(v, dict) and v.get("canonical_given_name"):
                    names.append(str(v["canonical_given_name"]))
                else:
                    names.append(k.replace("Character_", "").replace("_", " "))
            cast_txt = f" · cast: {', '.join(names)}"
    except Exception:
        cast_txt = f" · {_ch} character seeds" if _ch else ""
    st.success(
        f"**Adaptation complete** — Stage 1 on disk{runtime_txt}"
        + (f" · updated {mtime}" if mtime else "")
        + cast_txt
        + ". **Next:** Configuration, then **Characters**."
    )
elif status.get("present"):
    st.info("Stage 1 file exists but has no scenes yet — re-run Stage 1.")
else:
    st.caption("No Stage 1 scene bible yet.")

# ---- Book text readiness (gates Stage 1) ----
pdf_name = Path(paths.get("pdf") or "").name if pdf_ok else ""
has_source = pdf_ok or book_ok
_book_q = str(book_meta.get("text_quality") or "unknown")
_book_garbage = float(
    (book_meta.get("analysis") or {}).get("garbage_score")
    or book_meta.get("garbage_score")
    or 0
)
_book_engine = str(book_meta.get("text_engine") or "")
# Clean transcript required before Stage 1 is enabled
book_text_ready = bool(
    has_source
    and _book_q == "good"
    and _book_garbage < 0.45
    and book_ok
)

if not key_ok:
    st.error(
        "**XAI_API_KEY** is not set in this Streamlit process. "
        "Picture-book PDFs need it for **Re-import** (Grok vision). "
        "Stage 1 stays locked until the book text is clean.\n\n"
        "```powershell\n"
        "$env:XAI_API_KEY = \"…\"\n"
        "streamlit run gui/streamlit_app.py\n"
        "```"
    )

# ---- 1) Book (single Import / Re-import control) ----
st.divider()
st.subheader("1) Book — fix text here first")
st.caption(
    "Step order: **import / re-import** until the book text is clean → only then **Stage 1**."
)

if not has_source:
    st.warning("**Step 1 of 2:** Upload a PDF or TXT, then click **Import book**.")
elif book_text_ready:
    kind = _kind_label(book_meta.get("book_kind"))
    mins = book_meta.get("suggested_total_minutes")
    words = book_meta.get("text_words") or 0
    bits = []
    if pdf_name:
        bits.append(f"PDF `{pdf_name}`")
    if _img:
        bits.append(f"{_img} images")
    if words:
        bits.append(f"{words} words")
    if kind:
        bits.append(kind)
    if mins:
        bits.append(f"~{mins} min")
    if _book_engine:
        bits.append(f"via `{_book_engine}`")
    st.success(
        "**Book text is good — Stage 1 unlocked.** "
        + (" · ".join(bits) if bits else "")
    )
else:
    st.error(
        "**Step 1 incomplete — re-import required.** "
        "Current `book_full.txt` is still bad PDF OCR (Stage 1 is **disabled** until this is fixed)."
    )
    st.markdown(
        """
1. Ensure **`XAI_API_KEY`** is set in the shell that started Streamlit  
2. Click **Re-import book** below (must run **Grok vision** on page images)  
3. Wait until you see **“Book text is good — Stage 1 unlocked”**  
4. Only then use **Run Stage 1**
"""
    )
    with st.expander("Why? Preview of bad OCR text", expanded=True):
        try:
            preview = Path(paths.get("book_full") or "").read_text(
                encoding="utf-8", errors="ignore"
            )[:500]
            st.code(preview or "(empty)", language="text")
        except OSError:
            st.caption("Could not read book_full.txt")
        st.caption(
            f"quality=`{_book_q}` · garbage={_book_garbage:.2f} · engine=`{_book_engine or '—'}`"
        )

_last = st.session_state.get("adapt_last_import")
if isinstance(_last, dict) and _last.get("message"):
    st.caption(f"Last import: {_last.get('message')}")

uploaded = st.file_uploader(
    "PDF or TXT (optional if a book is already on disk)",
    type=["pdf", "txt"],
    accept_multiple_files=False,
    key="adapt_book_upload",
    help="Pick a new file to import/replace, or leave empty and re-import the book already in the project.",
)

with st.expander("Advanced", expanded=False):
    st.selectbox(
        "PDF page stills",
        options=["cover,sparse", "cover", "sparse", "cover,sparse,all", "none"],
        index=0,
        key="adapt_render_mode",
    )
    st.checkbox(
        "Auto vision when text is weak (needs XAI_API_KEY)",
        value=True,
        key="adapt_auto_vision",
    )
    st.checkbox("Force Grok vision even if text looks OK", value=False, key="adapt_force_vision")
    if img_status.get("present") and img_status.get("images"):
        st.caption(f"{img_status.get('count') or 0} page images on disk")
        cols = st.columns(4)
        proj = Path(paths.get("project") or ".")
        try:
            from review_app.thumbnails import ui_image_path
        except Exception:
            ui_image_path = None  # type: ignore
        for i, im in enumerate(img_status["images"][:8]):
            rel = im.get("path") or ""
            fp = proj / "source" / rel if not Path(rel).is_absolute() else Path(rel)
            if not fp.is_file():
                fp = proj / rel
            with cols[i % 4]:
                if not fp.is_file():
                    continue
                # Never st.image() full-res PDF embeds (WSL segfault risk)
                show = ui_image_path(str(fp), max_px=320) if ui_image_path else ""
                if show:
                    st.image(show, caption=f"p{im.get('page')}", width="stretch")
                else:
                    st.caption(f"p{im.get('page')} (preview skipped)")

render_mode = st.session_state.get("adapt_render_mode", "cover,sparse")
force_vision = bool(st.session_state.get("adapt_force_vision", False))
auto_vision = bool(st.session_state.get("adapt_auto_vision", True))

# Single control: Import (new file) or Re-import (on-disk / replace)
if uploaded is not None:
    btn_label = "Re-import book" if has_source else "Import book"
    btn_help = (
        "Save the uploaded file, extract images, and rebuild clean text "
        "(Grok vision when OCR is bad)."
    )
    can_run_import = True
elif has_source and not book_text_ready:
    btn_label = "Re-import book (required — fix OCR)"
    btn_help = (
        "Re-run extract + Grok vision so book_full.txt is real English. "
        "Stage 1 stays locked until this succeeds."
    )
    can_run_import = True
elif has_source:
    btn_label = "Re-import book"
    btn_help = "Optional: re-extract / re-run vision on the book already in this project."
    can_run_import = True
else:
    btn_label = "Import book"
    btn_help = "Choose a PDF or TXT file first."
    can_run_import = False

do_book = st.button(
    btn_label,
    type="primary",
    width="stretch",
    disabled=not can_run_import,
    help=btn_help,
    key="adapt_import_btn",
)
if has_source and not book_text_ready:
    st.caption(
        "↑ Do this step first. The Stage 1 button below stays **off** until import produces clean text."
    )

if do_book:
    prog = st.progress(0.0, text="Working…")
    log_box = st.empty()
    lines: list[str] = []

    def on_prep(ev: dict) -> None:
        event = ev.get("event") or ""
        total = max(1, int(ev.get("total") or 1))
        chunk = int(ev.get("chunk") or 0)
        if event in ("page_done", "page_start"):
            frac = min(0.95, 0.2 + 0.7 * (chunk / max(total, 1)))
        elif event == "done":
            frac = 1.0
        else:
            frac = min(0.95, chunk / total if total else 0.15)
        msg = ev.get("message") or event
        prog.progress(frac, text=msg)
        lines.append(f"[{event}] {msg}")
        log_box.code("\n".join(lines[-24:]), language="text")

    try:
        if uploaded is not None:
            result = api.import_book_upload(
                filename=uploaded.name,
                data=uploaded.getvalue(),
                extract_pdf=True,
                render_pages=render_mode,
                force=True,
                auto_prepare=True,
                progress_cb=on_prep,
            )
            prep = result.get("prepare") or result.get("extract") or {}
            _apply_prepare_defaults(prep if isinstance(prep, dict) else {})
            name = result.get("original_name") or uploaded.name
            st.session_state["adapt_last_import"] = {
                "message": (
                    f"Imported `{name}` · "
                    f"quality={prep.get('text_quality') or '—'} · "
                    f"ready={prep.get('ready_for_stage1')}"
                ),
                "prep": prep if isinstance(prep, dict) else {},
            }
        else:
            prep = api.prepare_book_source(
                force_extract=True,
                force_vision=force_vision,
                render_pages=render_mode,
                auto_vision=auto_vision,
                vision_model=os.environ.get("STAGE1_MODEL", "grok-4.5"),
                progress_cb=on_prep,
            )
            _apply_prepare_defaults(prep if isinstance(prep, dict) else {})
            st.session_state["adapt_last_import"] = {
                "message": (
                    f"Re-prepared on-disk book · "
                    f"quality={prep.get('text_quality') or '—'} · "
                    f"ready={prep.get('ready_for_stage1')}"
                ),
                "prep": prep if isinstance(prep, dict) else {},
            }
        try:
            from review_app.pipeline_progress import invalidate_progress_cache

            invalidate_progress_cache()
        except Exception:
            pass
        prog.progress(1.0, text="Done")
        # Clear uploader so the control reflects on-disk state after rerun
        if "adapt_book_upload" in st.session_state:
            del st.session_state["adapt_book_upload"]
        st.rerun()
    except Exception as e:
        st.error(str(e))
        if lines:
            log_box.code("\n".join(lines[-24:]), language="text")

# ---- 2) Stage 1 (only after book text is good) ----
st.divider()
st.subheader("2) Stage 1 — scene bible")

if "stage1_chunk_pages" not in st.session_state:
    st.session_state.stage1_chunk_pages = max(
        5, min(30, int(book_meta.get("suggested_chunk_pages") or 10))
    )
if "stage1_total_minutes" not in st.session_state:
    st.session_state.stage1_total_minutes = max(
        3, min(180, int(book_meta.get("suggested_total_minutes") or 90))
    )
if "stage1_model" not in st.session_state:
    st.session_state.stage1_model = os.environ.get("STAGE1_MODEL", "grok-4.5")
if "stage1_resume" not in st.session_state:
    st.session_state.stage1_resume = False
if "stage1_max_chunks" not in st.session_state:
    st.session_state.stage1_max_chunks = 0

chunk_pages = int(st.session_state.get("stage1_chunk_pages") or 10)
total_minutes = int(st.session_state.get("stage1_total_minutes") or 90)
model = str(st.session_state.get("stage1_model") or "grok-4.5")
resume = bool(st.session_state.get("stage1_resume", False))
max_chunks = int(st.session_state.get("stage1_max_chunks") or 0)

if "stage1_running" not in st.session_state:
    st.session_state.stage1_running = False

stage1_exists = bool(
    status.get("present") and int(status.get("scene_count") or 0) > 0
)
stage1_btn_label = "Re-run Stage 1" if stage1_exists else "Run Stage 1"
stage1_running = bool(st.session_state.stage1_running)

if not book_text_ready:
    st.info(
        f"**{stage1_btn_label} is locked.** Finish **§1 Book** first "
        "(Re-import until you see green *Book text is good*)."
    )
    # Keep a disabled control so the label/state is obvious
    st.button(
        f"{stage1_btn_label} (locked — re-import book first)",
        type="primary",
        disabled=True,
        width="stretch",
        key="adapt_stage1_btn_locked",
    )
    can_run = False
    run_clicked = False
else:
    with st.expander("Stage 1 options", expanded=False):
        st.number_input(
            "Pages per chunk",
            min_value=5,
            max_value=30,
            key="stage1_chunk_pages",
        )
        st.number_input(
            "Target runtime (minutes)",
            min_value=3,
            max_value=180,
            key="stage1_total_minutes",
        )
        st.text_input("Model", key="stage1_model")
        st.checkbox("Resume / merge into existing scenes", key="stage1_resume")
        st.number_input(
            "Max chunks (0 = all)",
            min_value=0,
            max_value=50,
            key="stage1_max_chunks",
        )

    chunk_pages = int(st.session_state.get("stage1_chunk_pages") or 10)
    total_minutes = int(st.session_state.get("stage1_total_minutes") or 90)
    model = str(st.session_state.get("stage1_model") or "grok-4.5")
    resume = bool(st.session_state.get("stage1_resume", False))
    max_chunks = int(st.session_state.get("stage1_max_chunks") or 0)

    st.caption(
        f"Will run ~**{total_minutes} min** target · "
        f"**{chunk_pages}** pages/chunk"
        + (" · resume" if resume else "")
        + (" · **running…**" if stage1_running else "")
    )

    can_run = key_ok and book_text_ready and not stage1_running
    if not key_ok:
        st.caption("Stage 1 also needs `XAI_API_KEY` for the LLM.")
    run_clicked = st.button(
        stage1_btn_label,
        type="primary",
        disabled=not can_run,
        width="stretch",
        help=(
            "Disabled while Stage 1 is running."
            if stage1_running
            else (
                "Re-run overwrites the current scene bible (with backup)."
                if stage1_exists
                else "Build the scene bible from the prepared book."
            )
        ),
        key="adapt_stage1_btn",
    )

if run_clicked and not stage1_running:
    st.session_state.stage1_running = True
    st.rerun()

if st.session_state.stage1_running:
    prog = st.progress(0.0, text="Starting Stage 1…")
    log_box = st.empty()
    lines: list[str] = []

    def on_progress(ev: dict) -> None:
        event = ev.get("event") or ""
        total = max(1, int(ev.get("total") or 1))
        chunk = int(ev.get("chunk") or 0)
        if event == "chunk_done":
            frac = min(1.0, chunk / total)
        elif event == "done":
            frac = 1.0
        elif event in ("normalize", "verify", "character_images"):
            frac = min(0.99, max(chunk / total, 0.9))
        elif event == "chunk_start":
            frac = min(0.99, max(0.0, (chunk - 1) / total))
        else:
            frac = min(0.05, chunk / max(total, 1))
        msg = ev.get("message") or event
        if ev.get("scenes") is not None:
            msg = f"{msg} · scenes so far: {ev.get('scenes')}"
        prog.progress(frac, text=msg)
        lines.append(f"[{event}] {msg}")
        log_box.code("\n".join(lines[-40:]), language="text")

    try:
        with st.spinner("Stage 1 running… keep this tab open"):
            summary = api.run_stage1_from_book(
                chunk_pages=chunk_pages,
                total_minutes=total_minutes,
                model=model.strip() or "grok-4.5",
                resume=resume,
                max_chunks=max_chunks,
                extract_pdf_if_needed=True,
                progress_cb=on_progress,
            )
        prog.progress(1.0, text="Complete")
        if summary.get("ok"):
            st.session_state["adapt_last_stage1"] = (
                f"{summary.get('scenes')} scenes · "
                f"{summary.get('locations')} locations · "
                f"~{int(summary.get('runtime_sec') or 0) // 60} min"
            )
        else:
            st.session_state["adapt_last_stage1"] = (
                "Finished with normalize issues"
            )
            if summary.get("hard_errors"):
                st.session_state["adapt_last_stage1_errors"] = summary[
                    "hard_errors"
                ][:30]
        try:
            from review_app.pipeline_progress import invalidate_progress_cache

            invalidate_progress_cache()
        except Exception:
            pass
    except Exception as e:
        st.session_state["adapt_last_stage1"] = f"Error: {e}"
        if lines:
            st.session_state["adapt_last_stage1_log"] = "\n".join(lines[-40:])
    finally:
        st.session_state.stage1_running = False
    st.rerun()

_last_s1 = st.session_state.get("adapt_last_stage1")
if _last_s1 and not stage1_running:
    if str(_last_s1).startswith("Error:"):
        st.error(_last_s1)
        if st.session_state.get("adapt_last_stage1_log"):
            st.code(st.session_state["adapt_last_stage1_log"], language="text")
    elif _last_s1 == "Finished with normalize issues":
        st.warning(_last_s1)
        if st.session_state.get("adapt_last_stage1_errors"):
            st.code("\n".join(st.session_state["adapt_last_stage1_errors"]))
    else:
        st.caption(f"Last Stage 1 run: {_last_s1}")

# ---- Stage 2 (clip plan for Scenes page) ----
st.divider()
st.subheader("Stage 2 — clip plan")
st.caption(
    "Turns the Stage 1 scene bible into the Grok **clip plan** used by **Scenes & Clips**. "
    "No API call — deterministic shot planner from story beats."
)
try:
    s2 = api.stage2_status()
except Exception as e:
    s2 = {}
    st.error(f"Stage 2 status: {e}")

if not s2.get("stage1_exists"):
    st.info("Finish **Stage 1** above before generating Stage 2.")
elif s2.get("stage2_ready"):
    st.success(
        f"Stage 2 ready: **{s2.get('stage2_scenes')}** scenes · "
        f"**{s2.get('stage2_clips')}** clips"
    )
    st.caption("Open **Scenes & Clips** to review and generate video.")
else:
    st.warning(
        f"Stage 1 has **{s2.get('stage1_scenes', 0)}** scenes, but the generate "
        "blueprint has no clips yet — Scenes & Clips will look empty until you run Stage 2."
    )

stage2_disabled = not s2.get("stage1_exists")
if st.button(
    "Re-generate Stage 2 plan" if s2.get("stage2_ready") else "▶ Generate Stage 2 plan",
    type="primary" if not s2.get("stage2_ready") else "secondary",
    disabled=stage2_disabled,
    key="adapt_stage2_btn",
):
    try:
        with st.spinner("Building Stage 2 clip plan…"):
            summary = api.run_stage2_from_stage1()
        st.session_state["adapt_last_stage2"] = (
            f"{summary.get('scenes')} scenes · {summary.get('clips')} clips · "
            f"~{int(summary.get('duration_sec') or 0)}s"
        )
        if summary.get("validation_issues"):
            st.session_state["adapt_last_stage2_notes"] = summary["validation_issues"][:20]
        try:
            from review_app.pipeline_progress import invalidate_progress_cache

            invalidate_progress_cache()
        except Exception:
            pass
        st.rerun()
    except Exception as e:
        st.error(str(e))

_last_s2 = st.session_state.get("adapt_last_stage2")
if _last_s2:
    st.caption(f"Last Stage 2 run: {_last_s2}")
    if st.session_state.get("adapt_last_stage2_notes"):
        with st.expander("Validation notes"):
            st.code("\n".join(st.session_state["adapt_last_stage2_notes"]))
