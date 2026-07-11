"""Streamlit review UI helpers for the film renderer."""
from __future__ import annotations

import sys
from pathlib import Path

# Ensure workspace root is importable (renderer package) whenever GUI code loads.
_GUI = Path(__file__).resolve().parent.parent
_WORKSPACE = _GUI.parent
for _p in (_WORKSPACE, _GUI):
    _s = str(_p)
    if _s not in sys.path:
        sys.path.insert(0, _s)
