"""
Cached UI thumbnails for Streamlit (avoids shipping full ~250–300KB PNGs every rerun).
"""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
from typing import Optional

THUMB_DIR = Path("assets/characters/_thumbs")
DEFAULT_MAX_PX = 384


def ui_image_path(
    src: str,
    *,
    max_px: int = DEFAULT_MAX_PX,
    force: bool = False,
) -> str:
    """
    Return a JPEG thumbnail path for Streamlit display.
    Falls back to original path if resize fails or source missing.
    """
    if not src or not os.path.isfile(src):
        return src
    try:
        src_path = Path(src)
        mtime = int(src_path.stat().st_mtime)
        size = src_path.stat().st_size
        # Full-res small enough already
        if size <= 48_000 and not force:
            return src
        key = hashlib.md5(f"{src_path.resolve()}|{mtime}|{max_px}".encode()).hexdigest()[:16]
        stem = src_path.stem
        out = THUMB_DIR / f"{stem}_{key}.jpg"
        if out.is_file() and not force:
            return str(out)

        from PIL import Image

        THUMB_DIR.mkdir(parents=True, exist_ok=True)
        with Image.open(src_path) as im:
            im = im.convert("RGB")
            im.thumbnail((max_px, max_px), Image.Resampling.LANCZOS)
            im.save(out, "JPEG", quality=82, optimize=True)
        return str(out)
    except Exception:
        return src


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
