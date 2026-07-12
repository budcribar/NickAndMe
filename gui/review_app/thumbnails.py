"""
Cached UI thumbnails for Streamlit (avoids shipping full-res book plates every rerun).

Huge PDF embeds (~10k px) can segfault Streamlit/PIL under WSL — always thumbnail.
"""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
from typing import Optional, Tuple

# Prefer project thumbs under assets; fall back for non-character pages
THUMB_DIR = Path("assets/characters/_thumbs")
DEFAULT_MAX_PX = 384
# Never hand Streamlit an image larger than this (edge or pixel count)
SAFE_MAX_EDGE = 2048
SAFE_MAX_PIXELS = 25_000_000


def _thumb_out_dir(src_path: Path) -> Path:
    """
    Character assets → assets/characters/_thumbs.
    Book images → source/book_images/_thumbs (or sibling _thumbs).
    """
    parts = [p.lower() for p in src_path.parts]
    if "characters" in parts:
        return THUMB_DIR
    parent = src_path.parent
    if parent.name.lower() in ("book_images", "images"):
        return parent / "_thumbs"
    return parent / "_thumbs"


def _safe_open_rgb(src_path: Path) -> "Image.Image":
    """Open image without decoding multi-hundred-MP rasters into memory if possible."""
    from PIL import Image

    # Allow oversized PDF embeds; we immediately downscale
    Image.MAX_IMAGE_PIXELS = max(
        getattr(Image, "MAX_IMAGE_PIXELS", None) or 0,
        200_000_000,
    )
    im = Image.open(src_path)
    # JPEG: draft reduces decode cost before full load
    try:
        if getattr(im, "format", None) == "JPEG":
            im.draft("RGB", (SAFE_MAX_EDGE, SAFE_MAX_EDGE))
    except Exception:
        pass
    im = im.convert("RGB")
    w, h = im.size
    if w * h > SAFE_MAX_PIXELS or max(w, h) > SAFE_MAX_EDGE * 2:
        im.thumbnail((SAFE_MAX_EDGE, SAFE_MAX_EDGE), Image.Resampling.BILINEAR)
    return im


def ui_image_path(
    src: str,
    *,
    max_px: int = DEFAULT_MAX_PX,
    force: bool = False,
) -> str:
    """
    Return a JPEG thumbnail path for Streamlit display.
    On failure returns empty string (caller should skip st.image) — never the raw giant file.
    """
    if not src or not os.path.isfile(src):
        return src or ""
    try:
        src_path = Path(src)
        mtime = int(src_path.stat().st_mtime)
        size = src_path.stat().st_size
        # Tiny file already safe for UI
        if size <= 48_000 and not force:
            return src
        key = hashlib.md5(
            f"{src_path.resolve()}|{mtime}|{max_px}".encode()
        ).hexdigest()[:16]
        stem = src_path.stem
        out_dir = _thumb_out_dir(src_path)
        out = out_dir / f"{stem}_{key}.jpg"
        if out.is_file() and not force:
            return str(out)

        from PIL import Image

        out_dir.mkdir(parents=True, exist_ok=True)
        with _safe_open_rgb(src_path) as im:
            im.thumbnail((max_px, max_px), Image.Resampling.LANCZOS)
            im.save(out, "JPEG", quality=82, optimize=True)
        return str(out)
    except Exception:
        # Do NOT return original path — large embeds crash Streamlit on WSL
        return ""


def downscale_image_file(
    path: str | Path,
    *,
    max_edge: int = SAFE_MAX_EDGE,
    backup: bool = True,
) -> Tuple[bool, str]:
    """
    In-place downscale if image exceeds max_edge. Optional .bak_oversized copy.
    Returns (changed, message).
    """
    path = Path(path)
    if not path.is_file():
        return False, "missing"
    try:
        from PIL import Image

        Image.MAX_IMAGE_PIXELS = max(
            getattr(Image, "MAX_IMAGE_PIXELS", None) or 0,
            200_000_000,
        )
        with Image.open(path) as im:
            w, h = im.size
            if max(w, h) <= max_edge:
                return False, f"ok {w}x{h}"
            if backup:
                bak = path.with_suffix(path.suffix + ".bak_oversized")
                if not bak.is_file():
                    bak.write_bytes(path.read_bytes())
            im = im.convert("RGB") if im.mode not in ("RGB", "L") else im
            im.thumbnail((max_edge, max_edge), Image.Resampling.LANCZOS)
            # Preserve ext when possible
            if path.suffix.lower() in (".jpg", ".jpeg"):
                im.save(path, "JPEG", quality=90, optimize=True)
            elif path.suffix.lower() == ".png":
                im.save(path, "PNG", optimize=True)
            else:
                im.save(path)
            return True, f"{w}x{h} → {im.size[0]}x{im.size[1]}"
    except Exception as e:
        return False, str(e)


def clear_thumb_cache() -> int:
    """Delete cached thumbs. Returns count removed."""
    if not THUMB_DIR.is_dir():
        return 0
    n = 0
    for p in THUMB_DIR.glob("*.jpg"):
        try:
            p.unlink()
            n += 1
        except OSError:
            pass
    return n
