#!/usr/bin/env python3
"""
Extract book text (+ optional images) from a project PDF for Stage 1 adaptation.

Writes under projects/<id>/source/:
  book_full.txt              — page-marked plain text for Stage 1 chunks
  book_images/               — embedded images and/or rendered page stills
  book_images/manifest.json  — inventory for GUI / location refs

Usage (repo root):
  python scripts/two_stage_adaptation/extract_book_source.py
  python scripts/two_stage_adaptation/extract_book_source.py --pdf path/to/book.pdf
  python scripts/two_stage_adaptation/extract_book_source.py --render-pages cover,sparse
"""
from __future__ import annotations

import argparse
import json
import re
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

ROOT = Path(__file__).resolve().parents[2]


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


def find_pdf(source_dir: Path, explicit: Optional[Path] = None) -> Optional[Path]:
    if explicit:
        p = Path(explicit)
        return p if p.is_file() else None
    cands = sorted(source_dir.glob("*.pdf")) + sorted(source_dir.glob("*.PDF"))
    if not cands:
        return None
    # Prefer Nickandme / largest
    cands.sort(key=lambda p: (0 if "nick" in p.name.lower() else 1, -p.stat().st_size))
    return cands[0]


def _pdf_deps_hint() -> str:
    return (
        "PDF extract needs pypdf and/or pymupdf in the same Python env as Streamlit/CLI. "
        "Install into the project venv, then restart Streamlit:\n"
        "  pip install pypdf pymupdf\n"
        "  # or: pip install -r requirements-review.txt"
    )


def extract_text_pypdf(pdf_path: Path) -> Tuple[str, int]:
    try:
        from pypdf import PdfReader
    except ImportError as e:
        raise ImportError(_pdf_deps_hint()) from e

    reader = PdfReader(str(pdf_path))
    parts: List[str] = []
    for i, page in enumerate(reader.pages):
        t = (page.extract_text() or "").strip()
        if t:
            parts.append(f"--- PAGE {i + 1} ---\n{t}")
        else:
            parts.append(f"--- PAGE {i + 1} ---\n")
    return "\n\n".join(parts), len(reader.pages)


def extract_text_pymupdf(pdf_path: Path) -> Tuple[str, int]:
    try:
        import fitz  # PyMuPDF
    except ImportError as e:
        raise ImportError("pymupdf not installed") from e

    doc = fitz.open(str(pdf_path))
    parts: List[str] = []
    for i in range(doc.page_count):
        t = (doc.load_page(i).get_text("text") or "").strip()
        parts.append(f"--- PAGE {i + 1} ---\n{t}")
    n = doc.page_count
    doc.close()
    return "\n\n".join(parts), n


def extract_text_pymupdf_ocr(
    pdf_path: Path, *, dpi: int = 200, language: str = "eng"
) -> Tuple[str, int]:
    """
    Page OCR via PyMuPDF + system Tesseract (when installed).
    Raises ImportError/RuntimeError if OCR is unavailable.
    """
    try:
        import fitz  # PyMuPDF
    except ImportError as e:
        raise ImportError("pymupdf not installed") from e

    doc = fitz.open(str(pdf_path))
    parts: List[str] = []
    ocr_pages = 0
    for i in range(doc.page_count):
        page = doc.load_page(i)
        t = ""
        try:
            # Requires Tesseract on PATH; full=True OCRs whole page
            tp = page.get_textpage_ocr(dpi=dpi, language=language, full=True)
            t = (page.get_text("text", textpage=tp) or "").strip()
            ocr_pages += 1
        except Exception:
            t = (page.get_text("text") or "").strip()
        parts.append(f"--- PAGE {i + 1} ---\n{t}")
    n = doc.page_count
    doc.close()
    if ocr_pages == 0:
        raise RuntimeError(
            "PyMuPDF OCR failed on all pages. Install Tesseract "
            "(e.g. apt install tesseract-ocr / winget install UB-Mannheim.TesseractOCR) "
            "and ensure it is on PATH."
        )
    return "\n\n".join(parts), n


def _page_bodies(text: str) -> List[str]:
    parts = re.split(r"(?=--- PAGE \d+ ---)", text or "")
    bodies: List[str] = []
    for p in parts:
        p = p.strip()
        if not p:
            continue
        body = re.sub(r"^--- PAGE \d+ ---\s*", "", p).strip()
        bodies.append(body)
    return bodies


def _is_illustration_only(body: str) -> bool:
    b = (body or "").strip().lower()
    if not b:
        return True
    if b in ("(illustration only)", "illustration only", "[illustration only]"):
        return True
    if re.fullmatch(r"\(.*illustration.*\)", b):
        return True
    return False


def analyze_book_text(text: str, *, pages_hint: Optional[int] = None) -> Dict[str, Any]:
    """
    Score extract quality and suggest Stage 1 defaults (runtime, chunks).

    text_quality = readability of the words we have (good / poor / empty)
    text_density = layout density (normal / sparse) — sparse is normal for picture books
                   with illustration-only pages and is NOT the same as garbled OCR.
    """
    bodies = _page_bodies(text)
    pages = pages_hint if pages_hint and pages_hint > 0 else (len(bodies) or 1)
    if not bodies and text.strip():
        bodies = [text.strip()]

    # Strip page markers for global stats; ignore illustration placeholders as "content"
    content_bodies = [b for b in bodies if not _is_illustration_only(b)]
    plain = re.sub(r"--- PAGE \d+ ---", " ", text or "")
    plain = re.sub(r"\(\s*illustration only\s*\)", " ", plain, flags=re.I)
    plain = re.sub(r"\s+", " ", plain).strip()
    chars = len(plain)
    words = len(plain.split()) if plain else 0
    letters = sum(1 for c in plain if c.isalpha())
    letter_ratio = (letters / chars) if chars else 0.0

    empty_pages = sum(1 for b in bodies if _is_illustration_only(b) or len(b) < 20)
    sparse_pages = sum(1 for b in bodies if len(b) < 120)
    empty_ratio = empty_pages / max(len(bodies), 1)
    sparse_ratio = sparse_pages / max(len(bodies), 1)
    avg_chars = chars / max(pages, 1)

    # Heuristic garbage: OCR noise / broken words (not merely short picture-book text)
    garbage_score = 0.0
    word_list = plain.split() if plain else []
    if chars > 40:
        weird = len(re.findall(r"[^\w\s'.,!?;:\-\"()…°]", plain))
        garbage_score += min(1.0, weird / max(chars, 1) * 10)
        if letter_ratio < 0.55:
            garbage_score += 0.35
        if letter_ratio < 0.4:
            garbage_score += 0.35
        bad_tokens = len(re.findall(r"\b\w*[0-9]\w*\b", plain))
        garbage_score += min(0.35, bad_tokens / max(words, 1))
        # classic OCR garble: mixed case junk mid-token, punctuation salad
        garble_hits = len(
            re.findall(
                r"\b(?:[A-Za-z]*[0-9][A-Za-z0-9]*|[A-Za-z]{1,2}[;:][A-Za-z]{2,})\b",
                plain,
            )
        )
        garbage_score += min(0.4, garble_hits / max(words, 1) * 4)
        # High share of short nonsense / non-vowel-heavy tokens (OCR soup)
        if word_list:
            junk = 0
            for w in word_list:
                wl = re.sub(r"[^A-Za-z]", "", w)
                if len(wl) < 2:
                    continue
                vowels = len(re.findall(r"[aeiouAEIOU]", wl))
                if vowels == 0 and len(wl) >= 3:
                    junk += 1
                elif re.search(r"[;:°•]|[A-Z]{3,}[a-z]+[A-Z]", w):
                    junk += 1
            garbage_score += min(0.45, junk / max(len(word_list), 1) * 2.5)
        # Clear PDF OCR failure markers only (do NOT use real book words like potty/haughty)
        if re.search(
            r"\bNOOPLE\b|HEAP\s+POG|Noodle\s+Head\s+Dos\b|Duster\s+the\s+Noodle|"
            r"Junin\s+arouhd|\bwhrte\b|IMIShil|Pebra\s+McG|111,AI-rated|UPIliaty|"
            r"Goihg\s+oh|°Aide|Moirtra",
            plain,
            re.I,
        ):
            garbage_score = max(garbage_score, 0.75)
        # Only treat non-ASCII soup as garbage when dense (vision text is usually clean ASCII)
        non_ascii = len(re.findall(r"[^\x00-\x7F]", plain))
        if non_ascii > 8 and non_ascii / max(chars, 1) > 0.02:
            garbage_score += 0.15

    garbage_score = min(1.0, garbage_score)

    # Density (layout) vs quality (readability)
    if empty_ratio >= 0.35 and avg_chars < 500:
        text_density = "sparse"
    else:
        text_density = "normal"

    if chars < 40 and not content_bodies:
        text_quality = "empty"
    elif garbage_score >= 0.45 or (letter_ratio < 0.45 and chars > 100):
        text_quality = "poor"
    elif words >= 40 and letter_ratio >= 0.55 and garbage_score < 0.35:
        # Clean short verse / vision transcript is good even with illustration-only pages
        text_quality = "good"
    elif words >= 25 and letter_ratio >= 0.65 and garbage_score < 0.3:
        text_quality = "good"
    elif text_density == "sparse" and words >= 40 and garbage_score < 0.35:
        # Picture book with real dialogue + many art-only pages
        text_quality = "good"
    elif text_density == "sparse" and (words < 20 or garbage_score >= 0.35):
        text_quality = "poor" if garbage_score >= 0.4 or words < 15 else "sparse"
    else:
        text_quality = "good" if garbage_score < 0.35 else "poor"

    # Book kind → runtime suggestion
    if pages <= 40 and (
        avg_chars < 450
        or sparse_ratio >= 0.4
        or text_density == "sparse"
        or text_quality in ("poor", "empty", "sparse")
    ):
        book_kind = "picture_book"
        minutes = int(round(max(3, min(15, pages * 0.4 + words / 200))))
        chunk_pages = max(5, min(pages, 15))
    elif pages <= 100 and words < 25000:
        book_kind = "short"
        minutes = int(round(max(8, min(45, words / 200 + pages * 0.25))))
        chunk_pages = 10
    else:
        book_kind = "novel"
        minutes = int(round(max(45, min(120, words / 280 + 25))))
        chunk_pages = 10

    notes: List[str] = []
    if text_quality == "poor":
        notes.append(
            "Text looks garbled (OCR noise). Prefer Grok vision on page images, "
            "Tesseract re-extract, or paste a clean transcript into book_full.txt."
        )
    elif text_quality == "empty":
        notes.append(
            "Almost no readable text. Use Grok vision on page images or paste a transcript."
        )
    elif text_quality == "sparse":
        notes.append(
            "Text layer is very thin (few words). Vision transcription may help fill gaps."
        )
    if text_density == "sparse" and text_quality == "good":
        notes.append(
            "Layout is illustration-heavy (normal for picture books) but the wording "
            "looks clean enough for Stage 1."
        )
    if book_kind == "picture_book":
        notes.append(
            f"Treated as picture book (~{pages} pages). Suggested Stage 1 runtime "
            f"{minutes} min — not a feature-length 90."
        )

    ready = text_quality == "good"

    return {
        "pages": pages,
        "text_chars": chars,
        "text_words": words,
        "avg_chars_per_page": round(avg_chars, 1),
        "letter_ratio": round(letter_ratio, 3),
        "empty_page_ratio": round(empty_ratio, 3),
        "sparse_page_ratio": round(sparse_ratio, 3),
        "garbage_score": round(min(1.0, garbage_score), 3),
        "text_quality": text_quality,
        "text_density": text_density,
        "book_kind": book_kind,
        "ready_for_stage1": ready,
        "suggested_total_minutes": minutes,
        "suggested_chunk_pages": chunk_pages,
        "notes": notes,
    }


def suggest_stage1_defaults_from_text(
    text: str, *, pages_hint: Optional[int] = None
) -> Dict[str, Any]:
    """Public alias used by GUI / pipeline_api."""
    return analyze_book_text(text, pages_hint=pages_hint)


def extract_text(
    pdf_path: Path,
    *,
    try_ocr_if_poor: bool = True,
) -> Tuple[str, int, str, Dict[str, Any]]:
    """
    Return (text, page_count, engine, analysis).

    Prefer pymupdf embedded text; if quality is poor/sparse and OCR is available,
    replace with Tesseract OCR via PyMuPDF.
    """
    pymupdf_err: Optional[BaseException] = None
    text = ""
    n = 0
    engine = "none"

    try:
        text, n = extract_text_pymupdf(pdf_path)
        engine = "pymupdf"
    except ImportError as e:
        pymupdf_err = e
    except Exception as e:
        pymupdf_err = e

    if engine == "none":
        try:
            text, n = extract_text_pypdf(pdf_path)
            engine = "pypdf"
        except ImportError as e:
            raise ImportError(_pdf_deps_hint()) from e
        except Exception:
            if pymupdf_err is not None and not isinstance(pymupdf_err, ImportError):
                raise pymupdf_err
            raise

    analysis = analyze_book_text(text, pages_hint=n)
    if try_ocr_if_poor and analysis.get("text_quality") in ("poor", "empty", "sparse"):
        try:
            ocr_text, ocr_n = extract_text_pymupdf_ocr(pdf_path)
            ocr_analysis = analyze_book_text(ocr_text, pages_hint=ocr_n)
            # Prefer OCR when it improves letter density / word count meaningfully
            better = (
                ocr_analysis.get("text_quality") == "good"
                or (
                    int(ocr_analysis.get("text_words") or 0)
                    > int(analysis.get("text_words") or 0) * 1.25
                    and float(ocr_analysis.get("letter_ratio") or 0)
                    > float(analysis.get("letter_ratio") or 0)
                )
            )
            if better or analysis.get("text_quality") in ("empty", "sparse"):
                # For sparse picture books OCR may still be weak but often better
                if int(ocr_analysis.get("text_words") or 0) >= int(
                    analysis.get("text_words") or 0
                ):
                    text, n, engine = ocr_text, ocr_n, "pymupdf_ocr"
                    analysis = ocr_analysis
                    analysis["ocr_attempted"] = True
                    analysis["ocr_used"] = True
                else:
                    analysis["ocr_attempted"] = True
                    analysis["ocr_used"] = False
            else:
                analysis["ocr_attempted"] = True
                analysis["ocr_used"] = False
        except Exception as e:
            analysis["ocr_attempted"] = True
            analysis["ocr_used"] = False
            analysis["ocr_error"] = str(e)[:300]

    analysis["text_engine"] = engine
    return text, n, engine, analysis


# PDF embeds can be 10k+ px covers — Streamlit/PIL on WSL segfaults on those.
_EMBED_MAX_EDGE = 2500
_EMBED_MAX_PIXELS = 12_000_000


def _normalize_embed_image(
    im: "Image.Image", max_edge: int = _EMBED_MAX_EDGE
) -> "Image.Image":
    """Downscale huge embeds before write (keeps orientation work cheap downstream)."""
    from PIL import Image

    w, h = im.size
    if w * h > _EMBED_MAX_PIXELS or max(w, h) > max_edge:
        im = im.copy()
        im.thumbnail((max_edge, max_edge), Image.Resampling.LANCZOS)
    return im


def _apply_page_rotation_to_image_bytes(
    image_bytes: bytes, page_rotation: int, ext: str
) -> Tuple[bytes, str, int, int]:
    """
    PDF pages may have rotation=90/180/270 while extract_image returns raw pixels.
    Apply the page rotation so saved plates match on-screen orientation.
    Always caps very large embeds so GUI/WSL do not load 100MP+ rasters.
    Returns (bytes, ext, width, height).
    """
    rot = int(page_rotation or 0) % 360
    try:
        from PIL import Image
        import io

        Image.MAX_IMAGE_PIXELS = max(
            getattr(Image, "MAX_IMAGE_PIXELS", None) or 0,
            200_000_000,
        )
        im = Image.open(io.BytesIO(image_bytes))
        if rot:
            # PDF rotation is clockwise; PIL rotate is counter-clockwise → use negative
            im = im.rotate(-rot, expand=True)
        im = _normalize_embed_image(im)
        buf = io.BytesIO()
        out_ext = ext if ext in ("png", "jpg", "jpeg", "webp") else "png"
        # Prefer JPEG for large plates after normalize
        if max(im.size) >= 1200 and out_ext in ("png", "jpg", "jpeg", "webp"):
            if im.mode not in ("RGB", "L"):
                im = im.convert("RGB")
            im.save(buf, format="JPEG", quality=90, optimize=True)
            out_ext = "jpg"
        elif out_ext in ("jpg", "jpeg"):
            if im.mode not in ("RGB", "L"):
                im = im.convert("RGB")
            im.save(buf, format="JPEG", quality=92, optimize=True)
            out_ext = "jpg"
        else:
            im.save(buf, format="PNG", optimize=True)
            out_ext = "png"
        return buf.getvalue(), out_ext, im.size[0], im.size[1]
    except Exception:
        return image_bytes, ext, 0, 0


def extract_embedded_images(pdf_path: Path, out_dir: Path) -> List[Dict[str, Any]]:
    """Extract embedded raster images via PyMuPDF. Returns manifest rows."""
    try:
        import fitz
    except ImportError:
        return []

    out_dir.mkdir(parents=True, exist_ok=True)
    doc = fitz.open(str(pdf_path))
    rows: List[Dict[str, Any]] = []
    seen_xrefs: set = set()
    for page_index in range(doc.page_count):
        page = doc.load_page(page_index)
        page_rot = int(getattr(page, "rotation", 0) or 0)
        for img in doc.get_page_images(page_index, full=True):
            xref = img[0]
            if xref in seen_xrefs:
                continue
            seen_xrefs.add(xref)
            try:
                base = doc.extract_image(xref)
            except Exception:
                continue
            if not base or not base.get("image"):
                continue
            ext = (base.get("ext") or "png").lower()
            if ext == "jpeg":
                ext = "jpg"
            w, h = base.get("width"), base.get("height")
            # Skip tiny icons
            if (w or 0) < 64 or (h or 0) < 64:
                continue
            raw = base["image"]
            fixed, ext2, w2, h2 = _apply_page_rotation_to_image_bytes(raw, page_rot, ext)
            if w2 and h2:
                w, h = w2, h2
            ext = ext2 or ext
            name = f"embedded_p{page_index + 1:03d}_x{xref}.{ext}"
            path = out_dir / name
            path.write_bytes(fixed)
            rows.append(
                {
                    "kind": "embedded",
                    "page": page_index + 1,
                    "path": str(path.relative_to(out_dir.parent)).replace("\\", "/"),
                    "width": w,
                    "height": h,
                    "xref": xref,
                    "page_rotation": page_rot,
                    "relevance": "embedded_figure",
                }
            )
    doc.close()
    return rows


def _page_text_len(doc: Any, page_index: int) -> int:
    t = doc.load_page(page_index).get_text("text") or ""
    return len(re.sub(r"\s+", "", t))


def render_relevant_pages(
    pdf_path: Path,
    out_dir: Path,
    *,
    modes: Sequence[str],
    dpi: int = 144,
    sparse_char_threshold: int = 80,
    max_renders: int = 40,
) -> List[Dict[str, Any]]:
    """
    Render page stills that may help location/character refs:
      - cover: page 1
      - sparse: low text density (photo/illustration pages)
      - all: every Nth page (not default)
    """
    try:
        import fitz
    except ImportError:
        return []

    modes_l = {m.strip().lower() for m in modes if m.strip()}
    if not modes_l or modes_l == {"none"}:
        return []

    out_dir.mkdir(parents=True, exist_ok=True)
    doc = fitz.open(str(pdf_path))
    n = doc.page_count
    want: List[Tuple[int, str]] = []  # 0-based page, reason

    if "cover" in modes_l or "first" in modes_l:
        want.append((0, "cover"))

    if "sparse" in modes_l or "photos" in modes_l:
        for i in range(n):
            if _page_text_len(doc, i) < sparse_char_threshold:
                want.append((i, "sparse_text_page"))

    if "all" in modes_l:
        step = max(1, n // max(1, max_renders))
        for i in range(0, n, step):
            want.append((i, "sampled_page"))

    # de-dupe pages, keep first reason
    seen = set()
    ordered: List[Tuple[int, str]] = []
    for i, reason in want:
        if i in seen:
            continue
        seen.add(i)
        ordered.append((i, reason))
    ordered = ordered[:max_renders]

    zoom = dpi / 72.0
    mat = fitz.Matrix(zoom, zoom)
    rows: List[Dict[str, Any]] = []
    for i, reason in ordered:
        page = doc.load_page(i)
        pix = page.get_pixmap(matrix=mat, alpha=False)
        name = f"page_{i + 1:03d}_{reason}.png"
        path = out_dir / name
        pix.save(str(path))
        rows.append(
            {
                "kind": "rendered_page",
                "page": i + 1,
                "path": str(path.relative_to(out_dir.parent)).replace("\\", "/"),
                "width": pix.width,
                "height": pix.height,
                "relevance": reason,
                "text_chars": _page_text_len(doc, i),
            }
        )
    doc.close()
    return rows


def extract_book_source(
    *,
    project_id: Optional[str] = None,
    pdf_path: Optional[Path] = None,
    write_text: bool = True,
    extract_images: bool = True,
    render_modes: Sequence[str] = ("cover", "sparse"),
    dpi: int = 144,
    force: bool = False,
) -> Dict[str, Any]:
    """
    Extract text + images from project PDF into source/.
    Returns summary dict for CLI/UI.
    """
    project = _project_dir(project_id)
    source = project / "source"
    source.mkdir(parents=True, exist_ok=True)
    pdf = find_pdf(source, pdf_path)
    if pdf is None:
        raise FileNotFoundError(
            f"No PDF in {source}. Place Nickandme.PDF (or any .pdf) under source/."
        )

    book_txt = source / "book_full.txt"
    img_dir = source / "book_images"
    summary: Dict[str, Any] = {
        "project": project.name,
        "pdf": str(pdf),
        "pdf_name": pdf.name,
        "pdf_bytes": pdf.stat().st_size,
        "book_full": str(book_txt),
        "images_dir": str(img_dir),
        "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
    }

    # Text
    need_text = force or (not book_txt.is_file()) or (
        book_txt.is_file() and pdf.stat().st_mtime > book_txt.stat().st_mtime
    )
    analysis: Dict[str, Any] = {}
    if write_text and need_text:
        text, pages, engine, analysis = extract_text(pdf, try_ocr_if_poor=True)
        book_txt.write_text(text, encoding="utf-8")
        summary["text_extracted"] = True
        summary["text_engine"] = engine
        summary["pages"] = pages
        summary["text_chars"] = analysis.get("text_chars", len(text))
        summary["text_words"] = analysis.get("text_words", len(text.split()))
    else:
        summary["text_extracted"] = False
        summary["text_chars"] = book_txt.stat().st_size if book_txt.is_file() else 0
        if book_txt.is_file():
            raw = book_txt.read_text(encoding="utf-8", errors="ignore")
            summary["pages"] = len(re.findall(r"--- PAGE \d+ ---", raw))
            summary["text_chars"] = len(raw)
            analysis = analyze_book_text(raw, pages_hint=summary.get("pages"))
            summary["text_words"] = analysis.get("text_words", 0)
            summary["text_engine"] = "existing_book_full"

    # Picture-book / poor OCR → ensure we have page stills for every page
    modes = list(render_modes or [])
    if analysis.get("book_kind") == "picture_book" or analysis.get("text_density") == "sparse" or analysis.get("text_quality") in (
        "poor",
        "sparse",
        "empty",
    ):
        if modes and "all" not in modes:
            modes = list(dict.fromkeys([*modes, "all"]))
            summary["render_modes_upgraded"] = modes
            analysis.setdefault("notes", []).append(
                "Auto-added page stills for all pages (picture book / weak text layer)."
            )

    # Images
    image_rows: List[Dict[str, Any]] = []
    if extract_images:
        image_rows.extend(extract_embedded_images(pdf, img_dir))
        if modes:
            image_rows.extend(
                render_relevant_pages(pdf, img_dir, modes=modes, dpi=dpi)
            )
        manifest = {
            "schema_version": "book_images.v1",
            "pdf": pdf.name,
            "extracted_at": summary["ts"],
            "count": len(image_rows),
            "embedded_count": sum(1 for r in image_rows if r.get("kind") == "embedded"),
            "rendered_count": sum(1 for r in image_rows if r.get("kind") == "rendered_page"),
            "images": image_rows,
            "notes": (
                "Embedded figures extracted when present. "
                "Sparse-text pages rendered as stills (possible photo/illustration pages). "
                "Use as location/character reference candidates — not auto-locked."
            ),
        }
        img_dir.mkdir(parents=True, exist_ok=True)
        (img_dir / "manifest.json").write_text(
            json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
        )
        summary["images"] = len(image_rows)
        summary["embedded_images"] = manifest["embedded_count"]
        summary["rendered_pages"] = manifest["rendered_count"]
        summary["manifest"] = str(img_dir / "manifest.json")
    else:
        summary["images"] = 0

    # Stage 1 defaults + quality for GUI (source/extract_meta.json)
    if analysis:
        summary["text_quality"] = analysis.get("text_quality")
        summary["book_kind"] = analysis.get("book_kind")
        summary["suggested_total_minutes"] = analysis.get("suggested_total_minutes")
        summary["suggested_chunk_pages"] = analysis.get("suggested_chunk_pages")
        summary["analysis"] = analysis
        meta = {
            "schema_version": "extract_meta.v1",
            "pdf": pdf.name,
            "extracted_at": summary["ts"],
            "text_engine": summary.get("text_engine"),
            "pages": summary.get("pages"),
            "text_chars": summary.get("text_chars"),
            "text_words": summary.get("text_words"),
            "text_quality": analysis.get("text_quality"),
            "book_kind": analysis.get("book_kind"),
            "suggested_total_minutes": analysis.get("suggested_total_minutes"),
            "suggested_chunk_pages": analysis.get("suggested_chunk_pages"),
            "ocr_attempted": analysis.get("ocr_attempted", False),
            "ocr_used": analysis.get("ocr_used", False),
            "ocr_error": analysis.get("ocr_error"),
            "notes": analysis.get("notes") or [],
            "analysis": analysis,
        }
        meta_path = source / "extract_meta.json"
        meta_path.write_text(
            json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
        )
        summary["extract_meta"] = str(meta_path)

    summary["ok"] = True
    return summary


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--project", default=None)
    ap.add_argument("--pdf", default=None)
    ap.add_argument("--no-text", action="store_true")
    ap.add_argument("--no-images", action="store_true")
    ap.add_argument(
        "--render-pages",
        default="cover,sparse",
        help="Comma list: cover,sparse,all,none",
    )
    ap.add_argument("--dpi", type=int, default=144)
    ap.add_argument("--force", action="store_true")
    args = ap.parse_args()

    modes = [m.strip() for m in args.render_pages.split(",") if m.strip()]
    if "none" in modes:
        modes = []

    try:
        summary = extract_book_source(
            project_id=args.project,
            pdf_path=Path(args.pdf) if args.pdf else None,
            write_text=not args.no_text,
            extract_images=not args.no_images,
            render_modes=modes,
            dpi=args.dpi,
            force=args.force,
        )
    except Exception as e:
        print(f"[Error] {e}")
        return 1

    print(f"[Success] PDF: {summary.get('pdf_name')}")
    print(
        f"  pages={summary.get('pages')} text_chars={summary.get('text_chars')} "
        f"extracted={summary.get('text_extracted')} engine={summary.get('text_engine')}"
    )
    print(
        f"  quality={summary.get('text_quality')} kind={summary.get('book_kind')} "
        f"suggest_runtime={summary.get('suggested_total_minutes')}min "
        f"chunk_pages={summary.get('suggested_chunk_pages')}"
    )
    print(
        f"  images={summary.get('images')} "
        f"(embedded={summary.get('embedded_images')}, rendered={summary.get('rendered_pages')})"
    )
    print(f"  book_full={summary.get('book_full')}")
    if summary.get("manifest"):
        print(f"  manifest={summary.get('manifest')}")
    if summary.get("extract_meta"):
        print(f"  extract_meta={summary.get('extract_meta')}")
    for note in (summary.get("analysis") or {}).get("notes") or []:
        print(f"  note: {note}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
