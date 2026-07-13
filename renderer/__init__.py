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
    check_environment,
    clip_output_path,
    composite_output_path,
    environment_needs,
    ffmpeg_is_available,
    file_is_usable,
    music_output_path,
    require_environment,
    resolve_default_duration,
    resolve_duration_profile,
    resolve_ffmpeg,
    resolve_prompt_limits,
)

__all__ = [
    "DEFAULT_CONFIG",
    "AgenticGenerationEngine",
    "GenerationFailure",
    "check_environment",
    "clip_output_path",
    "composite_output_path",
    "environment_needs",
    "ffmpeg_is_available",
    "file_is_usable",
    "music_output_path",
    "require_environment",
    "resolve_default_duration",
    "resolve_duration_profile",
    "resolve_ffmpeg",
    "resolve_prompt_limits",
]
