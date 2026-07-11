"""
Film media renderer — realizes Stage 2 clip plans into video assets.

Used by both frontends:
  python -m cli
  streamlit run gui/streamlit_app.py
"""
from __future__ import annotations

from renderer.engine import (
    DEFAULT_CONFIG,
    AgenticGenerationEngine,
    GenerationFailure,
    clip_output_path,
    composite_output_path,
    file_is_usable,
    music_output_path,
)

__all__ = [
    "DEFAULT_CONFIG",
    "AgenticGenerationEngine",
    "GenerationFailure",
    "clip_output_path",
    "composite_output_path",
    "file_is_usable",
    "music_output_path",
]
