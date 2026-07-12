import base64
import json
import mimetypes
import os
import re
import signal
import sys
import time
import subprocess
import urllib.request
import urllib.error
from pathlib import Path
from typing import Dict, Any, List, Optional, Tuple

# Optional Google GenAI SDK (Gemini QA / Imagen fallbacks only)
try:
    from google import genai
    from google.genai import types
except ImportError:
    genai = None
    types = None

STATE_FILE = "pipeline_state.json"
# Stage 2 Grok clip plan is the active generate blueprint (see prompts/ + docs/two_stage_adaptation/)
# Package: renderer.engine — frontends: python -m cli, streamlit run gui/streamlit_app.py
BLUEPRINT_FILE = "nickandme.clips.grok.json"
CONFIG_FILE = "pipeline_config.json"

XAI_API_BASE = "https://api.x.ai/v1"

DEFAULT_CONFIG = {
    "video_provider": "grok",
    "character_design_provider": "grok",
    "qa_provider": "grok",
    "image_model_name": "grok-imagine-image-quality",
    "qa_model_name": "grok-4.5",
    "model_name": "grok-imagine-video",
    "use_video_audio_for_music": True,
    "regenerate_silent_clips": True,
    "merge_scene_after_each_clip": True,
    # Last-frame continuation only for true continuous shots (not cuts / new locations)
    "smart_continuation": True,
    # Re-generate when Grok QA rejects a clip
    "qa_retry_on_fail": True,
    "qa_max_retries": 2,
    # Prefer Grok native audio. TTS is optional fallback only (often robotic).
    # Set ensure_dialogue_audio true only if native speech is missing/weak.
    "ensure_dialogue_audio": False,
    "dialogue_audio_mode": "replace",
    "dialogue_tts_volume": 1.0,
    "native_audio_mix_volume": 0.12,
    "composite_audio_gain_db": 6.0,
    # After each scene is Approved, rebuild a running work-in-progress film
    "rebuild_wip_movie_after_scene": True,
    "wip_movie_path": "assets/movie_wip.mp4",
    "aspect_ratio": "16:9",
    # Legacy flat default; prefer resolve_default_duration() from duration_defaults
    "duration_seconds": 8,
    # When true, generate/plan use duration_defaults map (model/provider/resolution).
    # When false, always use duration_seconds as the flat override.
    "use_duration_defaults": True,
    "resolution": "720p",
    "qa_frame_count": 4,
    # Active production blueprint (Stage 2 Grok clip plan by default)
    "blueprint_file": "nickandme.clips.grok.json",
    # Model/provider-aware clip duration policy (seconds)
    "duration_defaults": {
        "fallback": 8,
        "providers": {
            "grok": {
                "default": 8,
                "min": 1,
                "max": 15,
                "prefer_min": 6,
                "prefer_max": 10,
                "models": {
                    "grok-imagine-video": {
                        "default": 8,
                        "prefer_min": 6,
                        "prefer_max": 10,
                    },
                    "grok-imagine-video-1.5": {
                        "default": 8,
                        "prefer_min": 6,
                        "prefer_max": 10,
                    },
                },
                "resolutions": {
                    "480p": {"default": 6},
                    "720p": {"default": 8},
                    "1080p": {"default": 8},
                },
            },
            "veo": {
                "default": 8,
                "min": 4,
                "max": 8,
                "prefer_min": 7,
                "prefer_max": 8,
                "models": {
                    "veo-3.1": {"default": 8},
                },
            },
        },
    },
    # USD planning rates for Streamlit cost estimates (edit if xAI list prices change)
    "cost_estimates": {
        "currency": "USD",
        "video_output_per_sec": {"480p": 0.05, "720p": 0.07, "1080p": 0.25},
        "video_input_image": 0.002,
        "video_input_per_sec": 0.01,
        "assume_ref_image_per_clip": True,
        "assume_avg_retries": 0.0,
        "notes": "Estimates only — not invoices. Update rates in pipeline_config.cost_estimates.",
    },
    # Models offered in the review UI for side-by-side scene comparison
    "available_video_models": [
        {
            "provider": "grok",
            "model_name": "grok-imagine-video",
            "label": "Grok Imagine Video",
        },
        {
            "provider": "grok",
            "model_name": "grok-imagine-video-1.5",
            "label": "Grok Imagine Video 1.5",
        },
        {
            "provider": "veo",
            "model_name": "veo-3.1",
            "label": "Google Veo 3.1",
        },
    ],
}


def _deep_setdefault(dst: Dict[str, Any], src: Dict[str, Any]) -> None:
    for key, value in src.items():
        if key not in dst:
            dst[key] = json.loads(json.dumps(value)) if isinstance(value, (dict, list)) else value
        elif isinstance(value, dict) and isinstance(dst.get(key), dict):
            _deep_setdefault(dst[key], value)


def resolve_duration_profile(
    config: Optional[Dict[str, Any]] = None,
    *,
    provider: Optional[str] = None,
    model_name: Optional[str] = None,
    resolution: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Resolve clip duration policy from config.duration_defaults.

    Returns dict with: default, min, max, prefer_min, prefer_max, source (label).
    """
    cfg = config or DEFAULT_CONFIG
    dd = cfg.get("duration_defaults") or DEFAULT_CONFIG.get("duration_defaults") or {}
    prov = str(provider or cfg.get("video_provider") or "grok").lower().strip()
    model = str(model_name or cfg.get("model_name") or "").lower().strip()
    res = str(resolution or cfg.get("resolution") or "720p").lower().strip()

    out: Dict[str, Any] = {
        "default": int(dd.get("fallback", 8)),
        "min": 1,
        "max": 15,
        "prefer_min": 6,
        "prefer_max": 10,
        "source": "fallback",
    }
    providers = dd.get("providers") or {}
    pblock = providers.get(prov) or providers.get(prov.split("-")[0]) or {}
    if pblock:
        for k in ("default", "min", "max", "prefer_min", "prefer_max"):
            if k in pblock and pblock[k] is not None:
                out[k] = int(pblock[k])
        out["source"] = f"provider:{prov}"

    models = (pblock.get("models") or {}) if isinstance(pblock, dict) else {}
    mblock = models.get(model) if model else None
    if not mblock and model:
        # prefix match e.g. grok-imagine-video-1.5-foo
        for mk, mv in models.items():
            if model.startswith(str(mk).lower()) or str(mk).lower() in model:
                mblock = mv
                break
    if isinstance(mblock, dict):
        for k in ("default", "min", "max", "prefer_min", "prefer_max"):
            if k in mblock and mblock[k] is not None:
                out[k] = int(mblock[k])
        out["source"] = f"model:{model or '?'}"

    resolutions = (pblock.get("resolutions") or {}) if isinstance(pblock, dict) else {}
    rblock = resolutions.get(res) if res else None
    if isinstance(rblock, dict):
        for k in ("default", "min", "max", "prefer_min", "prefer_max"):
            if k in rblock and rblock[k] is not None:
                out[k] = int(rblock[k])
        out["source"] = f"{out['source']}+res:{res}"

    # Clamp consistency
    out["min"] = int(out.get("min", 1))
    out["max"] = max(out["min"], int(out.get("max", 15)))
    out["prefer_min"] = max(out["min"], int(out.get("prefer_min", out["min"])))
    out["prefer_max"] = min(out["max"], max(out["prefer_min"], int(out.get("prefer_max", out["max"]))))
    out["default"] = max(out["min"], min(out["max"], int(out.get("default", 8))))
    return out


def resolve_default_duration(
    config: Optional[Dict[str, Any]] = None,
    *,
    provider: Optional[str] = None,
    model_name: Optional[str] = None,
    resolution: Optional[str] = None,
) -> int:
    """
    Default clip length in seconds for generate/plan when no per-clip timestamp.

    Honors use_duration_defaults (default True). If False, uses flat duration_seconds.
    """
    cfg = config or DEFAULT_CONFIG
    if not cfg.get("use_duration_defaults", True):
        try:
            return max(1, int(cfg.get("duration_seconds") or 8))
        except (TypeError, ValueError):
            return 8
    profile = resolve_duration_profile(
        cfg, provider=provider, model_name=model_name, resolution=resolution
    )
    return int(profile["default"])

# Prompt cues that mean a real cut / new setup — do NOT seed from previous last frame
_HARD_CUT_RE = re.compile(
    r"\b("
    r"CUT\s+TO|JUMP\s+CUT|SMASH\s+CUT|MATCH\s+CUT|"
    r"FLASHBACK|FLASH\s*FORWARD|"
    r"WIDE\s+SHOT|ESTABLISHING|AERIAL|DRONE\s+SHOT|"
    r"EXT\.|EXTERIOR|INT\.|INTERIOR|"
    r"MEANWHILE|LATER|ELSEWHERE|NEW\s+LOCATION|"
    r"BACK\s+TO\s+PRESENT|RETURN\s+TO\s+PRESENT"
    r")\b",
    re.IGNORECASE,
)

# Big action beats rarely work as last-frame image-to-video — force fresh generation
_ACTION_BEAT_RE = re.compile(
    r"\b("
    r"kicks?|hits?|soars?|rockets?|flies|flight|crash(?:es|ed|ing)?|"
    r"explodes?|smashes?|throws?|punches?|jumps?|leaps?|sprints?|runs?\s+(?:at|toward|into)|"
    r"car\s+chase|gunshot|fires?\s+a\s+gun|tackles?"
    r")\b",
    re.IGNORECASE,
)


class GenerationFailure(Exception):
    """
    Raised whenever a paid model/API call (Suno music, Grok/Veo video, or the local
    FFmpeg mux/master step) fails or produces unusable output.
    """
    pass


class PipelineInterrupted(Exception):
    """Raised for graceful shutdown after Ctrl+C / SIGTERM (state is preserved)."""
    pass


def music_output_path(scene_number: int) -> str:
    return f"assets/music/scene_{scene_number:02d}_music.mp3"


def clip_output_path(scene_number: int, clip_number: int) -> str:
    return f"assets/video/scene_{scene_number:02d}_clip_{clip_number:02d}.mp4"


def composite_output_path(scene_number: int) -> str:
    return f"assets/scenes/scene_{scene_number:02d}_complete.mp4"


def slugify_model_id(provider: str, model_name: str) -> str:
    """Filesystem-safe variant id, e.g. grok__grok-imagine-video."""
    raw = f"{provider}__{model_name or 'default'}"
    cleaned = re.sub(r"[^a-zA-Z0-9._-]+", "-", raw.strip().lower())
    return cleaned.strip("-._")[:80] or "default"


def variant_dir(scene_number: int, variant_id: str) -> str:
    return f"assets/variants/scene_{int(scene_number):02d}/{variant_id}"


def variant_clip_path(scene_number: int, clip_number: int, variant_id: str) -> str:
    return f"{variant_dir(scene_number, variant_id)}/clip_{int(clip_number):02d}.mp4"


def variant_composite_path(scene_number: int, variant_id: str) -> str:
    return f"{variant_dir(scene_number, variant_id)}/composite.mp4"


def variant_meta_path(scene_number: int, variant_id: str) -> str:
    return f"{variant_dir(scene_number, variant_id)}/meta.json"


ASSET_DIRS = (
    "assets",
    "assets/characters",
    "assets/music",
    "assets/video",
    "assets/scenes",
    "assets/variants",
)


def ensure_parent_dir(path: str) -> None:
    """Create the parent directory of a file path if it does not exist."""
    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)


def file_is_usable(path: Optional[str], min_bytes: int = 1) -> bool:
    """True when path exists and is larger than min_bytes (not a failed empty download)."""
    try:
        return bool(path) and os.path.isfile(path) and os.path.getsize(path) >= min_bytes
    except OSError:
        return False


def _running_on_wsl() -> bool:
    """True when the Python process is inside WSL (Linux kernel Microsoft build)."""
    if os.name == "nt":
        return False
    try:
        with open("/proc/version", "r", encoding="utf-8", errors="ignore") as f:
            return "microsoft" in f.read().lower()
    except OSError:
        return False


def resolve_ffmpeg() -> str:
    """
    Return an ffmpeg executable path.

    Order:
      1. FFMPEG_PATH / FFMPEG env override
      2. PATH (works in WSL when `ffmpeg` is apt-installed)
      3. Common Linux/WSL absolute paths
      4. Common Windows install paths (native Windows only)
      5. imageio-ffmpeg bundled binary (same OS only)
    """
    import shutil
    import sys

    for env_key in ("FFMPEG_PATH", "FFMPEG"):
        env_path = (os.environ.get(env_key) or "").strip().strip('"')
        if env_path and os.path.isfile(env_path) and os.access(env_path, os.X_OK):
            return env_path
        # On Windows, X_OK is odd; also accept plain exists
        if env_path and os.path.isfile(env_path):
            return env_path

    found = shutil.which("ffmpeg")
    if found:
        return found

    is_windows = os.name == "nt"
    is_wsl = _running_on_wsl()

    linux_candidates = [
        "/usr/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
        "/bin/ffmpeg",
        os.path.expanduser("~/.local/bin/ffmpeg"),
    ]
    windows_candidates = [
        r"C:\ffmpeg\bin\ffmpeg.exe",
        r"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
        r"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
        os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe"),
        os.path.expandvars(r"%USERPROFILE%\scoop\shims\ffmpeg.exe"),
        os.path.expandvars(r"%ProgramData%\chocolatey\bin\ffmpeg.exe"),
    ]

    candidates = []
    if is_windows:
        candidates.extend(windows_candidates)
    else:
        # WSL / native Linux: never pick a Windows .exe
        candidates.extend(linux_candidates)

    for c in candidates:
        if c and os.path.isfile(c):
            return c

    # Bundled binary via optional dependency (pip install imageio-ffmpeg)
    try:
        import imageio_ffmpeg
        bundled = imageio_ffmpeg.get_ffmpeg_exe()
        if bundled and os.path.isfile(bundled):
            # Reject cross-OS binaries (e.g. .exe under WSL)
            if is_windows and not bundled.lower().endswith(".exe"):
                pass
            elif (not is_windows) and bundled.lower().endswith(".exe"):
                pass
            else:
                return bundled
    except Exception:
        pass

    return "ffmpeg"


def ffmpeg_is_available() -> Tuple[bool, str]:
    """
    True when resolve_ffmpeg() points at a real executable.
    The bare fallback name \"ffmpeg\" (nothing found) counts as missing.
    """
    path = resolve_ffmpeg()
    if not path:
        return False, ""
    if path != "ffmpeg" and os.path.isfile(path):
        return True, path
    import shutil

    found = shutil.which(path)
    if found and os.path.isfile(found):
        return True, found
    return False, path


def environment_needs(config: Optional[Dict[str, Any]] = None) -> Dict[str, bool]:
    """
    Which external deps the pipeline needs for this config.

    - XAI_API_KEY: Grok video, Grok character design, or Grok QA
    - GEMINI_API_KEY: Veo video, Gemini/Imagen characters, or Gemini QA
    - ffmpeg: always required for mux/composite/WIP (and most generate paths)
    """
    cfg = config or {}
    video = str(cfg.get("video_provider", "grok")).lower().strip()
    char = str(cfg.get("character_design_provider", "grok")).lower().strip()
    qa = str(cfg.get("qa_provider", "grok")).lower().strip()

    need_xai = (
        video in ("grok", "xai", "grok-imagine", "imagine")
        or char in ("grok", "xai", "imagine")
        or qa in ("grok", "xai")
    )
    need_gemini = (
        video in ("veo", "google", "gemini")
        or char in ("gemini", "imagen", "google")
        or qa in ("gemini", "google")
    )
    return {
        "xai": need_xai,
        "gemini": need_gemini,
        "ffmpeg": True,
    }


def check_environment(
    config: Optional[Dict[str, Any]] = None,
    *,
    require_xai: Optional[bool] = None,
    require_gemini: Optional[bool] = None,
    require_ffmpeg: Optional[bool] = None,
) -> List[str]:
    """
    Preflight env check. Returns a list of error messages (empty = OK).

    When require_* is None, infer from config providers (Grok → XAI, Veo/Gemini → GEMINI).
    """
    needs = environment_needs(config)
    want_xai = needs["xai"] if require_xai is None else require_xai
    want_gemini = needs["gemini"] if require_gemini is None else require_gemini
    want_ffmpeg = needs["ffmpeg"] if require_ffmpeg is None else require_ffmpeg

    errors: List[str] = []
    if want_xai and not (os.environ.get("XAI_API_KEY") or "").strip():
        errors.append(
            "XAI_API_KEY is not set. Required for Grok video, character design, and QA. "
            "Set it in the shell before starting Streamlit/CLI "
            '(e.g. $env:XAI_API_KEY = "…" on PowerShell).'
        )
    if want_gemini and not (os.environ.get("GEMINI_API_KEY") or "").strip():
        errors.append(
            "GEMINI_API_KEY is not set. Required when video_provider is Veo, "
            "or character_design_provider / qa_provider is Gemini."
        )
    if want_ffmpeg:
        ok, path = ffmpeg_is_available()
        if not ok:
            errors.append(
                "ffmpeg not found. Install ffmpeg and put it on PATH, "
                "or set FFMPEG_PATH to the full path of the executable "
                "(current resolve fallback was "
                f"'{path or 'missing'}')."
            )
    return errors


def require_environment(
    config: Optional[Dict[str, Any]] = None,
    *,
    require_xai: Optional[bool] = None,
    require_gemini: Optional[bool] = None,
    require_ffmpeg: Optional[bool] = None,
) -> None:
    """Raise GenerationFailure if any required env tool/key is missing."""
    errors = check_environment(
        config,
        require_xai=require_xai,
        require_gemini=require_gemini,
        require_ffmpeg=require_ffmpeg,
    )
    if errors:
        raise GenerationFailure(
            "Environment check failed:\n- " + "\n- ".join(errors)
        )


class AgenticGenerationEngine:
    def __init__(
        self,
        blueprint_path: Optional[str] = None,
        state_path: Optional[str] = None,
        config_path: Optional[str] = None,
        install_signals: bool = True,
        project_dir: Optional[str] = None,
    ):
        """
        project_dir: working directory for all relative asset/blueprint paths.
        If omitted, uses NICKANDME_PROJECT / FILM_PROJECT_DIR env, else cwd.
        """
        # Resolve project / work dir first so all relative paths are project-local
        self.project_dir = self._resolve_project_dir(project_dir)
        try:
            os.chdir(self.project_dir)
        except OSError as e:
            print(f"[Warning] Could not chdir to project_dir={self.project_dir}: {e}")

        self.state_path = state_path or STATE_FILE
        self.config_path = config_path or CONFIG_FILE
        
        self.blueprint: Dict[str, Any] = {}
        self.state: Dict[str, Any] = {}
        self.config: Dict[str, Any] = {}
        self.client = None
        self._shutdown_requested = False
        self._active_scene_num: Optional[int] = None
        self._active_clip_num: Optional[int] = None

        # Ensure asset tree exists before any I/O
        self.ensure_asset_directories()

        # Load configurations first (may set blueprint_file)
        self.load_config()

        # Blueprint path: explicit arg > config.blueprint_file > default Grok Stage 2 plan
        if blueprint_path:
            self.blueprint_path = blueprint_path
        else:
            self.blueprint_path = str(
                self.config.get("blueprint_file") or BLUEPRINT_FILE
            )

        # Log runtime tooling so WSL vs Windows path issues are obvious
        ffmpeg_ok, ffmpeg_path = ffmpeg_is_available()
        env_label = "WSL" if _running_on_wsl() else ("Windows" if os.name == "nt" else "Linux")
        print(
            f"[Runtime] host={env_label}, ffmpeg="
            f"{ffmpeg_path if ffmpeg_ok else f'MISSING (resolved={ffmpeg_path!r})'}"
        )
        print(f"[Runtime] project_dir={self.project_dir}")
        print(f"[Runtime] blueprint={self.blueprint_path}")
        # Soft status only here so GUI can still open for config/review.
        # Paid generate / Stage 0 / remux call require_environment() and raise.
        for msg in check_environment(self.config):
            print(f"[Environment] {msg}")

        # Optional Gemini client (QA/Imagen fallbacks when qa_provider/character_design_provider is gemini)
        if genai and os.environ.get("GEMINI_API_KEY"):
            try:
                self.client = genai.Client()
            except Exception as e:
                print(f"[Warning] Failed to initialize GenAI Client: {e}")

        self.load_blueprint()
        self.load_state()
        # Streamlit / embedded hosts own their process lifecycle — skip signal hooks
        if install_signals:
            self._install_signal_handlers()

    @staticmethod
    def _resolve_project_dir(project_dir: Optional[str] = None) -> str:
        if project_dir:
            return str(Path(project_dir).expanduser().resolve())
        env = os.environ.get("NICKANDME_PROJECT") or os.environ.get("FILM_PROJECT_DIR")
        if env:
            p = Path(env).expanduser()
            if not p.is_absolute():
                p = (Path.cwd() / p).resolve()
            if p.is_dir():
                return str(p)
        # projects/workspace.json at cwd or parents → projects/<active_project>
        here = Path.cwd().resolve()
        for base in (here, *here.parents):
            projects_dir = base / "projects"
            ws_cfg = projects_dir / "workspace.json"
            if not ws_cfg.is_file():
                # legacy: workspace.json at repo root
                legacy = base / "workspace.json"
                if legacy.is_file():
                    ws_cfg = legacy
                else:
                    continue
            try:
                data = json.loads(ws_cfg.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                break
            active = data.get("active_project")
            if active:
                proj = projects_dir / str(active)
                if proj.is_dir():
                    return str(proj.resolve())
            break
        return str(here)

    def _install_signal_handlers(self) -> None:
        """Ctrl+C / SIGTERM request a graceful stop (second signal forces exit)."""
        def _handler(signum, frame):
            sig_name = signal.Signals(signum).name if hasattr(signal, "Signals") else str(signum)
            if self._shutdown_requested:
                print(f"\n[Shutdown] Second {sig_name} — forcing exit now.")
                try:
                    self.save_state()
                except Exception:
                    pass
                os._exit(130)
            self._shutdown_requested = True
            print(
                f"\n[Shutdown] {sig_name} received — stopping after the current wait "
                f"(state will be saved; re-run to resume). Press Ctrl+C again to force quit."
            )

        for sig in (signal.SIGINT, signal.SIGTERM):
            try:
                signal.signal(sig, _handler)
            except (ValueError, OSError):
                # Signals can be restricted in some embedded hosts
                pass

    def _check_shutdown(self, where: str = "") -> None:
        """Raise PipelineInterrupted if the user asked to stop."""
        if self._shutdown_requested:
            loc = f" during {where}" if where else ""
            raise PipelineInterrupted(f"Interrupted by user{loc}")

    def _interruptible_sleep(self, seconds: float, where: str = "sleep") -> None:
        """Sleep in short slices so Ctrl+C is handled promptly."""
        end = time.time() + max(0.0, float(seconds))
        while time.time() < end:
            self._check_shutdown(where)
            time.sleep(min(0.5, end - time.time()))

    def graceful_stop(self, reason: str = "Interrupted by user") -> None:
        """Save state and exit cleanly (no traceback)."""
        print("\n" + "=" * 75)
        print(f"[Shutdown] {reason}")
        if self._active_scene_num is not None:
            print(
                f"[Shutdown] Active work: Scene {self._active_scene_num}"
                + (f" Clip {self._active_clip_num}" if self._active_clip_num is not None else "")
            )
        try:
            # Mark current scene incomplete so resume continues here
            if self._active_scene_num is not None:
                self.state.setdefault("scenes_completed", {})[str(self._active_scene_num)] = False
                assets = self.state.setdefault("scene_assets", {}).setdefault(str(self._active_scene_num), {})
                assets["partial"] = True
                assets["last_error"] = reason
            self.save_state()
            print(f"[Shutdown] Progress saved to '{self.state_path}'.")
        except Exception as e:
            print(f"[Shutdown Warning] Could not save state: {e}")
        print("[Shutdown] Re-run:  python -m cli")
        print("           Existing clips will be reused; incomplete jobs will resume.")
        print("=" * 75)
        raise SystemExit(130)

    def ensure_asset_directories(self) -> None:
        """Create all standard asset directories if missing."""
        for d in ASSET_DIRS:
            os.makedirs(d, exist_ok=True)

    def load_config(self):
        """Loads execution engine options from a distinct external configuration JSON."""
        if not os.path.exists(self.config_path):
            print(f"[Info] Config file not found. Generating default '{self.config_path}'...")
            self.config = dict(DEFAULT_CONFIG)
            try:
                with open(self.config_path, 'w') as f:
                    json.dump(self.config, f, indent=2)
            except Exception as e:
                print(f"[Warning] Failed to write default config file: {e}")
        else:
            try:
                with open(self.config_path, 'r') as f:
                    self.config = json.load(f)
                print(f"[Success] Config loaded dynamically from '{self.config_path}'")
            except Exception as e:
                print(f"[Warning] Failed to parse config file, using internal engine defaults: {e}")
                self.config = dict(DEFAULT_CONFIG)

        # Fill any missing keys from defaults so older config files still work
        for key, value in DEFAULT_CONFIG.items():
            if key in ("duration_defaults", "cost_estimates") and isinstance(value, dict):
                self.config.setdefault(key, {})
                if isinstance(self.config.get(key), dict):
                    _deep_setdefault(self.config[key], value)
                else:
                    self.config[key] = json.loads(json.dumps(value))
            else:
                self.config.setdefault(key, value)

        video_provider = str(self.config.get("video_provider", "grok")).lower()
        char_provider = str(self.config.get("character_design_provider", "grok")).lower()
        qa_provider = str(self.config.get("qa_provider", "grok")).lower()
        dur_profile = resolve_duration_profile(self.config)
        print(
            f"[Config] video_provider={video_provider}, "
            f"character_design_provider={char_provider}, "
            f"qa_provider={qa_provider}, "
            f"model_name={self.config.get('model_name')}, "
            f"image_model_name={self.config.get('image_model_name')}, "
            f"qa_model_name={self.config.get('qa_model_name')}, "
            f"duration_default={dur_profile.get('default')}s "
            f"[{dur_profile.get('source')}] "
            f"(prefer {dur_profile.get('prefer_min')}-{dur_profile.get('prefer_max')}, "
            f"max {dur_profile.get('max')})"
        )

    def load_blueprint(self):
        """Loads the master movie blueprint JSON payload (Stage 2 clip plan preferred)."""
        if not os.path.exists(self.blueprint_path):
            print(f"[Error] Movie blueprint not found at: {self.blueprint_path}")
            # Helpful fallback message for Stage 1 vs Stage 2 files
            if os.path.exists("nickandme.scenes.json") and "scenes.json" not in self.blueprint_path:
                print(
                    "[Hint] Found nickandme.scenes.json (Stage 1 only — no veo_clips). "
                    "Use nickandme.clips.grok.json (Stage 2) for generation."
                )
            sys.exit(1)
        try:
            with open(self.blueprint_path, 'r', encoding='utf-8') as f:
                self.blueprint = json.load(f)
            title = self.blueprint.get('movie_title', 'Untitled')
            schema = self.blueprint.get('schema_version') or self.blueprint.get('video_provider_profile') or ''
            n_scenes = len(self.blueprint.get('scenes') or [])
            n_clips = sum(
                len(s.get('veo_clips') or [])
                for s in (self.blueprint.get('scenes') or [])
            )
            print(
                f"[Success] Loaded blueprint '{title}' from '{self.blueprint_path}' "
                f"(schema={schema!r}, scenes={n_scenes}, clips={n_clips})"
            )
            if n_scenes and n_clips == 0:
                print(
                    "[Warning] Blueprint has scenes but zero veo_clips — "
                    "this looks like Stage 1 only. Run Stage 2 shot planner for Grok."
                )
        except Exception as e:
            print(f"[Error] Failed to parse blueprint JSON: {e}")
            sys.exit(1)

    def initialize_fresh_state(self):
        """Initializes empty/default pipeline state metadata."""
        self.state = {
            "characters_designed": False,
            "current_scene_index": 0,
            "scenes_completed": {},
            "scene_assets": {},
            "clip_context_ids": {},
            # Per-clip job metadata for resume after download/API failures
            "clip_jobs": {},
            "music_jobs": {},
            # Bumped when a character ref is re-locked; clips using old revs are "stale"
            "character_revisions": {},
            # "scene_clip" -> { characters, reason, marked_at, revision }
            "stale_clips": {},
            # scene_number -> { active, variants: { id: meta } }
            "scene_variants": {},
            # scene_number -> { resolution, at, clip_count, ... } after hero pass
            "scene_hero": {},
        }
        self.save_state()
        print("[Info] Initialized clean pipeline state cache.")

    def load_state(self):
        """Loads progress state or initializes a new pipeline state cache."""
        if os.path.exists(self.state_path):
            try:
                with open(self.state_path, 'r') as f:
                    self.state = json.load(f)
                # Backfill keys added for resume support
                self.state.setdefault("characters_designed", False)
                self.state.setdefault("current_scene_index", 0)
                self.state.setdefault("scenes_completed", {})
                self.state.setdefault("scene_assets", {})
                self.state.setdefault("clip_context_ids", {})
                self.state.setdefault("clip_jobs", {})
                self.state.setdefault("music_jobs", {})
                self.state.setdefault("character_revisions", {})
                self.state.setdefault("stale_clips", {})
                self.state.setdefault("scene_variants", {})
                self.state.setdefault("scene_hero", {})
                print(f"[Success] Resumed execution state from '{self.state_path}'")
            except Exception as e:
                print(f"[Warning] Failed to parse state file, starting fresh: {e}")
                self.initialize_fresh_state()
        else:
            self.initialize_fresh_state()

    def save_state(self):
        """Saves current state cache to disk with atomic safety."""
        ensure_parent_dir(self.state_path)
        temp_file = f"{self.state_path}.tmp"
        try:
            with open(temp_file, 'w') as f:
                json.dump(self.state, f, indent=2)
            os.replace(temp_file, self.state_path)
        except Exception as e:
            print(f"[Error] Failed to write state cache: {e}")

    def save_blueprint_to_disk(self):
        """Saves modifications made to the live blueprint in memory back to disk."""
        ensure_parent_dir(self.blueprint_path)
        temp_file = f"{self.blueprint_path}.tmp"
        try:
            with open(temp_file, 'w') as f:
                json.dump(self.blueprint, f, indent=2)
            os.replace(temp_file, self.blueprint_path)
            print(f"[Success] Persisted blueprint updates to '{self.blueprint_path}'")
        except Exception as e:
            print(f"[Error] Failed to write blueprint to disk: {e}")

    def _clip_job_key(self, scene_num: int, clip_num: int) -> str:
        return f"{scene_num}_{clip_num}"

    def _update_clip_job(
        self,
        scene_num: int,
        clip_num: int,
        job_key: Optional[str] = None,
        **fields: Any,
    ) -> None:
        key = (
            job_key
            or getattr(self, "_active_job_key", None)
            or self._clip_job_key(scene_num, clip_num)
        )
        job = self.state.setdefault("clip_jobs", {}).setdefault(key, {})
        job.update(fields)
        job["scene_number"] = scene_num
        job["clip_number"] = clip_num
        if key != self._clip_job_key(scene_num, clip_num):
            job["variant_job_key"] = key
        job["updated_at"] = time.strftime("%Y-%m-%dT%H:%M:%S")
        self.save_state()

    def _record_cost_event(self, event: Dict[str, Any], *, save: bool = True) -> Dict[str, Any]:
        """
        Append a billable generation to pipeline_state.cost_ledger.
        USD is list-rate pricing at event time (not an xAI console invoice line).
        """
        ledger = self.state.setdefault("cost_ledger", [])
        if not isinstance(ledger, list):
            ledger = []
            self.state["cost_ledger"] = ledger
        row = dict(event)
        row.setdefault("id", f"{int(time.time())}_{len(ledger):04d}")
        row.setdefault("ts", time.strftime("%Y-%m-%dT%H:%M:%S"))
        row.setdefault("currency", "USD")
        row.setdefault("source", "list_rate")
        ledger.append(row)
        # Cap growth (keep last 20k events)
        if len(ledger) > 20000:
            self.state["cost_ledger"] = ledger[-20000:]
        totals = self.state.setdefault("cost_totals", {})
        if not isinstance(totals, dict):
            totals = {}
            self.state["cost_totals"] = totals
        totals["usd"] = round(float(totals.get("usd") or 0) + float(row.get("usd") or 0), 4)
        totals["events"] = int(totals.get("events") or 0) + 1
        totals["updated_at"] = row["ts"]
        if save:
            self.save_state()
        return row

    def _import_cost_helpers(self):
        """Load list-rate pricing helpers (gui/review_app) with fallback path bootstrap."""
        try:
            from review_app.cost_estimate import (  # type: ignore
                compute_image_job_usd,
                compute_video_job_usd,
            )

            return compute_video_job_usd, compute_image_job_usd
        except ImportError:
            gui = Path(__file__).resolve().parent.parent / "gui"
            g = str(gui)
            if g not in sys.path:
                sys.path.insert(0, g)
            from review_app.cost_estimate import (  # type: ignore
                compute_image_job_usd,
                compute_video_job_usd,
            )

            return compute_video_job_usd, compute_image_job_usd

    def record_video_generation_cost(
        self,
        *,
        scene_num: int,
        clip_num: int,
        duration_sec: float,
        resolution: str,
        model: str,
        has_ref_image: bool,
        is_extend: bool,
        request_id: str = "",
        extra: Optional[Dict[str, Any]] = None,
    ) -> Dict[str, Any]:
        """Price and store one completed video job using cost_estimates list rates."""
        try:
            compute_video_job_usd, _ = self._import_cost_helpers()
            priced = compute_video_job_usd(
                self.config,
                duration_sec=duration_sec,
                resolution=resolution,
                has_ref_image=has_ref_image,
                is_extend=is_extend,
            )
        except Exception:
            res = str(resolution or self.config.get("resolution") or "720p").lower()
            table = (self.config.get("cost_estimates") or {}).get("video_output_per_sec") or {
                "480p": 0.05,
                "720p": 0.07,
                "1080p": 0.25,
            }
            out_rate = float(table.get(res, table.get("720p", 0.07)))
            duration = max(0.0, float(duration_sec))
            video_out = duration * out_rate
            ref = 0.002 if has_ref_image else 0.0
            ext = duration * 0.01 if is_extend else 0.0
            priced = {
                "duration_sec": duration,
                "resolution": res,
                "output_rate_per_sec": out_rate,
                "video_output_usd": round(video_out, 4),
                "ref_image_usd": round(ref, 4),
                "extend_input_usd": round(ext, 4),
                "usd": round(video_out + ref + ext, 4),
                "currency": "USD",
                "source": "list_rate",
            }
        event = {
            "kind": "video",
            "scene": int(scene_num),
            "clip": int(clip_num),
            "model": model,
            "request_id": request_id or "",
            "has_ref_image": bool(has_ref_image),
            "is_extend": bool(is_extend),
            **priced,
        }
        if extra:
            event["extra"] = extra
        row = self._record_cost_event(event)
        print(
            f"  [Cost] Tracked actual S{scene_num}C{clip_num}: "
            f"${float(row.get('usd') or 0):.4f} "
            f"({row.get('duration_sec')}s @ {row.get('resolution')}, list rate)"
        )
        return row

    def record_image_generation_cost(
        self,
        *,
        n_images: int,
        model: str,
        character: str = "",
        quality: bool = True,
    ) -> Dict[str, Any]:
        try:
            _, compute_image_job_usd = self._import_cost_helpers()
            priced = compute_image_job_usd(self.config, n_images=n_images, quality=quality)
        except Exception:
            unit = 0.05 if quality else 0.02
            priced = {
                "n_images": int(n_images),
                "unit_usd": unit,
                "usd": round(unit * max(0, int(n_images)), 4),
                "currency": "USD",
                "source": "list_rate",
            }
        event = {
            "kind": "image",
            "model": model,
            "character": character,
            **priced,
        }
        row = self._record_cost_event(event)
        print(
            f"  [Cost] Tracked actual image gen: ${float(row.get('usd') or 0):.4f} "
            f"({n_images} img, {character or model})"
        )
        return row

    def get_cost_ledger(self) -> List[Dict[str, Any]]:
        ledger = self.state.get("cost_ledger")
        return list(ledger) if isinstance(ledger, list) else []

    def backfill_cost_ledger_from_completed_jobs(self, *, only_missing: bool = True) -> Dict[str, Any]:
        """
        Infer ledger rows for completed video jobs that predate tracking.
        Uses current list rates × job/blueprint duration (source=backfill).
        """
        ledger = self.get_cost_ledger()
        seen = set()
        for e in ledger:
            if e.get("kind") != "video":
                continue
            try:
                seen.add((int(e.get("scene")), int(e.get("clip")), str(e.get("request_id") or "")))
                seen.add((int(e.get("scene")), int(e.get("clip")), ""))  # clip-level
            except (TypeError, ValueError):
                pass

        added = 0
        skipped = 0
        for scene in self.blueprint.get("scenes") or []:
            sn = int(scene.get("scene_number") or 0)
            for clip in scene.get("veo_clips") or []:
                cn = int(clip.get("clip_number") or 0)
                path = clip_output_path(sn, cn)
                if not file_is_usable(path, min_bytes=1024):
                    skipped += 1
                    continue
                job = (self.state.get("clip_jobs") or {}).get(self._clip_job_key(sn, cn)) or {}
                if only_missing and ((sn, cn, "") in seen or (sn, cn, str(job.get("request_id") or "")) in seen):
                    skipped += 1
                    continue
                # duration
                duration = float(resolve_default_duration(self.config))
                ts = str(clip.get("timestamp") or "")
                m_ts = re.match(r"^\s*(\d+):(\d{2})\s*-\s*(\d+):(\d{2})\s*$", ts)
                if m_ts:
                    a = int(m_ts.group(1)) * 60 + int(m_ts.group(2))
                    b = int(m_ts.group(3)) * 60 + int(m_ts.group(4))
                    if b > a:
                        duration = float(b - a)
                cont = str(clip.get("veo_continuation_source") or "none").lower()
                is_extend = cont == "extend_previous"
                has_ref = True  # typical locked-cast pipeline
                res = str(
                    job.get("resolution")
                    or (self.state.get("scene_hero") or {}).get(str(sn), {}).get("resolution")
                    or self.config.get("resolution")
                    or "720p"
                )
                model = str(job.get("model") or self.config.get("model_name") or "grok-imagine-video")
                try:
                    compute_video_job_usd, _ = self._import_cost_helpers()
                    priced = compute_video_job_usd(
                        self.config,
                        duration_sec=duration,
                        resolution=res,
                        has_ref_image=has_ref,
                        is_extend=is_extend,
                    )
                except Exception:
                    priced = {"usd": 0.0, "duration_sec": duration, "resolution": res}
                event = {
                    "kind": "video",
                    "scene": sn,
                    "clip": cn,
                    "model": model,
                    "request_id": job.get("request_id") or "",
                    "has_ref_image": has_ref,
                    "is_extend": is_extend,
                    "source": "backfill",
                    **{k: v for k, v in priced.items() if k != "source"},
                    "extra": {"backfill": True},
                }
                self._record_cost_event(event, save=False)
                seen.add((sn, cn, ""))
                added += 1
        self.save_state()
        return {"added": added, "skipped": skipped, "ledger_events": len(self.get_cost_ledger())}

    def _download_to_path(self, url: str, output_path: str, retries: int = 5,
                          timeout: int = 300, label: str = "asset") -> None:
        """
        Download a remote URL to disk with retries, parent-dir creation, and atomic rename.
        Partial/failed files are written to output_path.tmp then promoted on success.
        """
        if not url:
            raise GenerationFailure(f"Cannot download {label}: empty URL.")

        ensure_parent_dir(output_path)
        tmp_path = f"{output_path}.download.tmp"
        last_error: Optional[Exception] = None

        for attempt in range(1, retries + 1):
            try:
                if os.path.exists(tmp_path):
                    try:
                        os.remove(tmp_path)
                    except OSError:
                        pass

                print(f"  [Download] {label}: attempt {attempt}/{retries} -> {output_path}")
                req = urllib.request.Request(url, method="GET", headers={
                    "User-Agent": "NickAndMe-GenerationScript/8.9",
                })
                with urllib.request.urlopen(req, timeout=timeout) as response:
                    # Stream to disk so large videos do not require full memory buffer
                    with open(tmp_path, "wb") as out_f:
                        while True:
                            chunk = response.read(1024 * 1024)
                            if not chunk:
                                break
                            out_f.write(chunk)

                if not os.path.exists(tmp_path) or os.path.getsize(tmp_path) == 0:
                    raise GenerationFailure(f"Downloaded {label} is empty: {tmp_path}")

                os.replace(tmp_path, output_path)
                size_mb = os.path.getsize(output_path) / (1024 * 1024)
                print(f"  [Download] {label}: saved ({size_mb:.2f} MB)")
                return
            except GenerationFailure as e:
                last_error = e
            except Exception as e:
                last_error = e

            # Clean partial temp on failure
            try:
                if os.path.exists(tmp_path):
                    os.remove(tmp_path)
            except OSError:
                pass

            if attempt < retries:
                backoff = min(30, 2 ** attempt)
                print(f"  [Download] {label}: failed ({last_error}). Retrying in {backoff}s...")
                time.sleep(backoff)

        raise GenerationFailure(
            f"Failed to download {label} to '{output_path}' after {retries} attempts: {last_error}"
        )

    def _parse_grok_image_response(self, result: Dict[str, Any], *, n: int, label: str) -> List[bytes]:
        data = result.get("data")
        if not isinstance(data, list) or not data:
            raise GenerationFailure(f"Grok image API returned no image data ({label}): {result}")
        images: List[bytes] = []
        for item in data:
            if not isinstance(item, dict):
                continue
            if item.get("b64_json"):
                images.append(base64.b64decode(item["b64_json"]))
            elif item.get("url"):
                tmp_path = f"assets/characters/_grok_tmp_{len(images)}.img"
                self._download_to_path(item["url"], tmp_path, label=label)
                with open(tmp_path, "rb") as f:
                    images.append(f.read())
                try:
                    os.remove(tmp_path)
                except OSError:
                    pass
        if len(images) < 1:
            raise GenerationFailure(
                f"Grok image API returned 0 usable images ({label}): {result}"
            )
        return images[:n]

    def _grok_generate_image_variants(self, prompt: str, n: int = 3, aspect_ratio: str = "1:1") -> List[bytes]:
        """Generate n portrait variants via Grok Imagine image API; returns raw image bytes list."""
        model_name = self.config.get("image_model_name", "grok-imagine-image-quality")
        payload = {
            "model": model_name,
            "prompt": prompt,
            "n": n,
            "aspect_ratio": aspect_ratio,
            "response_format": "b64_json",
        }
        result = self._grok_request("POST", f"{XAI_API_BASE}/images/generations", payload, timeout=180)
        images = self._parse_grok_image_response(result, n=n, label="generations")
        if len(images) < n:
            raise GenerationFailure(
                f"Grok image API returned {len(images)}/{n} usable images: {result}"
            )
        try:
            quality = "quality" in str(model_name).lower()
            self.record_image_generation_cost(
                n_images=len(images[:n]),
                model=str(model_name),
                quality=quality,
            )
        except Exception as cost_err:
            print(f"  [Cost Warning] Could not record image cost: {cost_err}")
        return images[:n]

    def _grok_edit_image_variants(
        self,
        prompt: str,
        reference_paths: List[str],
        n: int = 3,
        aspect_ratio: str = "1:1",
    ) -> List[bytes]:
        """
        Generate variants guided by 1–3 book/reference images via /v1/images/edits.
        Uses data URIs so local book_images work without a public URL.
        """
        model_name = self.config.get("image_model_name", "grok-imagine-image-quality")
        refs = [p for p in reference_paths if p and os.path.isfile(p)][:3]
        if not refs:
            raise GenerationFailure("No usable reference images for character edit.")

        # Downscale huge book pages so the request stays reasonable
        image_payloads: List[Dict[str, str]] = []
        for path in refs:
            try:
                uri = self._file_to_data_uri_resized(path, max_edge=1280)
            except Exception:
                uri = self._file_to_data_uri(path)
            image_payloads.append({"url": uri, "type": "image_url"})

        images: List[bytes] = []
        # Edit API: request one image per call for reliable multi-variant output
        for i in range(n):
            variant_prompt = (
                f"{prompt} Variation {i + 1} of {n}: slight pose/expression change only; "
                f"keep the same identity, colors, markings, and illustration style as the reference."
            )
            payload: Dict[str, Any] = {
                "model": model_name,
                "prompt": variant_prompt,
                "response_format": "b64_json",
                "aspect_ratio": aspect_ratio,
            }
            if len(image_payloads) == 1:
                payload["image"] = image_payloads[0]
            else:
                payload["image"] = image_payloads
            result = self._grok_request(
                "POST", f"{XAI_API_BASE}/images/edits", payload, timeout=180
            )
            batch = self._parse_grok_image_response(
                result, n=1, label=f"edits variant {i + 1}"
            )
            images.extend(batch)
            try:
                quality = "quality" in str(model_name).lower()
                # edits bill input + output; record n_images for output count
                self.record_image_generation_cost(
                    n_images=len(batch),
                    model=str(model_name),
                    quality=quality,
                )
            except Exception as cost_err:
                print(f"  [Cost Warning] Could not record image edit cost: {cost_err}")

        if len(images) < 1:
            raise GenerationFailure("Grok image edit returned no variants.")
        return images[:n]

    def _file_to_data_uri_resized(self, path: str, max_edge: int = 1280) -> str:
        """Data URI for a local image, optionally downscaled for API size limits."""
        try:
            from PIL import Image
            import io

            im = Image.open(path)
            im = im.convert("RGB")
            w, h = im.size
            edge = max(w, h)
            if edge > max_edge:
                scale = max_edge / float(edge)
                im = im.resize(
                    (max(1, int(w * scale)), max(1, int(h * scale))),
                    Image.Resampling.LANCZOS,
                )
            buf = io.BytesIO()
            im.save(buf, format="JPEG", quality=88, optimize=True)
            b64 = base64.b64encode(buf.getvalue()).decode("ascii")
            return f"data:image/jpeg;base64,{b64}"
        except Exception:
            return self._file_to_data_uri(path)

    def find_character_book_references(
        self, char_key: str, seed_info: Optional[Dict[str, Any]] = None, *, max_refs: int = 3
    ) -> List[str]:
        """
        Locate book/page images to use as likeness references for character design.

        Order:
          1) seed design_reference_images / book_reference_images (relative paths)
          2) source/book_images/ named for this character
          3) cover + early embedded pages (picture books — dog/hero usually on cover)
        """
        seed_info = seed_info or self._character_seed(char_key) or {}
        out: List[str] = []
        seen = set()

        def _add(p: str) -> None:
            if not p:
                return
            path = p if os.path.isabs(p) else p.replace("\\", "/")
            if not os.path.isfile(path):
                # try under project cwd
                alt = os.path.join("source", path) if not path.startswith("source/") else path
                if os.path.isfile(alt):
                    path = alt
                else:
                    return
            ap = os.path.normpath(path)
            if ap in seen:
                return
            seen.add(ap)
            out.append(ap)

        for key in ("design_reference_images", "book_reference_images", "reference_images"):
            raw = seed_info.get(key)
            if isinstance(raw, str):
                _add(raw)
            elif isinstance(raw, list):
                for item in raw:
                    if isinstance(item, str):
                        _add(item)
                    elif isinstance(item, dict) and item.get("path"):
                        _add(str(item["path"]))

        book_dir = Path("source/book_images")
        if book_dir.is_dir():
            token = char_key.replace("Character_", "").lower()
            names = [token, token.replace("_", "")]
            if seed_info.get("canonical_given_name"):
                names.append(str(seed_info["canonical_given_name"]).lower())
            # Prefer files whose names mention the character
            try:
                for f in sorted(book_dir.iterdir()):
                    if not f.is_file():
                        continue
                    low = f.name.lower()
                    if any(n and n in low for n in names):
                        _add(str(f))
            except OSError:
                pass

            # Picture-book fallback: cover + pages that often show the hero (1–3, 5)
            if len(out) < max_refs:
                for name in (
                    "page_001_cover.png",
                    "embedded_p001_x12.jpg",
                    "embedded_p002_x20.jpg",
                    "embedded_p003_x25.jpg",
                    "embedded_p005_x37.jpg",
                    "page_002_sampled_page.png",
                ):
                    _add(str(book_dir / name))
                    if len(out) >= max_refs:
                        break

            # Any remaining embedded pages if still short
            if len(out) < 1:
                try:
                    for f in sorted(book_dir.glob("embedded_p*.jpg"))[:max_refs]:
                        _add(str(f))
                    for f in sorted(book_dir.glob("page_*.png"))[:max_refs]:
                        _add(str(f))
                except OSError:
                    pass

        return out[:max_refs]

    def _imagen_generate_image_variants(self, prompt: str, n: int = 3) -> List[bytes]:
        """Optional Imagen fallback for character portraits (requires Gemini client)."""
        if not self.client or types is None:
            raise GenerationFailure("Imagen character design requires Gemini GenAI client (GEMINI_API_KEY).")

        images: List[bytes] = []
        for _ in range(n):
            result = self.client.models.generate_images(
                model="imagen-3.0-generate-002",
                prompt=prompt,
                config=types.GenerateImagesConfig(
                    numberOfImages=1,
                    outputMimeType="image/png",
                    aspectRatio="1:1",
                ),
            )
            for generated_img in result.generated_images:
                images.append(generated_img.image.image_bytes)
        if len(images) < n:
            raise GenerationFailure(f"Imagen returned {len(images)}/{n} character variants.")
        return images[:n]

    def pre_production_character_design(self):
        """
        STAGE 0: Interactively generate 3 portrait options per character seed
        (adults AND age variants like Character_N_Young / Character_P_Teen).

        Already-locked reference files are skipped so re-runs only design missing seeds.
        """
        print("\n==================== STAGE 0: PRE-PRODUCTION CHARACTER DESIGN GATE ====================")
        self.ensure_asset_directories()
        os.makedirs("assets/characters", exist_ok=True)

        char_seeds = self.blueprint.get("global_production_variables", {}).get("character_seed_tokens", {})
        if not char_seeds:
            print("[Info] No upfront character seed tokens found in blueprint. Skipping Phase 0.")
            return

        char_provider = str(self.config.get("character_design_provider", "grok")).lower().strip()
        if char_provider in ("grok", "xai", "imagine"):
            require_environment(self.config, require_xai=True, require_gemini=False, require_ffmpeg=False)
            design_label = "Grok Imagine"
        elif char_provider in ("imagen", "gemini", "google"):
            require_environment(self.config, require_xai=False, require_gemini=True, require_ffmpeg=False)
            if not self.client:
                raise GenerationFailure(
                    "Gemini/Imagen client not configured (GEMINI_API_KEY missing or GenAI SDK failed to init)."
                )
            design_label = "Imagen 3"
        else:
            raise GenerationFailure(
                f"Unknown character_design_provider '{char_provider}'. "
                "Use 'grok' or 'gemini'."
            )

        # Adults first, then age variants (Young/Teen), so family resemblance notes can reference adults
        def _seed_sort_key(item):
            key = item[0]
            if key.endswith("_Young"):
                return (1, key)
            if key.endswith("_Teen"):
                return (2, key)
            if key in ("Kevin McCleary", "Bob"):
                return (3, key)
            return (0, key)

        missing = []
        for char_key, seed_info in sorted(char_seeds.items(), key=_seed_sort_key):
            local_image_name = seed_info.get("reference_image_placeholder", f"{char_key.lower()}_ref.png")
            final_path = f"assets/characters/{local_image_name}"
            if not os.path.exists(final_path):
                missing.append(char_key)
        if missing:
            print(f"[Stage 0] Need portraits for: {', '.join(missing)}")
        else:
            print("[Stage 0] All character reference files already locked (including age variants).")

        for char_key, seed_info in sorted(char_seeds.items(), key=_seed_sort_key):
            local_image_name = seed_info.get("reference_image_placeholder", f"{char_key.lower()}_ref.png")
            final_path = f"assets/characters/{local_image_name}"

            # Check if this character has already been locked in from a previous execution
            if os.path.exists(final_path):
                print(f"[Anchor Locked] Character reference asset already locked at: {final_path}")
                continue

            satisfied = False
            while not satisfied:
                print(f"\n[Designing] Launching {design_label} variants for {char_key}...")
                description = seed_info.get("description", "")
                age_band = seed_info.get("age_band") or ""
                variant_of = seed_info.get("variant_of") or ""

                # Formulate structural design template prompt matching film parameters
                treatment = self.blueprint.get("global_production_variables", {}).get(
                    "directorial_treatment", "cinematic lighting"
                )
                age_clause = ""
                if age_band.startswith("child") or char_key.endswith("_Young"):
                    age_clause = (
                        "CRITICAL: this is a CHILD portrait with child proportions, smaller head-to-body ratio, "
                        "youthful face — NOT an adult, NOT a bodybuilder, NOT aged-up. "
                    )
                elif age_band.startswith("teen") or char_key.endswith("_Teen"):
                    age_clause = (
                        "CRITICAL: this is a TEEN / late-teen portrait — younger than the adult version, "
                        "not a middle-aged adult. "
                    )
                family_clause = ""
                if variant_of:
                    family_clause = (
                        f"Should clearly read as a younger version of {variant_of} "
                        f"(same ethnicity, hair color family, recognizable family features). "
                    )
                design_prompt = (
                    f"A detailed portrait model-sheet photograph of {char_key}: {description}. "
                    f"{age_clause}{family_clause}"
                    f"Character centered in frame, look straight at camera, neutral expression, {treatment}. "
                    f"High texture realism, isolated plain dark concrete studio background."
                )

                option_paths: List[str] = []
                try:
                    if char_provider in ("grok", "xai", "imagine"):
                        print("  Generating 3 option variants in one Grok batch...")
                        image_blobs = self._grok_generate_image_variants(design_prompt, n=3, aspect_ratio="1:1")
                    else:
                        print("  Generating 3 option variants via Imagen...")
                        image_blobs = self._imagen_generate_image_variants(design_prompt, n=3)

                    for idx, blob in enumerate(image_blobs, start=1):
                        opt_path = f"assets/characters/{char_key.lower()}_variant_0{idx}.png"
                        ensure_parent_dir(opt_path)
                        with open(opt_path, "wb") as f:
                            f.write(blob)
                        option_paths.append(opt_path)
                        print(f"  Saved Option variant {idx}/3 -> {opt_path}")
                except Exception as e:
                    print(f"  [Error] Variant generation failed: {e}")
                    for p in option_paths:
                        if os.path.exists(p):
                            os.remove(p)
                    print("[Error] Retrying entire batch...")
                    continue

                if len(option_paths) < 3:
                    print("[Error] Failed to generate all 3 variants. Retrying entire batch...")
                    for p in option_paths:
                        if os.path.exists(p):
                            os.remove(p)
                    continue

                print(f"\n*** INTERACTIVE SELECTION FOR {char_key} ***")
                print(f"Option 1 saved to: {option_paths[0]}")
                print(f"Option 2 saved to: {option_paths[1]}")
                print(f"Option 3 saved to: {option_paths[2]}")

                user_choice = ""
                while user_choice not in ["1", "2", "3", "R"]:
                    user_choice = input(
                        "Please inspect the character images. "
                        "Select [1], [2], [3] to Lock character look, or [R] to Regenerate fresh options: "
                    ).strip().upper()

                if user_choice in ["1", "2", "3"]:
                    selected_index = int(user_choice) - 1
                    chosen_variant_path = option_paths[selected_index]

                    # Promote chosen temporary variant image file to the master locked anchor file path
                    os.replace(chosen_variant_path, final_path)
                    print(f"[Success] Locked {char_key} design choice! Saved to master reference slot: {final_path}")

                    # Clean up other trailing variants to keep directories clean
                    for p in option_paths:
                        if os.path.exists(p):
                            os.remove(p)
                    satisfied = True
                elif user_choice == "R":
                    print(f"Flushing variant cache and rerolling seed space layout for {char_key}...")
                    for p in option_paths:
                        if os.path.exists(p):
                            os.remove(p)

        self.state["characters_designed"] = True
        self.save_state()

    # ------------------------------------------------------------------
    # Non-interactive character helpers (Streamlit / GUI)
    # ------------------------------------------------------------------

    def _character_seed(self, char_key: str) -> Optional[Dict[str, Any]]:
        seeds = self.blueprint.get("global_production_variables", {}).get("character_seed_tokens", {})
        return seeds.get(char_key)

    def character_ref_path(self, char_key: str) -> str:
        seed = self._character_seed(char_key) or {}
        name = seed.get("reference_image_placeholder", f"{char_key.lower()}_ref.png")
        return f"assets/characters/{name}"

    def character_variant_paths(self, char_key: str) -> List[str]:
        return [
            f"assets/characters/{char_key.lower()}_variant_0{idx}.png"
            for idx in (1, 2, 3)
        ]

    def _character_design_prompt(
        self,
        char_key: str,
        seed_info: Dict[str, Any],
        *,
        has_book_refs: bool = False,
    ) -> str:
        description = seed_info.get("description", "")
        age_band = seed_info.get("age_band") or ""
        variant_of = seed_info.get("variant_of") or ""
        display = (
            seed_info.get("canonical_given_name")
            or seed_info.get("voice_label")
            or char_key.replace("Character_", "").replace("_", " ")
        )
        treatment = self.blueprint.get("global_production_variables", {}).get(
            "directorial_treatment", "cinematic lighting"
        )
        age_clause = ""
        if age_band.startswith("child") or char_key.endswith("_Young"):
            age_clause = (
                "CRITICAL: this is a CHILD portrait with child proportions, smaller head-to-body ratio, "
                "youthful face — NOT an adult, NOT a bodybuilder, NOT aged-up. "
            )
        elif age_band.startswith("teen") or char_key.endswith("_Teen"):
            age_clause = (
                "CRITICAL: this is a TEEN / late-teen portrait — younger than the adult version, "
                "not a middle-aged adult. "
            )
        elif "dog" in age_band.lower() or "dog" in description.lower():
            age_clause = (
                "CRITICAL: this is a DOG portrait (animal), not a human. "
                "Match breed look, ear shape, coat color/markings from the description. "
            )
        family_clause = ""
        if variant_of:
            family_clause = (
                f"Should clearly read as a younger version of {variant_of} "
                f"(same ethnicity, hair color family, recognizable family features). "
            )
        if has_book_refs:
            return (
                f"Create a clean character model-sheet portrait of {display} for film continuity. "
                f"MATCH the character identity, colors, markings, and children's-book illustration style "
                f"from the reference image(s) as closely as possible — same dog/person as in the book art. "
                f"Do NOT invent a different breed, palette, or realistic photo style unless the reference is photo. "
                f"Description: {description}. {age_clause}{family_clause}"
                f"Character centered, facing camera, plain soft studio or simple background, "
                f"full head and upper body clear for video reference. "
                f"Keep the whimsical picture-book look of the source art; {treatment}."
            )
        return (
            f"A detailed portrait model-sheet of {display}: {description}. "
            f"{age_clause}{family_clause}"
            f"Character centered in frame, look straight at camera, neutral expression, {treatment}. "
            f"If this is a children's picture-book character, use illustrated storybook style "
            f"(not a photorealistic stock photo). Isolated plain soft background."
        )

    def generate_character_variants(
        self, char_key: str, *, allow_text_fallback: bool = False
    ) -> List[str]:
        """
        Generate 3 portrait variants for a character (no interactive prompt).
        Uses book page images as Grok edit references when available so likeness
        matches the source art (critical for picture books like Buster).

        Returns list of saved variant paths. Raises GenerationFailure on failure.
        Sets self._last_character_gen_meta with mode/refs for the GUI.
        """
        seed_info = self._character_seed(char_key)
        if not seed_info:
            raise GenerationFailure(f"Unknown character seed: {char_key}")

        self.ensure_asset_directories()
        os.makedirs("assets/characters", exist_ok=True)
        book_refs = self.find_character_book_references(char_key, seed_info, max_refs=3)
        design_prompt = self._character_design_prompt(
            char_key, seed_info, has_book_refs=bool(book_refs)
        )
        char_provider = str(self.config.get("character_design_provider", "grok")).lower().strip()
        mode = "text_only"
        edit_error: Optional[str] = None

        if char_provider in ("grok", "xai", "imagine"):
            if not os.environ.get("XAI_API_KEY"):
                raise GenerationFailure("XAI_API_KEY not set — cannot generate character portraits.")
            if book_refs:
                print(
                    f"  [Character design] Using {len(book_refs)} book reference(s) for {char_key}: "
                    + ", ".join(os.path.basename(p) for p in book_refs)
                )
                try:
                    # Prefer single best ref first (edits are more faithful than multi full-page noise)
                    primary = book_refs[:1]
                    image_blobs = self._grok_edit_image_variants(
                        design_prompt, primary, n=3, aspect_ratio="1:1"
                    )
                    mode = "book_edit"
                except Exception as e:
                    edit_error = str(e)
                    print(f"  [Character design] Book-ref edit failed ({e}).")
                    # Retry with up to 3 refs once
                    try:
                        image_blobs = self._grok_edit_image_variants(
                            design_prompt, book_refs, n=3, aspect_ratio="1:1"
                        )
                        mode = "book_edit_multi"
                        edit_error = None
                    except Exception as e2:
                        edit_error = f"{edit_error}; multi={e2}"
                        if not allow_text_fallback:
                            raise GenerationFailure(
                                f"Book-reference character design failed for {char_key}. "
                                f"References: {[os.path.basename(p) for p in book_refs]}. "
                                f"API error: {edit_error}. "
                                "Fix API/key or re-extract book images; do NOT re-run Stage 1. "
                                "Text-only fallback is disabled so we do not invent a different look."
                            ) from e2
                        print(
                            "  [Character design] Falling back to text-only "
                            "(likeness will NOT match the book)."
                        )
                        image_blobs = self._grok_generate_image_variants(
                            design_prompt, n=3, aspect_ratio="1:1"
                        )
                        mode = "text_fallback"
            else:
                print(
                    f"  [Character design] No book images found for {char_key} — text-only prompt "
                    f"(variants may not match source art). Put pages in source/book_images/."
                )
                image_blobs = self._grok_generate_image_variants(
                    design_prompt, n=3, aspect_ratio="1:1"
                )
                mode = "text_only"
        elif char_provider in ("imagen", "gemini", "google"):
            if not self.client:
                raise GenerationFailure("Gemini/Imagen client not configured.")
            image_blobs = self._imagen_generate_image_variants(design_prompt, n=3)
            mode = "imagen"
        else:
            raise GenerationFailure(f"Unknown character_design_provider '{char_provider}'")

        option_paths: List[str] = []
        for idx, blob in enumerate(image_blobs, start=1):
            opt_path = f"assets/characters/{char_key.lower()}_variant_0{idx}.png"
            ensure_parent_dir(opt_path)
            with open(opt_path, "wb") as f:
                f.write(blob)
            option_paths.append(opt_path)
        if len(option_paths) < 1:
            raise GenerationFailure(f"No variants generated for {char_key}")

        self._last_character_gen_meta = {
            "char_key": char_key,
            "mode": mode,
            "book_refs": book_refs,
            "paths": option_paths,
            "edit_error": edit_error,
        }
        return option_paths

    def unlock_character_ref(self, char_key: str) -> bool:
        """Remove locked reference so a new design can be chosen. Returns True if a file was removed."""
        path = self.character_ref_path(char_key)
        removed = False
        if os.path.isfile(path):
            os.remove(path)
            removed = True
        for vp in self.character_variant_paths(char_key):
            if os.path.isfile(vp):
                try:
                    os.remove(vp)
                except OSError:
                    pass
        # Unlock alone does not bump revision (no new look yet); lock will mark stale.
        return removed

    def lock_character_from_path(self, char_key: str, source_path: str) -> str:
        """
        Copy any image (book plate, variant, etc.) to the locked character ref path.
        Keeps the source file. Bumps character revision for cascade.
        """
        import shutil

        seed_info = self._character_seed(char_key)
        if not seed_info:
            raise GenerationFailure(f"Unknown character seed: {char_key}")
        src = source_path
        if not os.path.isfile(src):
            # try project-relative
            if os.path.isfile(os.path.normpath(source_path)):
                src = os.path.normpath(source_path)
            else:
                raise GenerationFailure(f"Image not found: {source_path}")
        final_path = self.character_ref_path(char_key)
        # Normalize locked ref to .png name from seed, but allow any source format
        ensure_parent_dir(final_path)
        # If source is not png and final is .png, convert via PIL when needed
        try:
            src_l = src.lower()
            final_l = final_path.lower()
            if src_l.endswith((".jpg", ".jpeg", ".webp")) and final_l.endswith(".png"):
                from PIL import Image

                im = Image.open(src)
                if im.mode not in ("RGB", "RGBA"):
                    im = im.convert("RGBA")
                im.save(final_path, format="PNG", optimize=True)
            else:
                shutil.copy2(src, final_path)
        except Exception:
            shutil.copy2(src, final_path)
        # Clear open variants so UI shows the lock clearly
        for p in self.character_variant_paths(char_key):
            if os.path.isfile(p):
                try:
                    os.remove(p)
                except OSError:
                    pass
        self.state["characters_designed"] = True
        self.mark_character_changed(
            char_key,
            reason=f"Locked reference from {os.path.basename(src)}",
            only_existing=True,
        )
        self.save_state()
        return final_path

    def lock_character_variant(self, char_key: str, variant_index: int) -> str:
        """
        Promote variant 1..3 to the locked reference path. Returns final ref path.
        Bumps character revision and marks on-disk clips that use this character as stale.
        """
        if variant_index not in (1, 2, 3):
            raise ValueError("variant_index must be 1, 2, or 3")
        seed_info = self._character_seed(char_key)
        if not seed_info:
            raise GenerationFailure(f"Unknown character seed: {char_key}")
        variant_path = f"assets/characters/{char_key.lower()}_variant_0{variant_index}.png"
        if not os.path.isfile(variant_path):
            raise GenerationFailure(f"Variant not found: {variant_path}")
        return self.lock_character_from_path(char_key, variant_path)

    def clips_using_character(self, char_key: str) -> List[Tuple[int, int]]:
        """Return (scene_number, clip_number) for every visual_prompt that mentions char_key."""
        hits: List[Tuple[int, int]] = []
        for scene in self.blueprint.get("scenes", []):
            sn = scene.get("scene_number")
            for clip in scene.get("veo_clips") or []:
                vp = clip.get("visual_prompt") or ""
                if char_key in vp:
                    hits.append((int(sn), int(clip.get("clip_number", 0))))
        return hits

    # ------------------------------------------------------------------
    # Character revision / stale clip tracking
    # ------------------------------------------------------------------

    def get_character_revision(self, char_key: str) -> int:
        revs = self.state.setdefault("character_revisions", {})
        entry = revs.get(char_key) or {}
        return int(entry.get("revision", 0))

    def mark_character_changed(
        self,
        char_key: str,
        reason: str = "",
        only_existing: bool = True,
    ) -> List[Tuple[int, int]]:
        """
        Bump character design revision and mark generated clips that use this
        character as out-of-date until they are regenerated.
        Returns list of (scene, clip) marked stale.
        """
        revs = self.state.setdefault("character_revisions", {})
        prev = int((revs.get(char_key) or {}).get("revision", 0))
        new_rev = prev + 1
        revs[char_key] = {
            "revision": new_rev,
            "updated_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
            "reason": reason or "character design changed",
        }

        stale = self.state.setdefault("stale_clips", {})
        marked: List[Tuple[int, int]] = []
        for sn, cn in self.clips_using_character(char_key):
            path = clip_output_path(sn, cn)
            if only_existing and not file_is_usable(path, min_bytes=1024):
                continue
            key = self._clip_job_key(sn, cn)
            entry = stale.get(key) or {
                "scene_number": sn,
                "clip_number": cn,
                "characters": [],
                "reasons": [],
            }
            chars = list(entry.get("characters") or [])
            if char_key not in chars:
                chars.append(char_key)
            reasons = list(entry.get("reasons") or [])
            note = reason or f"{char_key} revision {new_rev}"
            if note not in reasons:
                reasons.append(note)
            entry.update(
                {
                    "scene_number": sn,
                    "clip_number": cn,
                    "characters": chars,
                    "reasons": reasons,
                    "character_revision": {**(entry.get("character_revision") or {}), char_key: new_rev},
                    "marked_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
                    "stale": True,
                }
            )
            stale[key] = entry
            # Also flag clip job so CLI reuse / UI see it
            self._update_clip_job(
                sn,
                cn,
                stale=True,
                stale_reason=note,
                stale_characters=chars,
                qa_approved=False,
                review_status="stale",
            )
            # Scene no longer fully approved
            self.state.setdefault("scenes_completed", {})[str(sn)] = False
            marked.append((sn, cn))

        self.save_state()
        print(
            f"  [Stale] {char_key} → rev {new_rev}; marked {len(marked)} clip(s) out of date."
        )
        return marked

    def clear_clip_stale(self, scene_num: int, clip_num: int) -> None:
        """Clear stale flag after a successful regen of this clip."""
        key = self._clip_job_key(scene_num, clip_num)
        stale = self.state.setdefault("stale_clips", {})
        stale.pop(key, None)
        # Record which character revisions this render used
        char_revs = {}
        for scene in self.blueprint.get("scenes", []):
            if scene.get("scene_number") != scene_num:
                continue
            for clip in scene.get("veo_clips") or []:
                if clip.get("clip_number") != clip_num:
                    continue
                vp = clip.get("visual_prompt") or ""
                for ck in (
                    self.blueprint.get("global_production_variables", {}).get(
                        "character_seed_tokens", {}
                    )
                    or {}
                ).keys():
                    if ck in vp:
                        char_revs[ck] = self.get_character_revision(ck)
        self._update_clip_job(
            scene_num,
            clip_num,
            stale=False,
            stale_reason="",
            stale_characters=[],
            character_revisions_used=char_revs,
            review_status="pending",
        )
        self.save_state()

    def is_clip_stale(self, scene_num: int, clip_num: int) -> bool:
        key = self._clip_job_key(scene_num, clip_num)
        entry = (self.state.get("stale_clips") or {}).get(key)
        if entry and entry.get("stale", True):
            return True
        job = (self.state.get("clip_jobs") or {}).get(key) or {}
        return bool(job.get("stale"))

    def get_stale_clip_info(self, scene_num: int, clip_num: int) -> Optional[Dict[str, Any]]:
        key = self._clip_job_key(scene_num, clip_num)
        return (self.state.get("stale_clips") or {}).get(key)

    def list_stale_clips(
        self, only_existing: bool = True
    ) -> List[Dict[str, Any]]:
        out: List[Dict[str, Any]] = []
        for key, entry in (self.state.get("stale_clips") or {}).items():
            sn = int(entry.get("scene_number") or key.split("_")[0])
            cn = int(entry.get("clip_number") or key.split("_")[-1])
            path = clip_output_path(sn, cn)
            on_disk = file_is_usable(path, min_bytes=1024)
            if only_existing and not on_disk:
                continue
            out.append(
                {
                    "scene": sn,
                    "clip": cn,
                    "label": f"S{sn}C{cn}",
                    "characters": entry.get("characters") or [],
                    "reasons": entry.get("reasons") or [],
                    "marked_at": entry.get("marked_at"),
                    "on_disk": on_disk,
                    "path": path,
                }
            )
        out.sort(key=lambda r: (r["scene"], r["clip"]))
        return out

    def append_visual_prompt_feedback(
        self,
        scene_num: int,
        clip_num: int,
        feedback: str,
        max_len: Optional[int] = None,
    ) -> Tuple[str, str]:
        """
        Append reviewer feedback into a clip's visual_prompt (before the tech suffix).
        Returns (old_prompt, new_prompt).
        """
        feedback = (feedback or "").strip()
        if not feedback:
            raise ValueError("feedback is empty")
        if max_len is None:
            provider = str(self.config.get("video_provider", "grok")).lower()
            max_len = 400 if provider == "veo" else 800

        for scene in self.blueprint.get("scenes", []):
            if scene.get("scene_number") != scene_num:
                continue
            for clip in scene.get("veo_clips") or []:
                if clip.get("clip_number") != clip_num:
                    continue
                old = clip.get("visual_prompt") or ""
                suffix = " / 720p, 24fps"
                base = old
                if base.endswith(suffix):
                    base = base[: -len(suffix)].rstrip()
                elif " / 720p" in base:
                    base = re.sub(r"\s*/\s*720p.*$", "", base).rstrip()
                # Avoid double-appending the same note
                if feedback.lower() in base.lower():
                    return old, old
                candidate = f"{base}, {feedback}{suffix}"
                if len(candidate) > max_len:
                    # Keep end of base short enough for feedback
                    budget = max_len - len(suffix) - len(feedback) - 2
                    if budget < 40:
                        raise GenerationFailure(
                            f"Cannot fit feedback into visual_prompt (max {max_len} chars)."
                        )
                    base = base[:budget].rstrip(" ,;")
                    candidate = f"{base}, {feedback}{suffix}"
                clip["visual_prompt"] = candidate
                self.save_blueprint_to_disk()
                return old, candidate
        raise GenerationFailure(f"Scene {scene_num} clip {clip_num} not found in blueprint.")

    def regenerate_clip(
        self,
        scene_num: int,
        clip_num: int,
        feedback: Optional[str] = None,
        run_qa: bool = True,
    ) -> str:
        """
        Wipe one clip and force-generate it. Optionally append feedback to visual_prompt first.
        Returns path to new video.
        """
        if feedback:
            self.append_visual_prompt_feedback(scene_num, clip_num, feedback)

        scene = None
        clip = None
        for s in self.blueprint.get("scenes", []):
            if s.get("scene_number") == scene_num:
                scene = s
                for c in s.get("veo_clips") or []:
                    if c.get("clip_number") == clip_num:
                        clip = c
                        break
                break
        if not scene or not clip:
            raise GenerationFailure(f"Scene {scene_num} clip {clip_num} not found.")

        self._active_scene_num = scene_num
        self._active_clip_num = clip_num
        self._clear_clip_assets(scene_num, clip_num)

        seed_ctx = None
        if clip_num > 1:
            prev = clip_output_path(scene_num, clip_num - 1)
            if file_is_usable(prev, min_bytes=1024):
                seed_ctx = prev

        path, _ctx = self.generate_video_clip(
            scene_num, clip, seed_ctx, force_regenerate=True
        )
        if run_qa and path:
            qa_ok = self.run_clip_qa(path, clip.get("visual_prompt") or "")
            self._update_clip_job(
                scene_num,
                clip_num,
                path=path,
                qa_approved=bool(qa_ok),
                status="complete" if qa_ok else "qa_rejected",
            )
        # Fresh render matches current character refs
        if path:
            self.clear_clip_stale(scene_num, clip_num)
        self.state.setdefault("scenes_completed", {})[str(scene_num)] = False
        # Single-clip regen outside a hero batch returns scene to draft
        if not getattr(self, "_hero_regen_active", False):
            self.clear_scene_hero(scene_num)
        self.save_state()
        return path

    def remux_scene_from_disk(self, scene_num: int) -> Optional[str]:
        """Rebuild scene composite from all existing clip files on disk."""
        require_environment(
            self.config, require_xai=False, require_gemini=False, require_ffmpeg=True
        )
        scene = None
        for s in self.blueprint.get("scenes", []):
            if s.get("scene_number") == scene_num:
                scene = s
                break
        if not scene:
            return None
        paths: List[str] = []
        for clip in scene.get("veo_clips") or []:
            p = clip_output_path(scene_num, int(clip.get("clip_number", 0)))
            if file_is_usable(p, min_bytes=1024):
                paths.append(p)
        if not paths:
            return None
        music = music_output_path(scene_num)
        music_path = music if file_is_usable(music, min_bytes=64) else None
        if self.config.get("use_video_audio_for_music", False):
            music_path = None
        return self.mix_scene_assets(scene_num, paths, music_path, force=True)

    def set_clip_review_status(
        self, scene_num: int, clip_num: int, status: str, note: str = ""
    ) -> None:
        """status: pass | fail | pending"""
        fields: Dict[str, Any] = {
            "review_status": status,
            "review_note": note or "",
            "reviewed_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        }
        if status == "pass":
            fields["qa_approved"] = True
        elif status == "fail":
            fields["qa_approved"] = False
        self._update_clip_job(scene_num, clip_num, **fields)

    def approve_scene(self, scene_num: int) -> None:
        self.state.setdefault("scenes_completed", {})[str(scene_num)] = True
        self.save_state()
        try:
            self.remux_scene_from_disk(scene_num)
        except Exception as e:
            print(f"  [Approve Warning] Remux failed: {e}")
        self.rebuild_wip_movie(reason=f"after approving Scene {scene_num}")

    def clear_scene_hero(self, scene_num: int) -> None:
        """Drop hero/final flag so the scene is draft again (e.g. after re-edit)."""
        self.state.setdefault("scene_hero", {}).pop(str(scene_num), None)
        self.save_state()

    def get_scene_hero(self, scene_num: int) -> Optional[Dict[str, Any]]:
        return (self.state.get("scene_hero") or {}).get(str(scene_num))

    def hero_regen_scene(
        self,
        scene_num: int,
        *,
        resolution: str = "720p",
        only_existing: bool = True,
        run_qa: bool = True,
        approve_after: bool = True,
        snapshot_first: bool = True,
    ) -> Dict[str, Any]:
        """
        Delivery-quality pass: regenerate scene clips at a higher resolution
        without permanently changing the global draft config resolution.

        - Snapshots current main into assets/variants (draft preserve)
        - Temporarily sets config resolution for API calls
        - Regenerates clips (on-disk only by default)
        - Remuxes composite, optional approve + WIP rebuild
        - Records scene_hero in pipeline_state
        """
        scene = None
        for s in self.blueprint.get("scenes", []):
            if s.get("scene_number") == scene_num:
                scene = s
                break
        if not scene:
            raise GenerationFailure(f"Scene {scene_num} not found")

        resolution = str(resolution or "720p").strip().lower()
        if not resolution.endswith("p"):
            resolution = f"{resolution}p"

        if snapshot_first:
            try:
                self.snapshot_main_as_variant(scene_num)
            except Exception as e:
                print(f"  [Hero] Snapshot warning: {e}")

        prev_res = self.config.get("resolution", "720p")
        self.config["resolution"] = resolution
        regenerated: List[int] = []
        failed: List[Tuple[int, str]] = []
        self._hero_regen_active = True
        try:
            clips = scene.get("veo_clips") or []
            for clip in clips:
                cn = int(clip.get("clip_number", 0))
                main_path = clip_output_path(scene_num, cn)
                if only_existing and not file_is_usable(main_path, min_bytes=1024):
                    print(f"  [Hero] Skip clip {cn} (not on disk)")
                    continue
                if not only_existing and not file_is_usable(main_path, min_bytes=1024):
                    # still allow first-time if only_existing False
                    pass
                print(f"  [Hero] Regenerating Scene {scene_num} Clip {cn} @ {resolution}...")
                try:
                    self.regenerate_clip(scene_num, cn, feedback=None, run_qa=run_qa)
                    regenerated.append(cn)
                except Exception as e:
                    print(f"  [Hero] Clip {cn} failed: {e}")
                    failed.append((cn, str(e)))
        finally:
            self._hero_regen_active = False
            # Restore draft resolution so day-to-day stays cheap
            self.config["resolution"] = prev_res

        composite = None
        try:
            composite = self.remux_scene_from_disk(scene_num)
        except Exception as e:
            print(f"  [Hero] Remux warning: {e}")

        hero_meta = {
            "resolution": resolution,
            "at": time.strftime("%Y-%m-%dT%H:%M:%S"),
            "clip_numbers": regenerated,
            "clip_count": len(regenerated),
            "failed": [{"clip": c, "error": err} for c, err in failed],
            "composite_path": composite,
            "draft_resolution_restored": prev_res,
        }
        self.state.setdefault("scene_hero", {})[str(scene_num)] = hero_meta
        self.save_state()

        if approve_after and regenerated and not failed:
            self.approve_scene(scene_num)
        elif approve_after and regenerated:
            # partial success — still remux/WIP but don't hard-approve
            print("  [Hero] Partial success; not auto-approving (some clips failed).")
            try:
                self.rebuild_wip_movie(reason=f"hero partial Scene {scene_num}")
            except Exception:
                pass

        print(
            f"  [Hero] Scene {scene_num}: {len(regenerated)} clip(s) @ {resolution}; "
            f"config resolution restored to {prev_res}"
        )
        return hero_meta

    def save_config_to_disk(self) -> None:
        ensure_parent_dir(self.config_path)
        temp_file = f"{self.config_path}.tmp"
        with open(temp_file, "w", encoding="utf-8") as f:
            json.dump(self.config, f, indent=2)
        os.replace(temp_file, self.config_path)

    def reload_all(self) -> None:
        """Reload config, blueprint, and state from disk (after external edits)."""
        self.load_config()
        self.load_blueprint()
        self.load_state()

    def _build_suno_brief(self, music_bed: Dict[str, Any]) -> Dict[str, Any]:
        style = (music_bed.get("style_description") or "").strip()
        vocal = (music_bed.get("vocal_style") or "").strip()
        song_structure = music_bed.get("song_structure", []) or []

        tag_parts = [p for p in (style, vocal) if p]
        tags = ", ".join(tag_parts) if tag_parts else "cinematic, orchestral, emotional"

        lyric_blocks = []
        all_notes: List[str] = []
        for section in song_structure:
            notes = section.get("production_notes") or []
            all_notes.extend(notes)
            lyrics = section.get("lyrics")
            if lyrics:
                label = (section.get("section_label") or section.get("section_type") or "Section").strip()
                lyric_blocks.append(f"[{label}]\n{lyrics}")

        lyrics_text = "\n\n".join(lyric_blocks)
        notes_summary = "; ".join(all_notes[:6])

        return {
            "tags": tags,
            "lyrics_text": lyrics_text,
            "has_lyrics": bool(lyric_blocks),
            "notes_summary": notes_summary,
        }

    def _suno_submit(self, endpoint: str, payload: Dict[str, Any]) -> List[str]:
        try:
            req = urllib.request.Request(
                endpoint,
                data=json.dumps(payload).encode("utf-8"),
                headers={"Content-Type": "application/json"},
                method="POST",
            )
            with urllib.request.urlopen(req, timeout=60) as response:
                raw = response.read().decode()
        except urllib.error.HTTPError as e:
            body = e.read().decode(errors="ignore") if hasattr(e, "read") else ""
            raise GenerationFailure(f"suno-api returned HTTP {e.code} from {endpoint}: {body[:300]}")
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            raise GenerationFailure(f"Could not reach suno-api at '{endpoint}': {e}.")

        try:
            res_data = json.loads(raw)
        except json.JSONDecodeError as e:
            raise GenerationFailure(f"suno-api returned non-JSON response from {endpoint}: {e}")

        if isinstance(res_data, dict) and res_data.get("detail"):
            raise GenerationFailure(f"suno-api rejected the job: {res_data['detail']}")
        if not isinstance(res_data, list) or not res_data:
            raise GenerationFailure(f"suno-api returned an unexpected response shape from {endpoint}: {res_data}")

        clip_ids = [item["id"] for item in res_data if isinstance(item, dict) and item.get("id")]
        if not clip_ids:
            raise GenerationFailure(f"suno-api response contained no usable clip IDs: {res_data}")
        return clip_ids

    def _suno_poll_for_audio(self, base_url: str, clip_ids: List[str], s_num: int,
                              poll_interval: int, timeout_seconds: int) -> str:
        ids_param = ",".join(clip_ids)
        deadline = time.time() + timeout_seconds

        while time.time() < deadline:
            self._check_shutdown(f"Suno poll Scene {s_num}")
            try:
                req = urllib.request.Request(f"{base_url}/api/get?ids={ids_param}", method="GET")
                with urllib.request.urlopen(req, timeout=30) as response:
                    clips = json.loads(response.read().decode())
            except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, json.JSONDecodeError) as e:
                raise GenerationFailure(f"Scene {s_num} music: failed while polling suno-api job status: {e}")

            for clip in clips:
                status = clip.get("status")
                if status == "error":
                    err_detail = (clip.get("metadata") or {}).get("error_message", "no details provided")
                    raise GenerationFailure(f"Scene {s_num} music: suno-api reported an error: {err_detail}")
                if status == "complete" and clip.get("audio_url"):
                    print(f"  [Suno] Clip {clip.get('id')} complete.")
                    return clip["audio_url"]

            self._interruptible_sleep(poll_interval, f"Suno poll Scene {s_num}")

        raise GenerationFailure(f"Scene {s_num} music: suno-api job timed out.")

    def generate_suno_music(self, scene: Dict[str, Any]) -> str:
        s_num = scene["scene_number"]
        music_bed = scene.get("music_bed", {})
        brief = self._build_suno_brief(music_bed)
        output_music_path = music_output_path(s_num)
        ensure_parent_dir(output_music_path)
        os.makedirs("assets/music", exist_ok=True)

        # Resume: keep existing music if already on disk
        if file_is_usable(output_music_path, min_bytes=1024):
            print(f"  [Suno] Reusing existing music bed: {output_music_path}")
            return output_music_path

        music_job = self.state.setdefault("music_jobs", {}).get(str(s_num), {})
        base_url = os.environ.get("SUNO_API_URL", "http://localhost:3000").rstrip("/")
        poll_interval = int(os.environ.get("SUNO_POLL_INTERVAL_SECONDS", "5"))
        timeout_seconds = int(os.environ.get("SUNO_TIMEOUT_SECONDS", "300"))
        title = (scene.get("scene_filename") or f"scene_{s_num:02d}")[:80]

        # Resume pending download if we already have a URL from a prior run
        pending_url = music_job.get("audio_url")
        if pending_url:
            print(f"  [Suno] Resuming pending music download for Scene {s_num}...")
            try:
                self._download_to_path(
                    pending_url, output_music_path,
                    label=f"Scene {s_num} music",
                )
                self.state["music_jobs"][str(s_num)] = {
                    "status": "complete",
                    "path": output_music_path,
                }
                self.save_state()
                return output_music_path
            except GenerationFailure as e:
                print(f"  [Suno] Pending download failed ({e}); submitting a fresh job...")

        print(f"  [Suno] Submitting music generation for Scene {s_num}...")

        if brief["has_lyrics"]:
            endpoint = f"{base_url}/api/custom_generate"
            payload = {
                "prompt": brief["lyrics_text"],
                "tags": brief["tags"],
                "title": title,
                "make_instrumental": False,
                "wait_audio": False,
            }
        else:
            endpoint = f"{base_url}/api/generate"
            description = brief["tags"]
            if brief["notes_summary"]:
                description = f"{description}. {brief['notes_summary']}"
            payload = {
                "prompt": description,
                "make_instrumental": True,
                "wait_audio": False,
            }

        clip_ids = self._suno_submit(endpoint, payload)
        self.state.setdefault("music_jobs", {})[str(s_num)] = {
            "status": "submitted",
            "clip_ids": clip_ids,
        }
        self.save_state()

        audio_url = self._suno_poll_for_audio(base_url, clip_ids, s_num, poll_interval, timeout_seconds)
        self.state["music_jobs"][str(s_num)] = {
            "status": "pending_download",
            "clip_ids": clip_ids,
            "audio_url": audio_url,
        }
        self.save_state()

        self._download_to_path(audio_url, output_music_path, label=f"Scene {s_num} music")
        self.state["music_jobs"][str(s_num)] = {
            "status": "complete",
            "path": output_music_path,
        }
        self.save_state()
        return output_music_path

    def _file_to_data_uri(self, path: str) -> str:
        """Encode a local media file as a base64 data URI for the xAI API."""
        mime, _ = mimetypes.guess_type(path)
        if not mime:
            lower = path.lower()
            if lower.endswith(".png"):
                mime = "image/png"
            elif lower.endswith((".jpg", ".jpeg")):
                mime = "image/jpeg"
            elif lower.endswith(".webp"):
                mime = "image/webp"
            elif lower.endswith(".mp4"):
                mime = "video/mp4"
            else:
                mime = "application/octet-stream"
        with open(path, "rb") as f:
            encoded = base64.b64encode(f.read()).decode("ascii")
        return f"data:{mime};base64,{encoded}"

    def _find_character_anchor_path(self, prompt: str) -> Optional[str]:
        """
        Return the locked character reference for the PRIMARY on-screen subject.

        Matches the longest Character_* token first (so Character_N_Young wins over Character_N).
        Adult base tokens are not used when a Young/Teen token is present for that person,
        or when residual "Young Character_X" text remains without a variant token.
        """
        char_seeds = self.blueprint.get("global_production_variables", {}).get("character_seed_tokens", {})
        if not prompt or not char_seeds:
            return None

        # All seed keys sorted longest-first so _Young / _Teen beat base names
        keys_by_len = sorted(char_seeds.keys(), key=len, reverse=True)
        matches: List[tuple] = []  # (pos, key, path)
        for char_key in keys_by_len:
            pos = prompt.find(char_key)
            if pos < 0:
                continue
            seed_info = char_seeds[char_key]
            local_image_name = seed_info.get("reference_image_placeholder", f"{char_key.lower()}_ref.png")
            local_image_path = f"assets/characters/{local_image_name}"
            if os.path.exists(local_image_path):
                matches.append((pos, char_key, local_image_path))

        if not matches:
            if re.search(r"\b(Young\s+Character_|FLASHBACK|child|about\s+\d+\s+years)", prompt, re.I):
                print(
                    "  [Character Anchor] No matching ref file yet for young/flashback cast "
                    "(run Stage 0 to generate age-variant portraits)."
                )
            return None

        # Earliest mention wins; among same start, longer key already preferred via scan order
        matches.sort(key=lambda t: (t[0], -len(t[1])))
        pos, key, path = matches[0]

        # If earliest token is an adult base but a Young/Teen version of same person also appears, prefer that
        if not key.endswith(("_Young", "_Teen")):
            for alt_suffix in ("_Young", "_Teen"):
                alt = f"{key}{alt_suffix}"
                if alt in char_seeds:
                    alt_pos = prompt.find(alt)
                    if alt_pos >= 0:
                        alt_info = char_seeds[alt]
                        alt_name = alt_info.get("reference_image_placeholder", f"{alt.lower()}_ref.png")
                        alt_path = f"assets/characters/{alt_name}"
                        if os.path.exists(alt_path):
                            print(f"  [Character Anchor] Preferring age variant {alt} over adult {key}")
                            return alt_path
            # Residual "Young Character_N" without tokenized variant — do not use adult ref
            if re.search(rf"Young\s+{re.escape(key)}\b", prompt, re.I) or (
                re.search(r"\bFLASHBACK\b", prompt, re.I)
                and re.search(
                    rf"{re.escape(key)}\s*\([^)]{{0,40}}(about\s+)?([5-9]|1[0-7])\s+years?",
                    prompt,
                    re.I,
                )
            ):
                print(
                    f"  [Character Anchor] Skipping adult {key} ref in young/flashback context "
                    f"(use {key}_Young / {key}_Teen seeds)."
                )
                return None

        return path

    def _simplify_visual_for_single_clip(self, visual: str) -> str:
        """
        Rewrite multi-beat 'SHOT A. CUT to SHOT B' prompts into one continuous short-clip sequence.
        Short models handle one evolving shot better than hard multi-setup edits.
        """
        visual = (visual or "").strip()
        if not visual:
            return visual

        # Strip trailing technical suffix so we can reattach cleanly
        suffix = ""
        m = re.search(r"\s*/\s*720p.*$", visual, re.IGNORECASE)
        if m:
            suffix = visual[m.start():]
            visual = visual[:m.start()].strip()

        if re.search(r"\bCUT\s+TO\b", visual, re.IGNORECASE):
            parts = re.split(r"\bCUT\s+TO\b", visual, maxsplit=1, flags=re.IGNORECASE)
            if len(parts) == 2:
                first = parts[0].strip(" .;,")
                second = parts[1].strip(" .;,")
                visual = (
                    "Single continuous cinematic sequence for one short clip "
                    "(smooth transition, not a jarring random cut): "
                    f"Begin with {first}. Then the camera / scene transitions into {second}."
                )

        # Flashbacks should be clearly labeled as a new visual world
        if re.search(r"\bFLASHBACK\b", visual, re.IGNORECASE):
            if "new scene" not in visual.lower():
                visual = (
                    "Distinct FLASHBACK sequence in a clearly different time/place "
                    f"(do not continue the previous present-day framing): {visual}"
                )

        return f"{visual}{suffix}".strip()

    def _should_use_last_frame_continuation(
        self,
        clip: Dict[str, Any],
        continuation_source: str,
        prev_path: Optional[str],
    ) -> bool:
        """
        Only continue from the previous last frame for true same-setup extensions.
        Hard cuts, exteriors, flashbacks, and multi-shot 'CUT TO' prompts must start fresh
        (optionally with character reference images) so Grok is not stuck on the prior close-up.
        """
        if not self.config.get("smart_continuation", True):
            # Legacy behavior: any non-none continuation_source uses last frame
            return (
                continuation_source not in (None, "", "none")
                and bool(prev_path)
                and file_is_usable(prev_path, min_bytes=1024)
                and not str(prev_path).startswith("ctx_mock_")
            )

        if continuation_source in (None, "", "none"):
            return False
        if not prev_path or not file_is_usable(prev_path, min_bytes=1024):
            return False
        if str(prev_path).startswith("ctx_mock_"):
            return False

        visual = clip.get("visual_prompt") or ""
        if _HARD_CUT_RE.search(visual):
            return False
        # Explicit multi-setup language
        if re.search(r"\bCUT\s+TO\b", visual, re.IGNORECASE):
            return False
        # Kickball hits, crashes, sprints, etc. need a fresh shot — last-frame I2V freezes action
        if _ACTION_BEAT_RE.search(visual):
            return False

        return continuation_source.lower() in {
            "extend_previous",
            "extend",
            "continue",
            "previous",
            "continuous",
            "continuation",
        }

    def get_character_voice_profile(self, speaker: str) -> Dict[str, str]:
        """
        Return locked voice fields for a speaker token from character_seed_tokens.
        Keys: voice_profile (prompt text), tts_voice (edge-tts id), voice_label.
        """
        speaker = (speaker or "").strip()
        empty = {"voice_profile": "", "tts_voice": "", "voice_label": ""}
        if not speaker or speaker.lower() in ("none", "n/a", "null", "-"):
            return empty
        seeds = self.blueprint.get("global_production_variables", {}).get(
            "character_seed_tokens", {}
        )
        info = seeds.get(speaker)
        if not isinstance(info, dict):
            # try case-insensitive / alias
            for k, v in seeds.items():
                if k.lower() == speaker.lower():
                    info = v
                    speaker = k
                    break
        if not isinstance(info, dict):
            return empty
        profile = (
            (info.get("voice_profile") or info.get("voice_description") or "")
            .strip()
        )
        tts = (info.get("tts_voice") or info.get("edge_tts_voice") or "").strip()
        label = (info.get("voice_label") or speaker).strip()
        return {
            "voice_profile": profile,
            "tts_voice": tts,
            "voice_label": label,
            "character_key": speaker,
        }

    def set_character_voice_profile(
        self,
        char_key: str,
        *,
        voice_profile: Optional[str] = None,
        tts_voice: Optional[str] = None,
        voice_label: Optional[str] = None,
    ) -> Dict[str, Any]:
        """Persist voice lock on a character seed in the blueprint."""
        seeds = self.blueprint.setdefault("global_production_variables", {}).setdefault(
            "character_seed_tokens", {}
        )
        if char_key not in seeds or not isinstance(seeds[char_key], dict):
            raise GenerationFailure(f"Unknown character seed: {char_key}")
        info = seeds[char_key]
        if voice_profile is not None:
            info["voice_profile"] = voice_profile.strip()
        if tts_voice is not None:
            info["tts_voice"] = tts_voice.strip()
        if voice_label is not None:
            info["voice_label"] = voice_label.strip()
        self.save_blueprint_to_disk()
        return dict(info)

    def _voice_lock_clause(self, speaker: str) -> str:
        """Short clause for Grok AUDIO block — same every scene for this speaker."""
        vp = self.get_character_voice_profile(speaker)
        profile = vp.get("voice_profile") or ""
        if not profile:
            return ""
        label = vp.get("voice_label") or speaker
        # Keep compact — full profiles can be long
        return (
            f" VOICE LOCK for {label} (use this EXACT same vocal identity every time this "
            f"character speaks; do not reinvent pitch/age/accent between clips): {profile}"
        )

    def _build_video_generation_prompt(self, clip: Dict[str, Any], mode: str = "fresh") -> str:
        """
        Merge visual_prompt + audio_payload so Grok generates native audio
        (dialogue / narration / ambient / Foley) with the picture.

        AUDIO is placed first — Grok responds better when speech is explicit and early.
        Injects locked character voice_profile when speaker is a known seed.
        """
        visual = self._simplify_visual_for_single_clip((clip.get("visual_prompt") or "").strip())
        audio = clip.get("audio_payload") or {}
        speaker = (audio.get("speaker") or "").strip()
        dialogue = (audio.get("dialogue") or "").strip()
        sfx = (audio.get("sfx") or audio.get("sound_effects") or "").strip()
        ambient = (audio.get("ambient") or audio.get("atmosphere") or "").strip()

        framing_bits: List[str] = []
        if mode == "continue":
            framing_bits.append(
                "Continue seamlessly from the provided starting frame with the same character identity, "
                "wardrobe, and location. Natural camera motion only — do not invent a new establishing shot. "
                "Show clear progressive motion for the primary action (not a frozen pose)."
            )
        else:
            framing_bits.append(
                "Follow the camera framing and location in this prompt exactly. "
                "If a wide/exterior/establishing shot is specified, show that environment clearly "
                "(do not stay locked on an unrelated close-up face). "
                "Prioritize the PRIMARY subject and ONE clear action with visible motion; "
                "background characters may stay mostly still."
            )

        speaker_ok = bool(speaker) and speaker.lower() not in ("none", "n/a", "null", "-")
        delivery = str(audio.get("delivery") or "").strip().lower()
        is_internal_vo = delivery in (
            "voiceover_internal",
            "internal",
            "vo_internal",
            "thought",
            "thinking",
            "narration",
            "vo",
        )
        voice_lock = self._voice_lock_clause(speaker) if speaker_ok else ""

        # Leading AUDIO block (format used successfully with Grok Imagine)
        if dialogue and speaker_ok and is_internal_vo:
            audio_block = (
                f'AUDIO: Required audible stereo soundtrack. Off-camera internal monologue / narration '
                f'by {speaker} at normal volume (NOT lip-synced conversation with another character): '
                f'"{dialogue}". Character on screen should keep lips mostly closed / not mouth this text '
                f'to someone else. Soft ambient under the voice.'
                f'{voice_lock}'
            )
            framing_bits.append(
                "Performance: contemplative thinking face; do not stage this as spoken dialogue to another person."
            )
        elif dialogue and speaker_ok:
            audio_block = (
                f'AUDIO: Required audible stereo soundtrack. '
                f'Clear on-camera spoken dialogue by {speaker} at normal listening volume, '
                f'lip-synced when the speaker is visible: "{dialogue}". '
                f'Also include matching ambient room tone and environmental Foley under the voice.'
                f'{voice_lock}'
            )
        elif dialogue:
            audio_block = (
                f'AUDIO: Required audible stereo soundtrack. Clear voiceover at normal volume: "{dialogue}". '
                f'Include ambient atmosphere under the voice.'
            )
        else:
            audio_block = (
                "AUDIO: Required audible stereo soundtrack with realistic ambient environmental sound "
                "and Foley matching the action. No spoken dialogue. Do not output a silent clip."
            )

        if sfx:
            audio_block += f" Sound effects: {sfx}."
        if ambient:
            audio_block += f" Ambient bed: {ambient}."
        if self.config.get("use_video_audio_for_music", True) and dialogue:
            audio_block += " Keep speech dominant over any background music or wind."

        return (
            f"{audio_block} "
            f"VISUAL: {visual} {' '.join(framing_bits)} "
            f"Must include synchronized native audio track with the video."
        ).strip()

    def _mp4_audio_stats(self, video_path: str) -> Dict[str, Any]:
        """
        Pure-Python MP4 probe: detect audio track and rough payload size via stsz samples.
        Works even when ffmpeg is not on PATH.
        """
        import struct

        stats: Dict[str, Any] = {
            "has_audio_track": False,
            "audio_bytes": 0,
            "audio_samples": 0,
            "video_bytes": 0,
        }
        if not file_is_usable(video_path, min_bytes=8):
            return stats

        try:
            with open(video_path, "rb") as f:
                data = f.read()
        except OSError:
            return stats

        def iter_boxes(buf: bytes, start: int = 0, end: Optional[int] = None):
            if end is None:
                end = len(buf)
            i = start
            while i + 8 <= end:
                size = struct.unpack(">I", buf[i : i + 4])[0]
                typ = buf[i + 4 : i + 8]
                if size == 1:
                    if i + 16 > end:
                        break
                    size = struct.unpack(">Q", buf[i + 8 : i + 16])[0]
                    hdr = 16
                elif size == 0:
                    size = end - i
                    hdr = 8
                else:
                    hdr = 8
                if size < hdr:
                    break
                yield i, size, typ, i + hdr, i + size
                next_i = i + size
                if next_i <= i:
                    break
                i = next_i

        def find_boxes(buf: bytes, target: bytes, start: int = 0, end: Optional[int] = None):
            for i, size, typ, cs, ce in iter_boxes(buf, start, end):
                if typ == target:
                    yield i, size, typ, cs, ce
                if typ in (b"moov", b"trak", b"mdia", b"minf", b"stbl", b"udta", b"edts"):
                    yield from find_boxes(buf, target, cs, ce)

        try:
            for _, _, _, tcs, tce in find_boxes(data, b"trak"):
                hdlrs = list(find_boxes(data, b"hdlr", tcs, tce))
                if not hdlrs:
                    continue
                _, _, _, hs, _ = hdlrs[0]
                handler = data[hs + 8 : hs + 12]
                stszs = list(find_boxes(data, b"stsz", tcs, tce))
                if not stszs:
                    continue
                _, _, _, ss, se = stszs[0]
                if ss + 12 > len(data):
                    continue
                sample_size = struct.unpack(">I", data[ss + 4 : ss + 8])[0]
                sample_count = struct.unpack(">I", data[ss + 8 : ss + 12])[0]
                if sample_size == 0:
                    need = ss + 12 + 4 * sample_count
                    if need > len(data) or sample_count < 0 or sample_count > 2_000_000:
                        total = 0
                    else:
                        sizes = struct.unpack(f">{sample_count}I", data[ss + 12 : need])
                        total = int(sum(sizes))
                else:
                    total = int(sample_size) * int(sample_count)

                if handler == b"soun":
                    stats["has_audio_track"] = True
                    stats["audio_bytes"] = total
                    stats["audio_samples"] = sample_count
                elif handler == b"vide":
                    stats["video_bytes"] = total
        except Exception as e:
            print(f"  [Audio Probe Warning] MP4 parse failed for '{video_path}': {e}")

        return stats

    def _video_has_audio(self, video_path: str) -> bool:
        """Return True if the file has an audio track (ffmpeg or pure-Python MP4 parse)."""
        if not file_is_usable(video_path, min_bytes=1):
            return False

        # Fast pure-Python path (reliable without ffmpeg on PATH)
        mp4 = self._mp4_audio_stats(video_path)
        if mp4.get("has_audio_track"):
            return True

        ffmpeg = resolve_ffmpeg()
        try:
            result = subprocess.run(
                [ffmpeg, "-hide_banner", "-i", video_path],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            probe = (result.stderr or b"").decode(errors="ignore")
            for line in probe.splitlines():
                if "Audio:" in line or ": Audio:" in line:
                    return True
            return False
        except Exception:
            return bool(mp4.get("has_audio_track"))

    def _audio_is_weak(self, video_path: str, duration_hint: float = 8.0) -> bool:
        """
        Heuristic: missing track, or very small audio payload for the duration
        (often near-silent ambient with no usable speech).
        """
        stats = self._mp4_audio_stats(video_path)
        if not stats.get("has_audio_track"):
            return True
        # ~4 KB/s is extremely sparse for audible dialogue AAC; normal speech is much higher
        min_bytes = max(12_000, int(duration_hint * 4_000))
        return int(stats.get("audio_bytes") or 0) < min_bytes

    def _synthesize_dialogue_wav(
        self,
        text: str,
        wav_path: str,
        *,
        speaker: str = "",
    ) -> str:
        """
        Create a WAV of the dialogue.

        Backends (first success wins):
          1. edge-tts (prefer character seed tts_voice, then EDGE_TTS_VOICE env)
          2. espeak-ng / espeak  (typical on WSL/Linux)
          3. Windows SAPI via powershell
        """
        import shutil

        ensure_parent_dir(wav_path)
        raw = (text or "").replace("\r", " ").replace("\n", " ").strip()
        if not raw:
            raise GenerationFailure("Cannot synthesize empty dialogue.")

        abs_wav = os.path.abspath(wav_path)
        errors: List[str] = []
        voice_id = ""
        if speaker:
            voice_id = self.get_character_voice_profile(speaker).get("tts_voice") or ""
        if not voice_id:
            voice_id = os.environ.get("EDGE_TTS_VOICE", "en-US-GuyNeural")

        # --- 1) edge-tts (most natural; optional: pip install edge-tts) ---
        edge = shutil.which("edge-tts")
        if edge:
            mp3_path = abs_wav + ".mp3"
            cmd = [
                edge,
                "--voice", voice_id,
                "--text", raw,
                "--write-media", mp3_path,
            ]
            result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            if result.returncode == 0 and file_is_usable(mp3_path, min_bytes=100):
                ffmpeg = resolve_ffmpeg()
                conv = subprocess.run(
                    [ffmpeg, "-y", "-i", mp3_path, abs_wav],
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                )
                try:
                    os.remove(mp3_path)
                except OSError:
                    pass
                if conv.returncode == 0 and file_is_usable(wav_path, min_bytes=100):
                    print(f"  [Audio] TTS backend: edge-tts voice={voice_id!r} speaker={speaker!r}")
                    return wav_path
            errors.append(f"edge-tts: {(result.stderr or b'').decode(errors='ignore')[-120:]}")

        # --- 2) Windows SAPI (native Windows, or powershell.exe from WSL) ---
        ps_safe = raw.replace("'", "''")
        ps_candidates = []
        if os.name == "nt":
            ps_candidates = ["powershell"]
        else:
            for name in ("powershell.exe", "pwsh.exe"):
                if shutil.which(name):
                    ps_candidates.append(name)
            for p in (
                "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe",
                "/mnt/c/Program Files/PowerShell/7/pwsh.exe",
            ):
                if os.path.isfile(p):
                    ps_candidates.append(p)

        win_wav = abs_wav
        if _running_on_wsl() and abs_wav.startswith("/mnt/"):
            parts = abs_wav.split("/")
            if len(parts) > 3 and parts[1] == "mnt" and len(parts[2]) == 1:
                win_wav = parts[2].upper() + ":\\" + "\\".join(parts[3:])

        for ps in ps_candidates:
            ps_script = (
                "Add-Type -AssemblyName System.Speech; "
                "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
                f"$s.SetOutputToWaveFile('{win_wav.replace(chr(39), chr(39)+chr(39))}'); "
                f"$s.Speak('{ps_safe}'); "
                "$s.Dispose();"
            )
            cmd = [ps, "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps_script]
            try:
                result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
            except (FileNotFoundError, subprocess.TimeoutExpired) as e:
                errors.append(f"{ps}: {e}")
                continue
            if result.returncode == 0 and file_is_usable(wav_path, min_bytes=100):
                print(f"  [Audio] TTS backend: Windows SAPI via {os.path.basename(ps)}")
                return wav_path
            errors.append(
                f"{ps}: {(result.stderr or b'').decode(errors='ignore')[-120:]}"
            )

        # --- 3) espeak / espeak-ng (WSL / Linux fallback) ---
        for espeak in ("espeak-ng", "espeak"):
            exe = shutil.which(espeak)
            if not exe:
                continue
            cmd = [exe, "-w", abs_wav, "-s", "140", "-a", "150", raw]
            result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            if result.returncode == 0 and file_is_usable(wav_path, min_bytes=100):
                print(f"  [Audio] TTS backend: {espeak}")
                return wav_path
            errors.append(f"{espeak}: {(result.stderr or b'').decode(errors='ignore')[-120:]}")

        raise GenerationFailure(
            "TTS dialogue synthesis failed. Prefer: pip install edge-tts. "
            "Or on WSL: sudo apt install espeak-ng. On Windows: PowerShell SAPI. "
            f"Details: {' | '.join(errors)[:400]}"
        )

    def _ensure_dialogue_audio(self, video_path: str, clip: Dict[str, Any]) -> str:
        """
        Guarantee audible spoken dialogue on the clip.

        Modes (pipeline_config.dialogue_audio_mode):
          - "replace" (default): video picture + TTS only — no double voice
          - "mix": TTS over quiet native bed (can double if Grok also spoke)
        """
        if not self.config.get("ensure_dialogue_audio", True):
            return video_path

        audio = clip.get("audio_payload") or {}
        dialogue = str(audio.get("dialogue") or "").strip()
        speaker = str(audio.get("speaker") or "").strip()
        if not dialogue or speaker.lower() in ("none", "n/a", ""):
            return video_path

        ffmpeg = resolve_ffmpeg()
        tts_wav = f"{video_path}.dialogue.wav"
        tmp_out = f"{video_path}.voiced.tmp.mp4"
        native_backup = f"{video_path}.native.mp4"
        mode = str(self.config.get("dialogue_audio_mode", "replace")).lower().strip()
        tts_vol = float(self.config.get("dialogue_tts_volume", 1.0))
        bed_vol = float(self.config.get("native_audio_mix_volume", 0.12))

        try:
            import shutil as _shutil

            # Prefer original Grok picture as source if we saved a native backup earlier
            source_video = native_backup if file_is_usable(native_backup, min_bytes=1000) else video_path
            if source_video == video_path and not file_is_usable(native_backup, min_bytes=1000):
                # First time: keep a native backup before we replace audio
                try:
                    _shutil.copy2(video_path, native_backup)
                    source_video = native_backup
                except OSError:
                    pass

            print(
                f"  [Audio] Applying TTS dialogue ({mode}) for speaker={speaker!r}..."
            )
            self._synthesize_dialogue_wav(dialogue, tts_wav, speaker=speaker)

            has_native = self._video_has_audio(source_video)
            if mode == "mix" and has_native:
                # Quiet ambient under TTS — can still double if native has speech
                filter_complex = (
                    f"[0:a]volume={bed_vol},highpass=f=120,"
                    f"aformat=sample_rates=48000:channel_layouts=stereo[bed];"
                    f"[1:a]volume={tts_vol},aformat=sample_rates=48000:channel_layouts=stereo[voice];"
                    f"[bed][voice]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]"
                )
                cmd = [
                    ffmpeg, "-y",
                    "-i", source_video,
                    "-i", tts_wav,
                    "-filter_complex", filter_complex,
                    "-map", "0:v:0",
                    "-map", "[aout]",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k",
                    "-shortest",
                    "-movflags", "+faststart",
                    tmp_out,
                ]
            else:
                # REPLACE (default): picture from Grok + TTS only — one clear voice, no delay double
                if mode != "replace":
                    print(f"  [Audio] Mode '{mode}' unavailable without native audio; using replace.")
                cmd = [
                    ffmpeg, "-y",
                    "-i", source_video,
                    "-i", tts_wav,
                    "-map", "0:v:0",
                    "-map", "1:a:0",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k",
                    "-shortest",
                    "-movflags", "+faststart",
                    tmp_out,
                ]

            try:
                result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            except FileNotFoundError as e:
                raise GenerationFailure(f"ffmpeg executable not runnable ({ffmpeg}): {e}")

            if result.returncode != 0 or not file_is_usable(tmp_out, min_bytes=50_000):
                err = (result.stderr or b"").decode(errors="ignore")[-500:]
                raise GenerationFailure(f"Failed to mux dialogue TTS onto clip: {err}")

            stats = self._mp4_audio_stats(tmp_out)
            if not stats.get("has_audio_track") and not self._video_has_audio(tmp_out):
                raise GenerationFailure("Dialogue mux produced a file with no audio track.")
            if os.path.getsize(tmp_out) < 100_000:
                raise GenerationFailure(
                    f"Dialogue mux output suspiciously small ({os.path.getsize(tmp_out)} bytes)."
                )

            os.replace(tmp_out, video_path)
            print(f"  [Audio] Dialogue applied ({mode}) -> {video_path}")
            return video_path
        except GenerationFailure as e:
            print(f"  [Audio Warning] {e}. Leaving original clip audio as-is.")
            return video_path
        finally:
            for p in (tts_wav, tmp_out):
                try:
                    if os.path.exists(p):
                        os.remove(p)
                except OSError:
                    pass

    def _extract_last_frame(self, video_path: str, frame_path: str) -> str:
        """Extract the last frame of a clip for Grok image-to-video continuity."""
        ensure_parent_dir(frame_path)
        ffmpeg = resolve_ffmpeg()
        cmd = [
            ffmpeg, "-y",
            "-sseof", "-0.05",
            "-i", video_path,
            "-frames:v", "1",
            "-update", "1",
            frame_path,
        ]
        try:
            result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        except Exception as e:
            raise GenerationFailure(f"Failed to extract last frame from '{video_path}': {e}")

        if result.returncode != 0 or not os.path.exists(frame_path) or os.path.getsize(frame_path) == 0:
            stderr_tail = result.stderr.decode(errors="ignore")[-400:]
            raise GenerationFailure(
                f"Failed to extract last frame from '{video_path}'. FFmpeg: {stderr_tail}"
            )
        return frame_path

    def _grok_request(self, method: str, url: str, payload: Optional[Dict[str, Any]] = None,
                      timeout: int = 60) -> Dict[str, Any]:
        api_key = os.environ.get("XAI_API_KEY")
        if not api_key:
            raise GenerationFailure("XAI_API_KEY is not set. Required for Grok video generation.")

        headers = {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        }
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as response:
                raw = response.read().decode()
        except urllib.error.HTTPError as e:
            body = e.read().decode(errors="ignore") if hasattr(e, "read") else ""
            raise GenerationFailure(f"xAI API returned HTTP {e.code} from {url}: {body[:400]}")
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            raise GenerationFailure(f"Could not reach xAI API at '{url}': {e}")

        if not raw:
            return {}
        try:
            return json.loads(raw)
        except json.JSONDecodeError as e:
            raise GenerationFailure(f"xAI API returned non-JSON from {url}: {e}")

    def _grok_submit_generation(self, payload: Dict[str, Any]) -> str:
        result = self._grok_request("POST", f"{XAI_API_BASE}/videos/generations", payload, timeout=120)
        request_id = result.get("request_id")
        if not request_id:
            raise GenerationFailure(f"xAI video generation response missing request_id: {result}")
        return request_id

    def _grok_poll_for_video_url(self, request_id: str, scene_num: int, clip_num: int) -> str:
        poll_interval = int(os.environ.get("GROK_POLL_INTERVAL_SECONDS", "5"))
        timeout_seconds = int(os.environ.get("GROK_TIMEOUT_SECONDS", "900"))
        deadline = time.time() + timeout_seconds

        while time.time() < deadline:
            self._check_shutdown(f"Grok poll Scene {scene_num} Clip {clip_num}")
            data = self._grok_request("GET", f"{XAI_API_BASE}/videos/{request_id}", timeout=60)
            status = data.get("status")

            if status == "done":
                video = data.get("video") or {}
                if not video.get("respect_moderation", True):
                    raise GenerationFailure(
                        f"Scene {scene_num} Clip {clip_num}: Grok video blocked by moderation."
                    )
                url = video.get("url")
                if not url:
                    raise GenerationFailure(
                        f"Scene {scene_num} Clip {clip_num}: Grok returned done status with no video URL."
                    )
                return url

            if status in ("failed", "expired"):
                err = data.get("error") or {}
                detail = err.get("message") or data
                raise GenerationFailure(
                    f"Scene {scene_num} Clip {clip_num}: Grok video job {status}: {detail}"
                )

            progress = data.get("progress")
            progress_note = f" ({progress}%)" if progress is not None else ""
            print(f"  [Grok] Still generating clip {clip_num}{progress_note}...")
            self._interruptible_sleep(poll_interval, f"Grok poll Scene {scene_num} Clip {clip_num}")

        raise GenerationFailure(
            f"Scene {scene_num} Clip {clip_num}: Grok video job timed out after {timeout_seconds}s."
        )

    def _try_resume_grok_download(self, scene_num: int, clip_num: int, output_clip_path: str) -> bool:
        """
        Attempt to finish a previously submitted Grok job without re-billing generation.
        Returns True if the local clip file is ready.
        """
        if file_is_usable(output_clip_path, min_bytes=1024):
            return True

        job = self.state.get("clip_jobs", {}).get(self._clip_job_key(scene_num, clip_num), {})
        request_id = job.get("request_id")
        video_url = job.get("video_url")

        # Prefer re-polling request_id (URL may have expired)
        if request_id:
            print(f"  [Grok] Resuming job request_id={request_id} for Clip {clip_num}...")
            try:
                video_url = self._grok_poll_for_video_url(request_id, scene_num, clip_num)
                self._update_clip_job(
                    scene_num, clip_num,
                    status="pending_download",
                    request_id=request_id,
                    video_url=video_url,
                    path=output_clip_path,
                )
            except GenerationFailure as e:
                print(f"  [Grok] Could not resume request_id={request_id}: {e}")
                # Fall through to stored URL, if any
                if not video_url:
                    return False

        if video_url:
            try:
                self._download_to_path(
                    video_url, output_clip_path,
                    label=f"Scene {scene_num} Clip {clip_num}",
                )
                self._update_clip_job(
                    scene_num, clip_num,
                    status="complete",
                    path=output_clip_path,
                    video_url=video_url,
                    request_id=request_id,
                )
                return True
            except GenerationFailure as e:
                print(f"  [Grok] Resume download failed: {e}")
                self._update_clip_job(
                    scene_num, clip_num,
                    status="download_failed",
                    last_error=str(e),
                    path=output_clip_path,
                )
                return False

        return False

    def _invalidate_clip_file(self, scene_num: int, clip_num: int, output_clip_path: str, reason: str) -> None:
        """Delete a bad local clip and clear job download pointers so it can be regenerated."""
        print(f"  [Grok] Invalidating Clip {clip_num}: {reason}")
        try:
            if os.path.exists(output_clip_path):
                os.remove(output_clip_path)
        except OSError as e:
            print(f"  [Grok Warning] Could not remove '{output_clip_path}': {e}")
        self._update_clip_job(
            scene_num, clip_num,
            status="invalidated",
            path=output_clip_path,
            request_id=None,
            video_url=None,
            last_error=reason,
            qa_approved=False,
        )

    def generate_grok_clip(
        self,
        scene_num: int,
        clip: Dict[str, Any],
        previous_context_id: Any = None,
        force_regenerate: bool = False,
        model_name: Optional[str] = None,
        output_path: Optional[str] = None,
        job_key: Optional[str] = None,
        skip_stale_check: bool = False,
    ) -> tuple:
        """Generate a video clip via xAI Grok Imagine video API (text / image / reference modes)."""
        clip_num = clip["clip_number"]
        continuation_source = clip.get("veo_continuation_source", "none")
        output_clip_path = output_path or clip_output_path(scene_num, clip_num)
        ensure_parent_dir(output_clip_path)
        os.makedirs("assets/video", exist_ok=True)

        self._active_job_key = job_key  # isolates variant jobs from main clip_jobs
        job_lookup = job_key or self._clip_job_key(scene_num, clip_num)
        job = self.state.get("clip_jobs", {}).get(job_lookup, {})
        prior_qa_failed = job.get("qa_approved") is False
        character_stale = (not skip_stale_check) and self.is_clip_stale(scene_num, clip_num)
        if character_stale and not force_regenerate:
            print(
                f"  [Stale] Clip {clip_num} is out of date (character redesign) — will not reuse on-disk file."
            )
            force_regenerate = True

        # Forced regen (QA retry) or previous QA failure on disk
        if force_regenerate or prior_qa_failed:
            if file_is_usable(output_clip_path, min_bytes=1):
                self._invalidate_clip_file(
                    scene_num, clip_num, output_clip_path,
                    reason=(
                        "force_regenerate"
                        if force_regenerate and not character_stale
                        else ("character_stale" if character_stale else "prior_qa_failed")
                    ),
                )

        # Resume: skip generation when the clip is already on disk WITH audio and prior QA ok
        if file_is_usable(output_clip_path, min_bytes=1024) and not force_regenerate:
            has_audio = self._video_has_audio(output_clip_path)
            qa_ok = job.get("qa_approved") is not False
            dialogue_ready = job.get("dialogue_audio_ensured") is True
            dialogue_text = str(
                ((clip.get("audio_payload") or {}).get("dialogue") or "")
            ).strip()
            needs_dialogue = bool(dialogue_text)
            if (has_audio or not self.config.get("regenerate_silent_clips", True)) and qa_ok:
                # Still ensure TTS dialogue overlay if missing from older renders
                if needs_dialogue and self.config.get("ensure_dialogue_audio", True) and not dialogue_ready:
                    print(f"  [Grok] Existing Clip {clip_num} lacks ensured dialogue audio — mixing TTS voiceover...")
                    self._ensure_dialogue_audio(output_clip_path, clip)
                    self._update_clip_job(
                        scene_num, clip_num,
                        status="complete",
                        path=output_clip_path,
                        has_audio=True,
                        dialogue_audio_ensured=True,
                    )
                    return output_clip_path, output_clip_path
                print(f"  [Grok] Reusing existing Clip {clip_num}: {output_clip_path} (audio={has_audio})")
                self._update_clip_job(
                    scene_num, clip_num,
                    status="complete",
                    path=output_clip_path,
                    has_audio=has_audio,
                )
                return output_clip_path, output_clip_path
            if not has_audio and self.config.get("regenerate_silent_clips", True):
                self._invalidate_clip_file(
                    scene_num, clip_num, output_clip_path,
                    reason="no_audio_track",
                )
            elif not qa_ok:
                self._invalidate_clip_file(
                    scene_num, clip_num, output_clip_path,
                    reason="qa_not_approved",
                )

        # Resume: finish a prior generate+download that failed mid-way (skip if force regen)
        if not force_regenerate and self._try_resume_grok_download(scene_num, clip_num, output_clip_path):
            if self._video_has_audio(output_clip_path) or not self.config.get("regenerate_silent_clips", True):
                print(f"  [Grok] Resumed Clip {clip_num} successfully: {output_clip_path}")
                return output_clip_path, output_clip_path
            print(f"  [Grok] Resumed file for Clip {clip_num} is silent — submitting a new audio-aware job...")
            self._invalidate_clip_file(scene_num, clip_num, output_clip_path, reason="resumed_silent")

        # Decide continuation vs fresh shot BEFORE building the prompt
        prev_path = previous_context_id if isinstance(previous_context_id, str) else None
        use_continuation = self._should_use_last_frame_continuation(clip, continuation_source, prev_path)
        mode = "continue" if use_continuation else "fresh"
        prompt = self._build_video_generation_prompt(clip, mode=mode)

        print(
            f"  [Grok] Generating Clip {clip_num} "
            f"(blueprint_source={continuation_source}, mode={mode})..."
        )
        if not use_continuation and continuation_source not in (None, "", "none"):
            print(
                "  [Grok] Smart continuation: blueprint says extend, but prompt is a cut/new setup "
                "or big action beat — using fresh generation instead of last-frame lock."
            )

        audio_payload = clip.get("audio_payload") or {}
        if (audio_payload.get("dialogue") or "").strip():
            print(f"  [Grok] Audio: speaker={audio_payload.get('speaker')!r}, dialogue included in prompt")
        else:
            print("  [Grok] Audio: ambient/Foley only (no dialogue on this clip)")

        throttle_delay = int(os.environ.get("GROK_THROTTLE_DELAY", os.environ.get("VEO_THROTTLE_DELAY", "10")))
        if throttle_delay > 0:
            print(f"  [Grok] Throttling: Cooling down for {throttle_delay}s...")
            self._interruptible_sleep(throttle_delay, f"Grok throttle Scene {scene_num} Clip {clip_num}")

        resolved_model = model_name or self.config.get("model_name", "grok-imagine-video")
        resolved_res = str(self.config.get("resolution", "720p"))
        dur_profile = resolve_duration_profile(
            self.config,
            provider=self.config.get("video_provider"),
            model_name=resolved_model,
            resolution=resolved_res,
        )
        duration = resolve_default_duration(
            self.config,
            provider=self.config.get("video_provider"),
            model_name=resolved_model,
            resolution=resolved_res,
        )
        # Prefer explicit duration_seconds, then timestamp ("00:00-00:08")
        if clip.get("duration_seconds") is not None:
            try:
                duration = int(clip.get("duration_seconds"))
            except (TypeError, ValueError):
                pass
        ts = str(clip.get("timestamp") or "")
        m_ts = re.match(r"^\s*(\d+):(\d{2})\s*-\s*(\d+):(\d{2})\s*$", ts)
        if m_ts and clip.get("duration_seconds") is None:
            a = int(m_ts.group(1)) * 60 + int(m_ts.group(2))
            b = int(m_ts.group(3)) * 60 + int(m_ts.group(4))
            if b > a:
                duration = b - a
        # Clamp to provider profile (Grok 1–15, Veo tighter)
        duration = max(int(dur_profile["min"]), min(int(dur_profile["max"]), int(duration)))

        payload: Dict[str, Any] = {
            "model": resolved_model,
            "prompt": prompt,
            "duration": duration,
            "aspect_ratio": self.config.get("aspect_ratio", "16:9"),
            "resolution": resolved_res,
        }

        self._check_shutdown(f"Grok generate Scene {scene_num} Clip {clip_num}")
        # xAI: text-to-video allows longer; image-to-video / reference-to-video max is 10s
        # (HTTP 400: "Duration Ns exceeds the maximum allowed for reference-to-video")
        GROK_IMAGE_OR_REF_MAX_SEC = 10
        if use_continuation:
            frame_path = (
                f"{os.path.splitext(output_clip_path)[0]}_seed_frame.png"
                if output_path
                else f"assets/video/scene_{scene_num:02d}_clip_{clip_num:02d}_seed_frame.png"
            )
            ensure_parent_dir(frame_path)
            print(f"  [Grok] True continuation: image-to-video from last frame of {prev_path}...")
            self._extract_last_frame(prev_path, frame_path)
            payload["image"] = {"url": self._file_to_data_uri(frame_path)}
            if int(payload["duration"]) > GROK_IMAGE_OR_REF_MAX_SEC:
                print(
                    f"  [Grok] Clamping duration {payload['duration']}s → "
                    f"{GROK_IMAGE_OR_REF_MAX_SEC}s (image-to-video max)"
                )
                payload["duration"] = GROK_IMAGE_OR_REF_MAX_SEC
        else:
            # Fresh shot: lock PRIMARY visual subject (first adult Character_* in visual_prompt).
            # Do NOT prefer VO speaker — narration is often off-screen / different person.
            # Do NOT use adult Stage-0 portraits for young/flashback versions of characters.
            anchor_probe = clip.get("visual_prompt") or ""
            anchor_path = self._find_character_anchor_path(anchor_probe)
            if anchor_path:
                print(f"  [Character Anchor] Primary subject ref: {anchor_path}")
                payload["reference_images"] = [{"url": self._file_to_data_uri(anchor_path)}]
                if int(payload["duration"]) > GROK_IMAGE_OR_REF_MAX_SEC:
                    print(
                        f"  [Grok] Clamping duration {payload['duration']}s → "
                        f"{GROK_IMAGE_OR_REF_MAX_SEC}s (reference-to-video max)"
                    )
                    payload["duration"] = GROK_IMAGE_OR_REF_MAX_SEC
            else:
                print(
                    "  [Character Anchor] No adult ref applied "
                    "(missing character file, or young/flashback cast uses text-only age)."
                )

        try:
            request_id = self._grok_submit_generation(payload)
            print(f"  [Grok] Submitted job request_id={request_id}")
            # Persist immediately so a crash during poll can still resume
            self._update_clip_job(
                scene_num, clip_num,
                status="submitted",
                request_id=request_id,
                path=output_clip_path,
            )

            video_url = self._grok_poll_for_video_url(request_id, scene_num, clip_num)
            self._update_clip_job(
                scene_num, clip_num,
                status="pending_download",
                request_id=request_id,
                video_url=video_url,
                path=output_clip_path,
            )

            self._download_to_path(
                video_url, output_clip_path,
                label=f"Scene {scene_num} Clip {clip_num}",
            )
            audio_stats = self._mp4_audio_stats(output_clip_path)
            has_audio = bool(audio_stats.get("has_audio_track")) or self._video_has_audio(output_clip_path)
            print(
                f"  [Grok] Clip {clip_num} audio probe: track={has_audio}, "
                f"payload_bytes={audio_stats.get('audio_bytes', 0)}"
            )
            if not has_audio:
                print(f"  [Grok Warning] Clip {clip_num} has NO audio track after download.")
            elif self._audio_is_weak(output_clip_path, duration_hint=float(duration)):
                print(f"  [Grok Warning] Clip {clip_num} audio payload looks weak/near-silent.")

            # Guarantee audible spoken lines (Grok often returns ambient-only beds)
            self._ensure_dialogue_audio(output_clip_path, clip)
            has_audio = self._video_has_audio(output_clip_path)
            has_ref_image = bool(payload.get("image") or payload.get("reference_images"))
            is_extend = bool(use_continuation)
            self._update_clip_job(
                scene_num, clip_num,
                status="complete",
                request_id=request_id,
                video_url=video_url,
                path=output_clip_path,
                has_audio=has_audio,
                dialogue_audio_ensured=True,
                audio_bytes=audio_stats.get("audio_bytes"),
                model=resolved_model,
                resolution=str(payload.get("resolution") or self.config.get("resolution")),
                duration_sec=duration,
            )
            self.clear_clip_stale(scene_num, clip_num)
            # Billable generation completed (new submit + download) — track list-rate actual
            try:
                self.record_video_generation_cost(
                    scene_num=scene_num,
                    clip_num=clip_num,
                    duration_sec=float(duration),
                    resolution=str(payload.get("resolution") or self.config.get("resolution") or "720p"),
                    model=str(resolved_model),
                    has_ref_image=has_ref_image,
                    is_extend=is_extend,
                    request_id=str(request_id or ""),
                )
            except Exception as cost_err:
                print(f"  [Cost Warning] Could not record actual cost: {cost_err}")
        except (PipelineInterrupted, KeyboardInterrupt):
            # Keep submitted request_id in clip_jobs so resume can re-poll/download
            self._update_clip_job(
                scene_num, clip_num,
                status="interrupted",
                path=output_clip_path,
                last_error="interrupted_by_user",
            )
            raise
        except GenerationFailure as e:
            self._update_clip_job(
                scene_num, clip_num,
                status="failed",
                last_error=str(e),
                path=output_clip_path,
            )
            raise
        except Exception as e:
            err = f"Scene {scene_num} Clip {clip_num}: Grok generation failed: {e}"
            self._update_clip_job(
                scene_num, clip_num,
                status="failed",
                last_error=err,
                path=output_clip_path,
            )
            raise GenerationFailure(err)

        if not file_is_usable(output_clip_path, min_bytes=1024):
            raise GenerationFailure(
                f"Scene {scene_num} Clip {clip_num}: Grok output file is missing or zero-length."
            )

        # Context for the next clip is the local path (used for last-frame continuity)
        return output_clip_path, output_clip_path

    def generate_veo_clip(self, scene_num: int, clip: Dict[str, Any], previous_context_id: Any = None) -> tuple:
        """Executes Google GenAI Veo 3.1 SDK requests using model paths tracking."""
        clip_num = clip["clip_number"]
        prompt = clip["visual_prompt"]
        neg_prompt = clip["negative_prompt"]
        continuation_source = clip.get("veo_continuation_source", "none")

        output_clip_path = clip_output_path(scene_num, clip_num)
        ensure_parent_dir(output_clip_path)
        os.makedirs("assets/video", exist_ok=True)

        if file_is_usable(output_clip_path, min_bytes=1024):
            print(f"  [Veo 3.1] Reusing existing Clip {clip_num}: {output_clip_path}")
            ctx = self.state.get("clip_context_ids", {}).get(
                self._clip_job_key(scene_num, clip_num), output_clip_path
            )
            return output_clip_path, ctx

        print(f"  [Veo 3.1] Generating Clip {clip_num} (Source: {continuation_source})...")

        if not self.client:
            raise GenerationFailure(f"Scene {scene_num} Clip {clip_num}: Google GenAI client is not initialized.")

        if types is None:
            raise GenerationFailure(f"Scene {scene_num} Clip {clip_num}: google.genai.types is unavailable.")

        throttle_delay = int(os.environ.get("VEO_THROTTLE_DELAY", "30"))
        if throttle_delay > 0:
            print(f"  [Veo 3.1] Throttling: Cooling down for {throttle_delay}s...")
            time.sleep(throttle_delay)

        model_name = self.config.get("model_name", "veo-3.1-fast-generate-preview")

        generation_config = types.GenerateVideosConfig(
            aspect_ratio=self.config.get("aspect_ratio", "16:9"),
            duration_seconds=resolve_default_duration(
                self.config,
                provider=self.config.get("video_provider"),
                model_name=model_name,
                resolution=self.config.get("resolution"),
            ),
        )

        if self.config.get("use_video_audio_for_music", False):
            if "with audio" not in prompt.lower():
                prompt = f"{prompt}, high quality atmospheric stereo background cinematic sound and matching environment audio"

        try:
            # 🎬 IF A CAMERA JUMP CUT HAPPENS, ATTACH STAGE 0 PORTRAIT AS BASELINE LOCK
            if continuation_source == "none" or not previous_context_id or str(previous_context_id).startswith("ctx_mock_"):
                image_reference = None
                
                # Scan prompt context to verify which primary character token is under focus
                char_seeds = self.blueprint.get("global_production_variables", {}).get("character_seed_tokens", {})
                for char_key, seed_info in char_seeds.items():
                    if char_key in prompt:
                        local_image_name = seed_info.get("reference_image_placeholder", f"{char_key.lower()}_ref.png")
                        local_image_path = f"assets/characters/{local_image_name}"
                        
                        if os.path.exists(local_image_path):
                            print(f"  [Character Anchor] Found upfront character map image for {char_key}. Injecting into canvas input layer...")
                            image_reference = self.client.files.upload(file=local_image_path)
                            break

                if image_reference:
                    operation = self.client.models.generate_videos(
                        model=model_name,
                        prompt=prompt,
                        video=image_reference, # Forces face/outfit alignment consistency acrosscuts
                        config=generation_config
                    )
                else:
                    operation = self.client.models.generate_videos(
                        model=model_name,
                        prompt=prompt,
                        config=generation_config
                    )
            
            # 🔄 CONTINUOUS EXTENSION MODE
            else:
                operation = self.client.models.generate_videos(
                    model=model_name,
                    prompt=prompt,
                    video=previous_context_id,
                    config=generation_config 
                )

            poll_interval = int(os.environ.get("VEO_POLL_INTERVAL_SECONDS", "10"))
            while not getattr(operation, "done", False):
                time.sleep(poll_interval)
                operation = self.client.operations.get(operation)

            if getattr(operation, "error", None):
                raise GenerationFailure(f"Scene {scene_num} Clip {clip_num}: Veo 3.1 operation error: {operation.error}")

            generated_videos = getattr(getattr(operation, "response", None), "generated_videos", None)
            if not generated_videos:
                raise GenerationFailure(f"Scene {scene_num} Clip {clip_num}: Veo 3.1 returned no generated video.")

            self.client.files.download(file=generated_videos[0].video)
            generated_videos[0].video.save(output_clip_path)
            
            context_id = getattr(operation, "name", f"ctx_s{scene_num}_c{clip_num}")

        except GenerationFailure:
            raise
        except Exception as e:
            raise GenerationFailure(f"Scene {scene_num} Clip {clip_num}: Veo 3.1 generation call failed: {e}")

        if not os.path.exists(output_clip_path) or os.path.getsize(output_clip_path) == 0:
            raise GenerationFailure(f"Scene {scene_num} Clip {clip_num}: output file is missing or zero-length.")

        return output_clip_path, context_id

    def resolve_video_settings(
        self,
        scene: Optional[Dict[str, Any]] = None,
        provider: Optional[str] = None,
        model_name: Optional[str] = None,
    ) -> Dict[str, str]:
        """
        Resolve provider/model: explicit args > scene fields > pipeline_config.
        Scene may set video_provider / model_name for preferred generator.
        """
        cfg_provider = str(self.config.get("video_provider", "grok")).lower().strip()
        cfg_model = str(self.config.get("model_name", "grok-imagine-video")).strip()
        scene_provider = ""
        scene_model = ""
        if scene:
            scene_provider = str(scene.get("video_provider") or "").lower().strip()
            scene_model = str(scene.get("model_name") or "").strip()
        resolved_provider = (provider or scene_provider or cfg_provider).lower().strip()
        resolved_model = model_name or scene_model or cfg_model
        return {
            "provider": resolved_provider,
            "model_name": resolved_model,
            "variant_id": slugify_model_id(resolved_provider, resolved_model),
        }

    def generate_video_clip(
        self,
        scene_num: int,
        clip: Dict[str, Any],
        previous_context_id: Any = None,
        force_regenerate: bool = False,
        video_provider: Optional[str] = None,
        model_name: Optional[str] = None,
        output_path: Optional[str] = None,
        job_key: Optional[str] = None,
        skip_stale_check: bool = False,
    ) -> tuple:
        """Dispatch video generation to Grok (default) or Veo."""
        scene = None
        for s in self.blueprint.get("scenes", []):
            if s.get("scene_number") == scene_num:
                scene = s
                break
        settings = self.resolve_video_settings(scene, video_provider, model_name)
        provider = settings["provider"]
        resolved_model = settings["model_name"]
        # Fail fast before paid API / long polls
        if provider in ("grok", "xai", "grok-imagine", "imagine"):
            require_environment(self.config, require_xai=True, require_ffmpeg=True)
        elif provider in ("veo", "google", "gemini"):
            require_environment(self.config, require_gemini=True, require_ffmpeg=True)
        if provider in ("grok", "xai", "grok-imagine", "imagine"):
            return self.generate_grok_clip(
                scene_num,
                clip,
                previous_context_id,
                force_regenerate=force_regenerate,
                model_name=resolved_model,
                output_path=output_path,
                job_key=job_key,
                skip_stale_check=skip_stale_check,
            )
        if provider in ("veo", "google", "gemini"):
            # Veo path currently always writes main clip path; copy if variant requested
            path, ctx = self.generate_veo_clip(scene_num, clip, previous_context_id)
            if output_path and path and os.path.abspath(path) != os.path.abspath(output_path):
                ensure_parent_dir(output_path)
                import shutil

                shutil.copy2(path, output_path)
                return output_path, ctx
            return path, ctx
        raise GenerationFailure(
            f"Unknown video_provider '{provider}'. Use 'grok' (default) or 'veo'."
        )

    # ------------------------------------------------------------------
    # Multi-model scene variants (side-by-side comparison)
    # ------------------------------------------------------------------

    def available_video_models(self) -> List[Dict[str, str]]:
        models = self.config.get("available_video_models")
        if isinstance(models, list) and models:
            return models
        return list(DEFAULT_CONFIG.get("available_video_models") or [])

    def set_scene_video_settings(
        self,
        scene_num: int,
        provider: Optional[str] = None,
        model_name: Optional[str] = None,
        clear: bool = False,
    ) -> Dict[str, Any]:
        """Persist preferred generator on the scene in the active blueprint."""
        for scene in self.blueprint.get("scenes", []):
            if scene.get("scene_number") != scene_num:
                continue
            if clear:
                scene.pop("video_provider", None)
                scene.pop("model_name", None)
            else:
                if provider is not None:
                    scene["video_provider"] = provider
                if model_name is not None:
                    scene["model_name"] = model_name
            self.save_blueprint_to_disk()
            return self.resolve_video_settings(scene)
        raise GenerationFailure(f"Scene {scene_num} not found")

    def _register_scene_variant(
        self,
        scene_num: int,
        variant_id: str,
        meta: Dict[str, Any],
        set_active: bool = False,
    ) -> None:
        store = self.state.setdefault("scene_variants", {})
        entry = store.setdefault(str(scene_num), {"active": "main", "variants": {}})
        variants = entry.setdefault("variants", {})
        variants[variant_id] = {
            **(variants.get(variant_id) or {}),
            **meta,
            "variant_id": variant_id,
            "updated_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        }
        if set_active:
            entry["active"] = variant_id
        self.save_state()

    def snapshot_main_as_variant(self, scene_num: int) -> Optional[str]:
        """
        Copy current main scene clips + composite into a variant folder tagged
        with the scene's current resolved provider/model (if not already present).
        Returns variant_id or None if nothing to snapshot.
        """
        import shutil

        scene = None
        for s in self.blueprint.get("scenes", []):
            if s.get("scene_number") == scene_num:
                scene = s
                break
        if not scene:
            return None
        settings = self.resolve_video_settings(scene)
        variant_id = f"main__{settings['variant_id']}"
        composite = composite_output_path(scene_num)
        if not file_is_usable(composite, min_bytes=1024):
            # still snapshot individual clips if any
            has_any = False
            for c in scene.get("veo_clips") or []:
                if file_is_usable(clip_output_path(scene_num, int(c.get("clip_number", 0))), min_bytes=1024):
                    has_any = True
                    break
            if not has_any:
                return None

        vdir = variant_dir(scene_num, variant_id)
        os.makedirs(vdir, exist_ok=True)
        clip_paths: List[str] = []
        for c in scene.get("veo_clips") or []:
            cn = int(c.get("clip_number", 0))
            src = clip_output_path(scene_num, cn)
            if file_is_usable(src, min_bytes=1024):
                dst = variant_clip_path(scene_num, cn, variant_id)
                ensure_parent_dir(dst)
                shutil.copy2(src, dst)
                clip_paths.append(dst)
        comp_dst = variant_composite_path(scene_num, variant_id)
        if file_is_usable(composite, min_bytes=1024):
            shutil.copy2(composite, comp_dst)
        elif clip_paths:
            try:
                self.mix_scene_assets(scene_num, clip_paths, None, force=True)
                # mix writes to main composite path — copy then leave main as-is
                if file_is_usable(composite, min_bytes=1024):
                    shutil.copy2(composite, comp_dst)
            except Exception as e:
                print(f"  [Variant] Could not mux snapshot: {e}")

        meta = {
            "provider": settings["provider"],
            "model_name": settings["model_name"],
            "label": f"Main snapshot ({settings['provider']}/{settings['model_name']})",
            "source": "main_snapshot",
            "composite_path": comp_dst if file_is_usable(comp_dst, min_bytes=1024) else None,
            "clip_count": len(clip_paths),
        }
        try:
            with open(variant_meta_path(scene_num, variant_id), "w", encoding="utf-8") as f:
                json.dump(meta, f, indent=2)
        except OSError:
            pass
        self._register_scene_variant(scene_num, variant_id, meta, set_active=False)
        print(f"  [Variant] Snapshotted main → {variant_id} ({len(clip_paths)} clips)")
        return variant_id

    def generate_scene_variant(
        self,
        scene_num: int,
        provider: str,
        model_name: str,
        *,
        only_existing: bool = True,
        run_qa: bool = False,
        label: Optional[str] = None,
    ) -> Dict[str, Any]:
        """
        Generate (or re-generate) all relevant clips for a scene into a variant
        folder so it can be compared without overwriting the main timeline.
        """
        import shutil

        scene = None
        for s in self.blueprint.get("scenes", []):
            if s.get("scene_number") == scene_num:
                scene = s
                break
        if not scene:
            raise GenerationFailure(f"Scene {scene_num} not found")

        # Preserve current main before expensive alternate render
        self.snapshot_main_as_variant(scene_num)

        settings = self.resolve_video_settings(scene, provider, model_name)
        variant_id = settings["variant_id"]
        provider = settings["provider"]
        model_name = settings["model_name"]
        vdir = variant_dir(scene_num, variant_id)
        os.makedirs(vdir, exist_ok=True)

        clips = scene.get("veo_clips") or []
        generated: List[str] = []
        previous_path = None
        for clip in clips:
            cn = int(clip.get("clip_number", 0))
            main_path = clip_output_path(scene_num, cn)
            if only_existing and not file_is_usable(main_path, min_bytes=1024):
                print(f"  [Variant] Skip clip {cn} (not on main disk)")
                previous_path = None
                continue
            out = variant_clip_path(scene_num, cn, variant_id)
            job_key = f"{scene_num}_{cn}__{variant_id}"
            print(
                f"  [Variant] Scene {scene_num} Clip {cn} → {variant_id} "
                f"({provider}/{model_name})"
            )
            path, _ctx = self.generate_video_clip(
                scene_num,
                clip,
                previous_context_id=previous_path,
                force_regenerate=True,
                video_provider=provider,
                model_name=model_name,
                output_path=out,
                job_key=job_key,
                skip_stale_check=True,
            )
            if run_qa and path:
                try:
                    self.run_clip_qa(path, clip.get("visual_prompt") or "")
                except Exception as e:
                    print(f"  [Variant QA Warning] {e}")
            if file_is_usable(path, min_bytes=1024):
                generated.append(path)
                previous_path = path
            else:
                previous_path = None

        comp = variant_composite_path(scene_num, variant_id)
        if generated:
            try:
                # mix_scene_assets always writes main composite — mux into variant path
                tmp_main = composite_output_path(scene_num)
                main_backup = None
                if file_is_usable(tmp_main, min_bytes=1024):
                    main_backup = f"{tmp_main}.bak_variant"
                    shutil.copy2(tmp_main, main_backup)
                self.mix_scene_assets(scene_num, generated, None, force=True)
                if file_is_usable(tmp_main, min_bytes=1024):
                    ensure_parent_dir(comp)
                    shutil.copy2(tmp_main, comp)
                if main_backup and file_is_usable(main_backup, min_bytes=1024):
                    shutil.copy2(main_backup, tmp_main)
                    try:
                        os.remove(main_backup)
                    except OSError:
                        pass
            except Exception as e:
                print(f"  [Variant] Composite mux failed: {e}")

        meta = {
            "provider": provider,
            "model_name": model_name,
            "label": label or f"{provider} / {model_name}",
            "source": "generated",
            "composite_path": comp if file_is_usable(comp, min_bytes=1024) else None,
            "clip_count": len(generated),
            "clip_paths": generated,
            "only_existing": only_existing,
        }
        try:
            with open(variant_meta_path(scene_num, variant_id), "w", encoding="utf-8") as f:
                json.dump(meta, f, indent=2)
        except OSError:
            pass
        self._register_scene_variant(scene_num, variant_id, meta, set_active=False)
        return meta

    def list_scene_variants(self, scene_num: int) -> Dict[str, Any]:
        """Return active id + known variants (state + filesystem scan)."""
        store = self.state.setdefault("scene_variants", {})
        entry = store.setdefault(str(scene_num), {"active": "main", "variants": {}})
        variants = dict(entry.get("variants") or {})

        # Always include main timeline as virtual variant
        main_comp = composite_output_path(scene_num)
        scene = None
        for s in self.blueprint.get("scenes", []):
            if s.get("scene_number") == scene_num:
                scene = s
                break
        settings = self.resolve_video_settings(scene)
        variants["main"] = {
            "variant_id": "main",
            "provider": settings["provider"],
            "model_name": settings["model_name"],
            "label": f"Main ({settings['provider']}/{settings['model_name']})",
            "source": "main",
            "composite_path": main_comp if file_is_usable(main_comp, min_bytes=1024) else None,
            "is_main": True,
        }

        # Discover on-disk variant folders
        root = f"assets/variants/scene_{scene_num:02d}"
        if os.path.isdir(root):
            for name in os.listdir(root):
                if name in variants:
                    # refresh composite path
                    comp = variant_composite_path(scene_num, name)
                    if file_is_usable(comp, min_bytes=1024):
                        variants[name]["composite_path"] = comp
                    continue
                comp = variant_composite_path(scene_num, name)
                meta_file = variant_meta_path(scene_num, name)
                meta: Dict[str, Any] = {"variant_id": name, "label": name, "source": "disk"}
                if os.path.isfile(meta_file):
                    try:
                        with open(meta_file, "r", encoding="utf-8") as f:
                            meta.update(json.load(f))
                    except (json.JSONDecodeError, OSError):
                        pass
                if file_is_usable(comp, min_bytes=1024):
                    meta["composite_path"] = comp
                variants[name] = meta

        return {
            "scene_number": scene_num,
            "active": entry.get("active") or "main",
            "preferred": settings,
            "variants": variants,
        }

    def promote_scene_variant(self, scene_num: int, variant_id: str) -> str:
        """
        Copy a comparison variant into the main timeline paths and set scene
        preferred provider/model to match. Returns main composite path.
        """
        import shutil

        if variant_id == "main":
            return composite_output_path(scene_num)

        info = self.list_scene_variants(scene_num)
        meta = (info.get("variants") or {}).get(variant_id)
        if not meta:
            raise GenerationFailure(f"Unknown variant '{variant_id}' for scene {scene_num}")

        # Snapshot current main first
        self.snapshot_main_as_variant(scene_num)

        scene = None
        for s in self.blueprint.get("scenes", []):
            if s.get("scene_number") == scene_num:
                scene = s
                break
        if not scene:
            raise GenerationFailure(f"Scene {scene_num} not found")

        for c in scene.get("veo_clips") or []:
            cn = int(c.get("clip_number", 0))
            src = variant_clip_path(scene_num, cn, variant_id)
            if file_is_usable(src, min_bytes=1024):
                dst = clip_output_path(scene_num, cn)
                ensure_parent_dir(dst)
                shutil.copy2(src, dst)
                self.clear_clip_stale(scene_num, cn)

        src_comp = variant_composite_path(scene_num, variant_id)
        dst_comp = composite_output_path(scene_num)
        if file_is_usable(src_comp, min_bytes=1024):
            ensure_parent_dir(dst_comp)
            shutil.copy2(src_comp, dst_comp)
        else:
            self.remux_scene_from_disk(scene_num)

        provider = meta.get("provider")
        model_name = meta.get("model_name")
        if provider or model_name:
            self.set_scene_video_settings(
                scene_num,
                provider=provider,
                model_name=model_name,
            )

        self._register_scene_variant(
            scene_num,
            variant_id,
            {**meta, "promoted_at": time.strftime("%Y-%m-%dT%H:%M:%S")},
            set_active=True,
        )
        store = self.state.setdefault("scene_variants", {})
        store.setdefault(str(scene_num), {})["active"] = "main"
        self.save_state()
        print(f"  [Variant] Promoted {variant_id} → main for Scene {scene_num}")
        return dst_comp

    def _qa_evaluation_prompt(self, visual_prompt: str) -> str:
        return (
            f"You are a film continuity QA reviewer. These still frames were sampled in order "
            f"from a generated ~8s AI video clip. Intended description:\n"
            f"'{visual_prompt}'\n\n"
            f"Approve if the PRIMARY subject and PRIMARY action are roughly correct "
            f"(right person, place, wardrobe, and some visible motion matching the main beat). "
            f"Do NOT reject for missing secondary micro-actions (e.g. head shakes, eye scanning, "
            f"exact plate weights, perfect smirk consistency) if the main beat is clear. "
            f"Still reject if wrong location/character, frozen with no intended motion, or severe identity/wardrobe breaks.\n\n"
            f"Respond with ONLY a single JSON object (no markdown fences):\n"
            f"{{\n"
            f"  \"approved\": true or false,\n"
            f"  \"critique\": \"brief notes on primary action match, lighting, identity\"\n"
            f"}}"
        )

    def _probe_video_duration_seconds(self, video_path: str) -> float:
        cmd = [
            "ffprobe", "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            video_path,
        ]
        try:
            result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            if result.returncode == 0 and result.stdout.strip():
                return max(0.1, float(result.stdout.strip()))
        except Exception:
            pass
        # Fallback when ffprobe is unavailable
        return float(resolve_default_duration(self.config))

    def _extract_qa_frames(self, video_path: str, frame_count: int = 4) -> List[str]:
        """Sample evenly spaced JPEG frames from a clip for vision QA."""
        os.makedirs("assets/video", exist_ok=True)
        base = os.path.splitext(os.path.basename(video_path))[0]
        duration = self._probe_video_duration_seconds(video_path)
        frame_count = max(1, min(int(frame_count), 8))

        # Avoid the absolute end frame which can be black/corrupt on some encodes
        usable = max(0.05, duration - 0.05)
        if frame_count == 1:
            timestamps = [usable * 0.5]
        else:
            timestamps = [usable * (i / (frame_count - 1)) for i in range(frame_count)]

        frame_paths: List[str] = []
        for idx, ts in enumerate(timestamps, start=1):
            out_path = f"assets/video/{base}_qa_frame_{idx:02d}.jpg"
            ensure_parent_dir(out_path)
            ffmpeg = resolve_ffmpeg()
            cmd = [
                ffmpeg, "-y",
                "-ss", f"{ts:.3f}",
                "-i", video_path,
                "-frames:v", "1",
                "-q:v", "2",
                out_path,
            ]
            result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            if result.returncode == 0 and os.path.exists(out_path) and os.path.getsize(out_path) > 0:
                frame_paths.append(out_path)
            else:
                print(f"  [QA Warning] Failed to extract frame {idx} at t={ts:.2f}s")

        if not frame_paths:
            raise GenerationFailure(f"Could not extract any QA frames from '{video_path}'")
        return frame_paths

    def _parse_qa_json(self, text: str) -> Dict[str, Any]:
        """Parse model QA JSON, tolerating optional markdown fences."""
        cleaned = (text or "").strip()
        if cleaned.startswith("```"):
            lines = cleaned.splitlines()
            # Drop opening fence and optional trailing fence
            if lines and lines[0].startswith("```"):
                lines = lines[1:]
            if lines and lines[-1].strip().startswith("```"):
                lines = lines[:-1]
            cleaned = "\n".join(lines).strip()

        try:
            return json.loads(cleaned)
        except json.JSONDecodeError:
            # Best-effort extract of first {...} block
            start = cleaned.find("{")
            end = cleaned.rfind("}")
            if start >= 0 and end > start:
                return json.loads(cleaned[start:end + 1])
            raise

    def _extract_response_text(self, result: Dict[str, Any]) -> str:
        """Pull assistant text out of xAI /v1/responses or chat.completions payloads."""
        if not isinstance(result, dict):
            return str(result)

        # chat.completions shape
        choices = result.get("choices")
        if isinstance(choices, list) and choices:
            message = choices[0].get("message") or {}
            content = message.get("content")
            if isinstance(content, str):
                return content
            if isinstance(content, list):
                parts = []
                for part in content:
                    if isinstance(part, dict) and part.get("type") in ("text", "output_text"):
                        parts.append(part.get("text") or part.get("output_text") or "")
                if parts:
                    return "\n".join(parts)

        # responses API shape: output[].content[].text
        if isinstance(result.get("output_text"), str) and result["output_text"].strip():
            return result["output_text"]

        output = result.get("output")
        if isinstance(output, list):
            texts: List[str] = []
            for item in output:
                if not isinstance(item, dict):
                    continue
                content = item.get("content")
                if isinstance(content, list):
                    for part in content:
                        if not isinstance(part, dict):
                            continue
                        if part.get("type") in ("output_text", "text") and part.get("text"):
                            texts.append(part["text"])
                elif isinstance(content, str):
                    texts.append(content)
            if texts:
                return "\n".join(texts)

        return json.dumps(result)

    def run_grok_qa(self, video_path: str, visual_prompt: str) -> bool:
        """Critique a generated clip with Grok vision using sampled frames."""
        require_environment(self.config, require_xai=True, require_gemini=False, require_ffmpeg=True)

        print(f"  [Grok QA] Critiquing generated clip: {video_path}...")
        frame_paths: List[str] = []
        try:
            frame_count = int(self.config.get("qa_frame_count", 4))
            frame_paths = self._extract_qa_frames(video_path, frame_count=frame_count)
            print(f"  [Grok QA] Sampled {len(frame_paths)} frame(s) for vision review.")

            content: List[Dict[str, Any]] = []
            for path in frame_paths:
                content.append({
                    "type": "input_image",
                    "image_url": self._file_to_data_uri(path),
                    "detail": "high",
                })
            content.append({
                "type": "input_text",
                "text": self._qa_evaluation_prompt(visual_prompt),
            })

            model_name = self.config.get("qa_model_name", "grok-4.5")
            payload = {
                "model": model_name,
                "input": [
                    {
                        "role": "user",
                        "content": content,
                    }
                ],
            }
            result = self._grok_request("POST", f"{XAI_API_BASE}/responses", payload, timeout=180)
            text = self._extract_response_text(result)
            parsed = self._parse_qa_json(text)
            approved = bool(parsed.get("approved", True))
            print(f"  [Grok QA] Result: Approved={approved}, Critique={parsed.get('critique')}")
            return approved
        except Exception as e:
            print(f"  [Grok QA Warning] Evaluation failed: {e}. Bypassing safely.")
            return True
        finally:
            for p in frame_paths:
                try:
                    if os.path.exists(p):
                        os.remove(p)
                except OSError:
                    pass

    def run_gemini_qa(self, video_path: str, visual_prompt: str) -> bool:
        """Critique a generated clip with Gemini multimodal video understanding."""
        if not self.client:
            print("  [Gemini QA Warning] Gemini client not configured. Bypassing QA safely.")
            return True

        print(f"  [Gemini QA] Critiquing generated clip: {video_path}...")
        try:
            video_file = self.client.files.upload(file=video_path)

            while video_file.state.name == "PROCESSING":
                time.sleep(2)
                video_file = self.client.files.get(name=video_file.name)

            if video_file.state.name != "ACTIVE":
                raise ValueError(f"File upload entered state: {video_file.state.name}")

            gemini_model = "gemini-2.5-flash"
            configured_qa_model = str(self.config.get("qa_model_name", ""))
            if "gemini" in configured_qa_model.lower():
                gemini_model = configured_qa_model

            response = self.client.models.generate_content(
                model=gemini_model,
                contents=[video_file, self._qa_evaluation_prompt(visual_prompt)],
                config=types.GenerateContentConfig(
                    response_mime_type="application/json"
                ) if types else None,
            )

            result = json.loads(response.text)
            print(f"  [Gemini QA] Result: Approved={result.get('approved')}, Critique={result.get('critique')}")
            return result.get("approved", True)
        except Exception as e:
            print(f"  [Gemini QA Warning] Evaluation failed: {e}. Bypassing safely.")
            return True

    def run_clip_qa(self, video_path: str, visual_prompt: str) -> bool:
        """Dispatch clip QA to Grok (default) or Gemini based on pipeline_config.qa_provider."""
        provider = str(self.config.get("qa_provider", "grok")).lower().strip()
        if provider in ("grok", "xai"):
            return self.run_grok_qa(video_path, visual_prompt)
        if provider in ("gemini", "google"):
            return self.run_gemini_qa(video_path, visual_prompt)
        if provider in ("none", "off", "skip"):
            print("  [QA] Skipped by config (qa_provider=none).")
            return True
        print(f"  [QA Warning] Unknown qa_provider '{provider}'. Bypassing safely.")
        return True

    def _normalize_clip_for_concat(self, clip_path: str, normalized_path: str) -> str:
        """
        Re-encode a clip to H.264 + AAC so concat is reliable and silent clips get a silent track.
        Always boosts audio so soft Grok beds are still audible in the composite.
        """
        ensure_parent_dir(normalized_path)
        ffmpeg = resolve_ffmpeg()
        has_audio = self._video_has_audio(clip_path)
        # Loudness boost for soft ambient / quiet native beds
        gain_db = float(self.config.get("composite_audio_gain_db", 9.0))

        if has_audio:
            cmd = [
                ffmpeg, "-y",
                "-i", clip_path,
                "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=24,format=yuv420p",
                "-af", f"aresample=48000,aformat=channel_layouts=stereo,volume={gain_db}dB",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-c:a", "aac", "-b:a", "192k",
                "-ar", "48000", "-ac", "2",
                "-movflags", "+faststart",
                normalized_path,
            ]
        else:
            # Attach silent stereo audio so progressive concat never drops the track
            print(f"  [FFmpeg] Clip has no audio — adding silent track for mux: {clip_path}")
            cmd = [
                ffmpeg, "-y",
                "-i", clip_path,
                "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
                "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=24,format=yuv420p",
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-c:a", "aac", "-b:a", "192k",
                "-shortest",
                "-ar", "48000", "-ac", "2",
                "-movflags", "+faststart",
                normalized_path,
            ]

        result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if result.returncode != 0 or not file_is_usable(normalized_path, min_bytes=1024):
            stderr_tail = result.stderr.decode(errors="ignore")[-500:]
            raise GenerationFailure(
                f"Failed to normalize clip '{clip_path}' for scene concat. FFmpeg: {stderr_tail}"
            )
        if not self._video_has_audio(normalized_path):
            raise GenerationFailure(
                f"Normalized clip lost audio track: '{normalized_path}' from '{clip_path}'"
            )
        return normalized_path

    def mix_scene_assets(self, scene_num: int, clip_paths: List[str], music_path: Optional[str],
                         force: bool = False) -> str:
        """Stitches clips (and optional music bed) into the scene composite using FFmpeg."""
        output_scene_path = composite_output_path(scene_num)
        ensure_parent_dir(output_scene_path)
        os.makedirs("assets/scenes", exist_ok=True)

        # Only reuse a finished composite when not force-rebuilding (progressive updates use force=True)
        if (
            not force
            and file_is_usable(output_scene_path, min_bytes=1024)
            and self._video_has_audio(output_scene_path)
        ):
            print(f"  [FFmpeg] Reusing existing Scene {scene_num} composite: {output_scene_path}")
            return output_scene_path

        for p in clip_paths:
            if not file_is_usable(p, min_bytes=1024):
                raise GenerationFailure(
                    f"Scene {scene_num}: cannot mux — missing or empty clip '{p}'. "
                    f"Re-run the script to resume generation."
                )

        # Ensure dialogue TTS is on source clips before stitching (Grok audio is often ambient-only)
        scene_obj = None
        for s in self.blueprint.get("scenes", []):
            if s.get("scene_number") == scene_num:
                scene_obj = s
                break
        if scene_obj and self.config.get("ensure_dialogue_audio", True):
            clips_by_num = {
                c.get("clip_number"): c for c in (scene_obj.get("veo_clips") or [])
            }
            for p in clip_paths:
                # path like assets/video/scene_01_clip_03.mp4
                m = re.search(r"clip_(\d+)\.mp4$", p.replace("\\", "/"))
                if not m:
                    continue
                c_num = int(m.group(1))
                clip_meta = clips_by_num.get(c_num)
                if not clip_meta:
                    continue
                dlg = str(((clip_meta.get("audio_payload") or {}).get("dialogue") or "")).strip()
                if dlg:
                    job = self.state.get("clip_jobs", {}).get(self._clip_job_key(scene_num, c_num), {})
                    if not job.get("dialogue_audio_ensured"):
                        print(f"  [Audio] Ensuring dialogue on source clip before composite: {p}")
                        self._ensure_dialogue_audio(p, clip_meta)
                        self._update_clip_job(
                            scene_num, c_num,
                            path=p,
                            dialogue_audio_ensured=True,
                            has_audio=True,
                        )

        ffmpeg = resolve_ffmpeg()
        work_dir = f"assets/scenes/_work_scene_{scene_num:02d}"
        os.makedirs(work_dir, exist_ok=True)

        # Normalize each clip so concat keeps a continuous, loud enough audio track
        normalized_paths: List[str] = []
        for idx, p in enumerate(clip_paths, start=1):
            norm = os.path.join(work_dir, f"norm_clip_{idx:02d}.mp4")
            # Always rebuild norms on force so audio fixes apply
            if force and os.path.exists(norm):
                try:
                    os.remove(norm)
                except OSError:
                    pass
            self._normalize_clip_for_concat(p, norm)
            normalized_paths.append(norm)

        concat_list = f"assets/scenes/concat_list_scene_{scene_num}.txt"
        ensure_parent_dir(concat_list)
        with open(concat_list, "w", encoding="utf-8") as f:
            for p in normalized_paths:
                # Paths relative to concat list location (assets/scenes/)
                rel = os.path.relpath(p, start="assets/scenes").replace("\\", "/")
                f.write(f"file '{rel}'\n")

        tmp_out = f"{output_scene_path}.tmp.mp4"
        # Full re-encode (not stream-copy) so audio is never dropped by player/demux quirks
        if music_path and file_is_usable(music_path, min_bytes=1):
            ffmpeg_cmd = [
                ffmpeg, "-y",
                "-f", "concat", "-safe", "0", "-i", concat_list,
                "-i", music_path,
                "-filter_complex",
                "[0:a]aformat=sample_rates=48000:channel_layouts=stereo[va];"
                "[1:a]aformat=sample_rates=48000:channel_layouts=stereo,volume=0.25[m];"
                "[va][m]amix=inputs=2:duration=first:dropout_transition=2:normalize=0,"
                "loudnorm=I=-14:TP=-1.5:LRA=11[aout]",
                "-map", "0:v:0",
                "-map", "[aout]",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-c:a", "aac", "-b:a", "192k",
                "-ar", "48000", "-ac", "2",
                "-shortest",
                "-movflags", "+faststart",
                tmp_out,
            ]
        else:
            ffmpeg_cmd = [
                ffmpeg, "-y",
                "-f", "concat", "-safe", "0", "-i", concat_list,
                "-map", "0:v:0",
                "-map", "0:a:0?",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-c:a", "aac", "-b:a", "192k",
                "-af", "loudnorm=I=-14:TP=-1.5:LRA=11",
                "-ar", "48000", "-ac", "2",
                "-movflags", "+faststart",
                tmp_out,
            ]

        print(
            f"  [FFmpeg] Muxing Scene {scene_num} composite "
            f"({len(clip_paths)} clip(s)){' [force rebuild]' if force else ''}..."
        )
        try:
            result = subprocess.run(ffmpeg_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        except Exception as e:
            raise GenerationFailure(f"Scene {scene_num}: local FFmpeg execution error while muxing: {e}")

        if result.returncode != 0 or not file_is_usable(tmp_out, min_bytes=1024):
            stderr_tail = result.stderr.decode(errors="ignore")[-500:]
            raise GenerationFailure(f"Scene {scene_num}: FFmpeg muxing failed. Last FFmpeg output: {stderr_tail}")

        if not self._video_has_audio(tmp_out):
            # Fail loudly — silent composites are almost always a mux bug
            try:
                os.remove(tmp_out)
            except OSError:
                pass
            raise GenerationFailure(
                f"Scene {scene_num}: composite was written without an audio track. "
                f"Check source clips have audio and ffmpeg is working."
            )

        os.replace(tmp_out, output_scene_path)
        has_audio = self._video_has_audio(output_scene_path)
        audio_stats = self._mp4_audio_stats(output_scene_path)
        print(
            f"  [FFmpeg] Scene composite ready: {output_scene_path} "
            f"(clips={len(clip_paths)}, audio={has_audio}, "
            f"audio_bytes={audio_stats.get('audio_bytes', 0)})"
        )
        return output_scene_path

    def _remove_zero_length_files(self, paths: List[str]) -> List[str]:
        removed = []
        for p in paths:
            try:
                if p and os.path.exists(p) and os.path.getsize(p) == 0:
                    os.remove(p)
                    removed.append(p)
            except OSError as e:
                print(f"  [Cleanup Warning] Could not remove zero-length file '{p}': {e}")
        return removed

    def _handle_generation_failure(self, scene_num: int, generated_files: List[str],
                                   error: GenerationFailure, clip_paths: Optional[List[str]] = None,
                                   music_path: Optional[str] = None):
        print("\n" + "=" * 75)
        print(f"[FATAL] Generation failed for Scene {scene_num}. Halting pipeline.")
        print(f"[FATAL] Reason: {error}")
        print("[RESUME] Progress was saved. Re-run the script to continue from this point.")
        print("         Existing usable clips will be skipped; pending downloads will be retried.")
        print("=" * 75)

        removed = self._remove_zero_length_files(generated_files)
        if removed:
            print(f"[Cleanup] Removed {len(removed)} zero-length file(s).")

        s_key = str(scene_num)
        self.state["scenes_completed"][s_key] = False
        # Keep partial scene_assets so resume knows what was finished
        partial_clips = [p for p in (clip_paths or []) if file_is_usable(p, min_bytes=1024)]
        self.state["scene_assets"][s_key] = {
            "video_clips": partial_clips,
            "music_bed": music_path if file_is_usable(music_path, min_bytes=1) else None,
            "composite": None,
            "partial": True,
            "last_error": str(error),
        }
        self.save_state()
        sys.exit(1)

    def process_scene(self, scene: Dict[str, Any], clip_plan: Optional[Dict[str, Any]] = None) -> bool:
        """
        Generate clips for a scene and mux the composite.

        clip_plan (optional):
          {
            "targets": set[int] | None,   # clip numbers to generate; None = all
            "wipe": set[int],             # wipe these before generating
          }
        Non-target clips are reused from disk when present and included in the composite.
        """
        s_num = scene["scene_number"]
        all_clips = scene.get("veo_clips") or []
        targets = None if not clip_plan else clip_plan.get("targets")
        wipe_set = set((clip_plan or {}).get("wipe") or [])

        if targets is not None:
            target_label = ",".join(str(n) for n in sorted(targets))
            print(f"\n==================== PROCESSING SCENE {s_num} (clips: {target_label}) ====================")
        else:
            print(f"\n==================== PROCESSING SCENE {s_num} ====================")

        self.ensure_asset_directories()
        self._active_scene_num = s_num
        self._active_clip_num = None
        self._check_shutdown(f"start Scene {s_num}")

        generated_files: List[str] = []
        clip_paths: List[str] = []
        music_path: Optional[str] = None
        composite_scene_path: Optional[str] = None

        try:
            if not self.config.get("use_video_audio_for_music", False):
                generated_files.append(music_output_path(s_num))
                music_path = self.generate_suno_music(scene)
            else:
                print(f"  [Pipeline Config] Bypassing Suno API Generation. Utilizing native video generator background music.")

            previous_context_id = None
            max_qa_retries = int(self.config.get("qa_max_retries", 2))
            qa_retry_on_fail = bool(self.config.get("qa_retry_on_fail", True))

            for clip in all_clips:
                self._check_shutdown(f"Scene {s_num} before next clip")
                c_num = clip["clip_number"]
                self._active_clip_num = c_num
                clip_state_key = f"{s_num}_{c_num}"
                out_path = clip_output_path(s_num, c_num)
                generated_files.append(out_path)

                should_generate = targets is None or c_num in targets

                # Prefer previous clip's local file for continuity when resuming mid-scene
                seed_ctx = previous_context_id
                if not seed_ctx and c_num > 1:
                    prev_path = clip_output_path(s_num, c_num - 1)
                    if file_is_usable(prev_path, min_bytes=1024):
                        seed_ctx = prev_path
                # Do not pass stale Veo operation IDs as Grok seeds
                if seed_ctx and isinstance(seed_ctx, str) and (
                    seed_ctx.startswith("models/") or seed_ctx.startswith("ctx_mock_")
                ):
                    seed_ctx = None

                if not should_generate:
                    if file_is_usable(out_path, min_bytes=1024):
                        print(f"  [Skip] Clip {c_num}: reusing existing {out_path}")
                        clip_paths.append(out_path)
                        self.state["clip_context_ids"][clip_state_key] = out_path
                        previous_context_id = out_path
                        continue
                    print(
                        f"  [Skip] Clip {c_num}: not selected and missing on disk "
                        f"(not included in composite until generated)."
                    )
                    previous_context_id = None
                    continue

                if c_num in wipe_set or (clip_plan and clip_plan.get("force_all_wipe")):
                    self._clear_clip_assets(s_num, c_num)

                # If regenerating a selected clip that already exists and wipe wasn't requested,
                # still allow force via wipe_set; otherwise generate_video_clip may reuse.
                attempts = max_qa_retries + 1 if qa_retry_on_fail else 1
                clip_path = None
                context_id = None
                qa_passed = False

                for attempt in range(1, attempts + 1):
                    force = attempt > 1 or (c_num in wipe_set)
                    if attempt > 1:
                        print(
                            f"  [QA Retry] Clip {c_num}: attempt {attempt}/{attempts} "
                            f"(regenerating after QA rejection)..."
                        )
                    clip_path, context_id = self.generate_video_clip(
                        s_num, clip, seed_ctx, force_regenerate=force
                    )

                    qa_passed = self.run_clip_qa(clip_path, clip["visual_prompt"])
                    self._update_clip_job(
                        s_num, c_num,
                        path=clip_path,
                        qa_approved=bool(qa_passed),
                        qa_attempt=attempt,
                        status="complete" if qa_passed else "qa_rejected",
                    )
                    if qa_passed:
                        self.clear_clip_stale(s_num, c_num)
                        break
                    if attempt < attempts:
                        self._invalidate_clip_file(
                            s_num, c_num, clip_path,
                            reason=f"qa_rejected_attempt_{attempt}",
                        )
                    else:
                        print(
                            f"  [QA Warning] Clip {c_num} still rejected after {attempts} attempt(s); "
                            f"keeping last render and continuing."
                        )

                clip_paths.append(clip_path)
                self.state["clip_context_ids"][clip_state_key] = context_id

                # Progressive scene merge: all existing clips in order so far
                if self.config.get("merge_scene_after_each_clip", True):
                    try:
                        ordered = self._ordered_existing_clip_paths(s_num, all_clips)
                        composite_scene_path = self.mix_scene_assets(
                            s_num, ordered, music_path, force=True
                        )
                        print(
                            f"  [Scene] Progressive merge after clip {c_num}: "
                            f"{composite_scene_path} ({len(ordered)} clip(s) on disk)"
                        )
                    except GenerationFailure as mix_err:
                        print(f"  [Scene] Progressive merge failed: {mix_err}")
                        raise

                ordered_now = self._ordered_existing_clip_paths(s_num, all_clips)
                self.state["scene_assets"][str(s_num)] = {
                    "video_clips": ordered_now,
                    "music_bed": music_path,
                    "composite": composite_scene_path,
                    "partial": True,
                    "clips_merged": len(ordered_now),
                }
                self.save_state()
                previous_context_id = context_id

            # Final mux of every clip that exists for this scene (ordered)
            ordered_final = self._ordered_existing_clip_paths(s_num, all_clips)
            if not ordered_final:
                raise GenerationFailure(f"Scene {s_num}: no clips available to composite.")
            generated_files.append(composite_output_path(s_num))
            composite_scene_path = self.mix_scene_assets(
                s_num, ordered_final, music_path, force=True
            )
            clip_paths = ordered_final

        except PipelineInterrupted:
            # Preserve partial scene progress, then re-raise for outer graceful_stop
            ordered = self._ordered_existing_clip_paths(s_num, all_clips)
            self.state.setdefault("scenes_completed", {})[str(s_num)] = False
            self.state.setdefault("scene_assets", {})[str(s_num)] = {
                "video_clips": ordered,
                "music_bed": music_path if file_is_usable(music_path, min_bytes=1) else None,
                "composite": composite_scene_path if file_is_usable(composite_scene_path, min_bytes=1024) else None,
                "partial": True,
                "clips_merged": len(ordered),
                "last_error": "interrupted_by_user",
            }
            self.save_state()
            raise
        except GenerationFailure as e:
            self._handle_generation_failure(
                s_num, generated_files, e,
                clip_paths=clip_paths, music_path=music_path,
            )

        self.state["scene_assets"][str(s_num)] = {
            "video_clips": clip_paths,
            "music_bed": music_path,
            "composite": composite_scene_path,
            "partial": False,
            "clips_merged": len(clip_paths),
        }
        self.save_state()
        self._active_clip_num = None
        return True

    def _ordered_existing_clip_paths(self, scene_num: int, clips: List[Dict[str, Any]]) -> List[str]:
        """Return on-disk clip paths in blueprint order (skip missing)."""
        paths: List[str] = []
        for clip in clips:
            p = clip_output_path(scene_num, clip.get("clip_number", 0))
            if file_is_usable(p, min_bytes=1024):
                paths.append(p)
        return paths

    def _clear_clip_assets(self, scene_num: int, clip_num: int) -> None:
        """Delete one clip's files and job state so it can be regenerated."""
        paths = [
            clip_output_path(scene_num, clip_num),
            f"assets/video/scene_{scene_num:02d}_clip_{clip_num:02d}.native.mp4",
            f"assets/video/scene_{scene_num:02d}_clip_{clip_num:02d}_seed_frame.png",
            composite_output_path(scene_num),  # force remux after regen
        ]
        for p in paths:
            try:
                if p and os.path.isfile(p):
                    os.remove(p)
            except OSError as e:
                print(f"  [Cleanup Warning] Could not remove '{p}': {e}")

        key = f"{scene_num}_{clip_num}"
        self.state.get("clip_context_ids", {}).pop(key, None)
        self.state.get("clip_jobs", {}).pop(key, None)
        self.state.setdefault("scenes_completed", {})[str(scene_num)] = False
        self.save_state()
        print(f"  [Reset] Cleared Scene {scene_num} Clip {clip_num} assets.")

    def backpropagate_retroactive_feedback(self, current_scene_num: int, target_scene_num: int, feedback: str):
        print(f"\n[Retroactive Propagation] Propagating feedback from Scene {current_scene_num} to Scene {target_scene_num}...")
        for scene in self.blueprint.get("scenes", []):
            s_num = scene["scene_number"]
            if target_scene_num <= s_num <= current_scene_num:
                for clip in scene.get("veo_clips", []):
                    if feedback not in clip["visual_prompt"]:
                        current_prompt = clip["visual_prompt"]
                        suffix = " / 720p, 24fps"
                        base_prompt = current_prompt.replace(suffix, "").strip()

                        updated_prompt = f"{base_prompt}, {feedback}"
                        if len(updated_prompt) + len(suffix) < 400:
                            clip["visual_prompt"] = f"{updated_prompt}{suffix}"
                        else:
                            allowed_len = 400 - len(suffix)
                            clip["visual_prompt"] = f"{updated_prompt[:allowed_len]}{suffix}"

        seed_tokens = self.blueprint.get("global_production_variables", {}).get("character_seed_tokens", {})
        for char_key, token in seed_tokens.items():
            if char_key.lower() in feedback.lower() or "character" in feedback.lower():
                token["description"] = f"{token['description']}, (Feedback Sync: {feedback})"

        self.save_blueprint_to_disk()

        for s_idx in range(target_scene_num, current_scene_num + 1):
            s_key = str(s_idx)
            if s_key in self.state["scenes_completed"]:
                del self.state["scenes_completed"][s_key]
            if s_key in self.state["scene_assets"]:
                del self.state["scene_assets"][s_key]
            clip_keys_to_remove = [k for k in self.state["clip_context_ids"] if k.startswith(f"{s_idx}_")]
            for k in clip_keys_to_remove:
                del self.state["clip_context_ids"][k]

        self.state["current_scene_index"] = target_scene_num - 1
        self.save_state()

    def _resolve_scene_composite_path(self, scene_num: int) -> Optional[str]:
        """Return a usable on-disk composite path for a scene, or None."""
        asset_info = self.state.get("scene_assets", {}).get(str(scene_num)) or {}
        composite = asset_info.get("composite")
        if file_is_usable(composite, min_bytes=1024):
            return composite
        candidate = composite_output_path(scene_num)
        if file_is_usable(candidate, min_bytes=1024):
            return candidate
        return None

    def _collect_scene_composites(self, approved_only: bool = True) -> List[str]:
        """
        Ordered list of scene composite paths from the blueprint.
        If approved_only, only scenes marked completed in state are included
        (still requires the composite file to exist on disk).
        """
        paths: List[str] = []
        for s in self.blueprint.get("scenes", []):
            s_num = s["scene_number"]
            if approved_only and not self.state.get("scenes_completed", {}).get(str(s_num)):
                continue
            comp = self._resolve_scene_composite_path(s_num)
            if comp:
                paths.append(comp)
        return paths

    def _concat_videos(self, input_paths: List[str], output_path: str, label: str = "movie") -> str:
        """Concatenate MP4s with ffmpeg (stream copy when possible)."""
        if not input_paths:
            raise GenerationFailure(f"No input videos to build {label}.")
        ensure_parent_dir(output_path)
        ffmpeg = resolve_ffmpeg()
        concat_list = f"assets/scenes/concat_list_{label.replace(' ', '_')}.txt"
        ensure_parent_dir(concat_list)
        with open(concat_list, "w", encoding="utf-8") as f:
            for path in input_paths:
                # Absolute paths are safest across CWD / WSL
                abs_path = os.path.abspath(path).replace("\\", "/")
                # ffmpeg concat demuxer on Windows/WSL: escape single quotes
                safe = abs_path.replace("'", "'\\''")
                f.write(f"file '{safe}'\n")

        tmp_out = f"{output_path}.tmp.mp4"
        cmd = [
            ffmpeg, "-y",
            "-f", "concat", "-safe", "0",
            "-i", concat_list,
            "-c", "copy",
            "-movflags", "+faststart",
            tmp_out,
        ]
        print(f"  [FFmpeg] Building {label} from {len(input_paths)} scene(s) -> {output_path}")
        try:
            result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        except Exception as e:
            raise GenerationFailure(f"Failed to run ffmpeg for {label}: {e}")

        if result.returncode != 0 or not file_is_usable(tmp_out, min_bytes=1024):
            # Fallback: re-encode if stream copy fails (codec/timebase mismatches)
            print(f"  [FFmpeg] Stream-copy failed for {label}; retrying with re-encode...")
            cmd_re = [
                ffmpeg, "-y",
                "-f", "concat", "-safe", "0",
                "-i", concat_list,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart",
                tmp_out,
            ]
            result = subprocess.run(cmd_re, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            if result.returncode != 0 or not file_is_usable(tmp_out, min_bytes=1024):
                err = (result.stderr or b"").decode(errors="ignore")[-500:]
                raise GenerationFailure(f"Failed to build {label}. FFmpeg: {err}")

        os.replace(tmp_out, output_path)
        return output_path

    def rebuild_wip_movie(
        self,
        reason: str = "",
        *,
        approved_only: Optional[bool] = None,
        force: bool = False,
    ) -> Optional[str]:
        """
        Rebuild the work-in-progress film from scene composites.

        approved_only:
          True  — only scenes marked approved (default for post-Approve)
          False — any scene with a composite on disk (use after clip/cascade regen)
          None  — True unless config wip_include_unapproved_composites is set
        force: rebuild even if rebuild_wip_movie_after_scene is false (manual refresh)
        """
        if not force and not self.config.get("rebuild_wip_movie_after_scene", True):
            return None

        require_environment(
            self.config, require_xai=False, require_gemini=False, require_ffmpeg=True
        )

        if approved_only is None:
            approved_only = not bool(
                self.config.get("wip_include_unapproved_composites", False)
            )

        output_path = self.config.get("wip_movie_path", "assets/movie_wip.mp4")
        scene_files = self._collect_scene_composites(approved_only=approved_only)
        if not scene_files:
            # Fallback: if nothing is marked approved, still stitch whatever composites exist
            if approved_only:
                scene_files = self._collect_scene_composites(approved_only=False)
                if scene_files:
                    print(
                        "  [WIP Movie] No approved scenes; using all composites on disk."
                    )
                    approved_only = False
            if not scene_files:
                print("  [WIP Movie] No scene composites yet — skip.")
                return None

        note = f" ({reason})" if reason else ""
        mode = "approved" if approved_only else "all composites"
        print(f"\n==================== WIP MOVIE UPDATE{note} [{mode}] ====================")
        try:
            path = self._concat_videos(scene_files, output_path, label="wip_movie")
            print(
                f"  [WIP Movie] Updated {path} with {len(scene_files)} scene(s) ({mode}). "
                f"Open this anytime to review the film so far."
            )
            self.state["wip_movie"] = {
                "path": path,
                "scene_count": len(scene_files),
                "approved_only": approved_only,
                "updated_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
                "reason": reason or "",
            }
            self.save_state()
            return path
        except GenerationFailure as e:
            print(f"  [WIP Movie Warning] {e}")
            return None

    def remux_scenes_and_rebuild_wip(
        self,
        scene_nums: List[int],
        reason: str = "",
    ) -> Optional[str]:
        """
        Force-remux each scene from current on-disk clips, then rebuild movie_wip.mp4
        from all available composites (not only approved). Used after cascade/clip regen.
        """
        unique = sorted({int(s) for s in scene_nums})
        for sn in unique:
            try:
                path = self.remux_scene_from_disk(sn)
                if path:
                    print(f"  [Remux] Scene {sn} -> {path}")
                else:
                    print(f"  [Remux] Scene {sn}: no clips on disk")
            except Exception as e:
                print(f"  [Remux Warning] Scene {sn}: {e}")
        return self.rebuild_wip_movie(
            reason=reason or f"after remux scenes {unique}",
            approved_only=False,
            force=True,
        )

    def run_mastering(self):
        print("\n==================== PIPELINE STAGE 5: GLOBAL FILM MASTERING ====================")
        self.ensure_asset_directories()
        output_movie_path = "assets/movie_final_master.mp4"

        # Prefer approved scenes; if none marked approved, fall back to any composites on disk
        scene_files = self._collect_scene_composites(approved_only=True)
        if not scene_files:
            print("[Info] No approved scenes; using any available scene composites on disk.")
            scene_files = self._collect_scene_composites(approved_only=False)

        if not scene_files:
            print("[Error] No scene composite files available for mastering.")
            return

        try:
            path = self._concat_videos(scene_files, output_movie_path, label="final_master")
            print(f"SUCCESS: Completed cinematic mastering! Saved final video to: {path}")
            # Keep WIP in sync with final when mastering runs
            if self.config.get("rebuild_wip_movie_after_scene", True):
                wip = self.config.get("wip_movie_path", "assets/movie_wip.mp4")
                try:
                    import shutil
                    ensure_parent_dir(wip)
                    shutil.copy2(path, wip)
                    print(f"  [WIP Movie] Synced {wip} to final master.")
                except OSError as e:
                    print(f"  [WIP Movie Warning] Could not sync WIP copy: {e}")
        except GenerationFailure as e:
            print(f"[Error] Mastering compilation failed: {e}")

    def _find_scene_index(self, scenes: List[Dict[str, Any]], scene_number: int) -> int:
        for idx, s in enumerate(scenes):
            if s.get("scene_number") == scene_number:
                return idx
        return -1

    def _first_incomplete_scene_index(self, scenes: List[Dict[str, Any]]) -> int:
        for idx, s in enumerate(scenes):
            if not self.state.get("scenes_completed", {}).get(str(s["scene_number"])):
                return idx
        return len(scenes)

    def _scene_status_line(self, scene: Dict[str, Any]) -> str:
        s_num = scene["scene_number"]
        s_key = str(s_num)
        approved = bool(self.state.get("scenes_completed", {}).get(s_key))
        composite = composite_output_path(s_num)
        has_composite = file_is_usable(composite, min_bytes=1024)
        clip_count = len(scene.get("veo_clips") or [])
        on_disk = 0
        for c in scene.get("veo_clips") or []:
            if file_is_usable(clip_output_path(s_num, c.get("clip_number", 0)), min_bytes=1024):
                on_disk += 1
        if approved:
            status = "APPROVED"
        elif has_composite or on_disk:
            status = f"PARTIAL {on_disk}/{clip_count} clips"
        else:
            status = "NOT STARTED"
        setting = (scene.get("setting") or "")[:48]
        return f"  Scene {s_num:>3}: {status:<18} | {setting}"

    def _clear_scene_generation_assets(self, scene_num: int, wipe_files: bool = True) -> None:
        """Reset state (and optionally files) so a scene can be regenerated from scratch."""
        s_key = str(scene_num)
        self.state.setdefault("scenes_completed", {})[s_key] = False
        self.state.get("scene_assets", {}).pop(s_key, None)

        # clip_context_ids keys like "1_3"
        prefix = f"{scene_num}_"
        for k in list(self.state.get("clip_context_ids", {}).keys()):
            if k.startswith(prefix):
                del self.state["clip_context_ids"][k]
        for k in list(self.state.get("clip_jobs", {}).keys()):
            if k.startswith(prefix):
                del self.state["clip_jobs"][k]
        self.state.get("music_jobs", {}).pop(s_key, None)

        if wipe_files:
            paths = [composite_output_path(scene_num), music_output_path(scene_num)]
            # Wipe known clip slots 1..40 (covers long scenes)
            for c_num in range(1, 41):
                paths.append(clip_output_path(scene_num, c_num))
                paths.append(f"assets/video/scene_{scene_num:02d}_clip_{c_num:02d}.native.mp4")
                paths.append(f"assets/video/scene_{scene_num:02d}_clip_{c_num:02d}_seed_frame.png")
            work_dir = f"assets/scenes/_work_scene_{scene_num:02d}"
            concat_list = f"assets/scenes/concat_list_scene_{scene_num}.txt"
            paths.append(concat_list)
            for p in paths:
                try:
                    if p and os.path.isfile(p):
                        os.remove(p)
                except OSError as e:
                    print(f"  [Cleanup Warning] Could not remove '{p}': {e}")
            if os.path.isdir(work_dir):
                try:
                    import shutil
                    shutil.rmtree(work_dir, ignore_errors=True)
                except Exception as e:
                    print(f"  [Cleanup Warning] Could not remove work dir '{work_dir}': {e}")
            print(f"  [Reset] Cleared generated assets for Scene {scene_num}.")

        self.save_state()

    def _print_clip_status(self, scene: Dict[str, Any]) -> None:
        s_num = scene["scene_number"]
        clips = scene.get("veo_clips") or []
        print(f"\n  Clips in Scene {s_num} ({len(clips)} total):")
        for c in clips:
            c_num = c.get("clip_number")
            path = clip_output_path(s_num, c_num)
            on_disk = file_is_usable(path, min_bytes=1024)
            job = self.state.get("clip_jobs", {}).get(f"{s_num}_{c_num}", {})
            qa = job.get("qa_approved")
            if on_disk and qa is True:
                flag = "OK "
            elif on_disk and qa is False:
                flag = "QA!"
            elif on_disk:
                flag = "DISK"
            else:
                flag = " -- "
            dlg = str(((c.get("audio_payload") or {}).get("dialogue") or "")).strip()
            src = c.get("veo_continuation_source", "none")
            preview = (c.get("visual_prompt") or "")[:55].replace("\n", " ")
            dlg_mark = "VO" if dlg else "  "
            print(f"    [{flag}] clip {c_num:>2} {dlg_mark} src={src:<16} | {preview}")

    def _parse_clip_spec(self, raw: str, max_clip: int) -> Optional[Dict[str, Any]]:
        """
        Parse clip selection text into a clip_plan.
        Examples: '', 'all', '3', '3 regen', '2-4', '2-4 regen', '2,4,5'
        """
        text = (raw or "").strip().lower()
        if not text or text in ("all", "a", "*"):
            return {"targets": None, "wipe": set()}

        wipe = False
        tokens = text.replace(",", " ").split()
        # trailing regen/force/wipe
        while tokens and tokens[-1] in ("regen", "r", "force", "f", "wipe", "w"):
            wipe = True
            tokens.pop()
        if not tokens:
            return None

        numbers: set = set()
        for tok in tokens:
            if "-" in tok:
                try:
                    a_str, b_str = tok.split("-", 1)
                    a, b = int(a_str), int(b_str)
                except ValueError:
                    return None
                if a > b:
                    a, b = b, a
                for n in range(a, b + 1):
                    numbers.add(n)
            else:
                try:
                    numbers.add(int(tok))
                except ValueError:
                    return None

        if not numbers:
            return None
        invalid = [n for n in numbers if n < 1 or n > max_clip]
        if invalid:
            print(f"  Clip number(s) out of range 1..{max_clip}: {invalid}")
            return None

        wipe_set = set(numbers) if wipe else set()
        return {"targets": numbers, "wipe": wipe_set}

    def _prompt_clip_selection(self, scene: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        """Ask which clip(s) within a scene to generate. None targets = all clips."""
        clips = scene.get("veo_clips") or []
        if not clips:
            return {"targets": None, "wipe": set()}
        max_clip = max(c.get("clip_number", 0) for c in clips)
        self._print_clip_status(scene)
        print(
            "\n  Clip options:\n"
            "    [Enter] / all     All clips in this scene\n"
            "    3                 Only clip 3 (reuse others on disk)\n"
            "    3 regen           Wipe + regenerate only clip 3\n"
            "    2-4               Clips 2 through 4\n"
            "    2-4 regen         Wipe + regenerate clips 2-4\n"
            "    2,5               Clips 2 and 5\n"
        )
        while True:
            self._check_shutdown("clip selector")
            try:
                raw = input(f"  Clip(s) for Scene {scene['scene_number']} [all]: ").strip()
            except EOFError as e:
                raise PipelineInterrupted("EOF on clip selector") from e
            plan = self._parse_clip_spec(raw, max_clip)
            if plan is None:
                print("  Could not parse clip selection. Try 'all', '3', '3 regen', or '2-4'.")
                continue
            if plan["wipe"]:
                confirm = input(
                    f"  Wipe clip(s) {sorted(plan['wipe'])} and regenerate? [y/N]: "
                ).strip().lower()
                if confirm not in ("y", "yes"):
                    print("  Cancelled wipe.")
                    plan["wipe"] = set()
            if plan["targets"] is None:
                print("  [Select] All clips in scene.")
            else:
                print(f"  [Select] Clips {sorted(plan['targets'])}"
                      + (" (wipe+regen)" if plan["wipe"] else " (reuse others)"))
            return plan

    def _prompt_scene_selection(
        self, scenes: List[Dict[str, Any]]
    ) -> Tuple[int, bool, Optional[Dict[str, Any]]]:
        """
        Ask which scene (and optionally which clips) to work on.

        Returns (scene_index, single_scene_mode, clip_plan).
        clip_plan is None for pipeline-forward mode (all clips each scene).
        """
        total = len(scenes)
        first_incomplete_idx = self._first_incomplete_scene_index(scenes)
        default_num = (
            scenes[first_incomplete_idx]["scene_number"]
            if first_incomplete_idx < total
            else scenes[-1]["scene_number"]
        )

        print("\n==================== SCENE / CLIP SELECTOR ====================")
        print(f"Blueprint has {total} scenes. Resume default: Scene {default_num}")
        print("Recent / nearby status:")
        default_idx = self._find_scene_index(scenes, default_num)
        start = max(0, default_idx - 3)
        end = min(total, default_idx + 5)
        for s in scenes[start:end]:
            print(self._scene_status_line(s))
        if start > 0 or end < total:
            print("  ... (enter a number to jump to any scene)")

        print(
            "\nOptions:\n"
            "  [Enter]               Continue from default incomplete scene (all clips)\n"
            "  <N>                   Jump to scene N, then continue forward (all clips)\n"
            "  <N> only              Work only on scene N (stop after review)\n"
            "  <N> regen             Wipe whole scene N and regenerate (stop after)\n"
            "  <N> clip <C>          Regen/reuse clip C in scene N (stop after scene)\n"
            "  <N> clip <C> regen    Wipe + regen clip C only (stop after scene)\n"
            "  <N> clip <C> regen go Wipe + regen clip C, finish scene, then continue to next scenes\n"
            "  status                Show scene statuses\n"
            "  master                Final film mastering\n"
            "  Q                     Quit\n"
            "\nTip: add 'go' or 'continue' to keep moving forward after the scene "
            "(e.g. '1 clip 5 regen go')."
        )

        while True:
            self._check_shutdown("scene selector")
            try:
                raw = input(f"\nScene to generate [{default_num}]: ").strip()
            except EOFError as e:
                raise PipelineInterrupted("EOF on scene selector") from e

            if not raw:
                if first_incomplete_idx < total:
                    return first_incomplete_idx, False, None
                print("  All scenes look approved. Enter a scene number to rework, 'master', or Q.")
                continue

            low = raw.lower()
            if low in ("q", "quit", "exit"):
                self.graceful_stop("Quit from scene selector")

            if low in ("master", "mastering", "m"):
                return total, False, None

            if low in ("status", "s", "list", "ls"):
                for i, s in enumerate(scenes):
                    print(self._scene_status_line(s))
                    if (i + 1) % 20 == 0:
                        try:
                            more = input("  -- more -- [Enter] continue, Q stop listing: ").strip().upper()
                        except EOFError:
                            break
                        if more == "Q":
                            break
                continue

            parts = raw.split()
            try:
                scene_num = int(parts[0])
            except ValueError:
                print("  Enter a scene number, 'status', 'master', or Q.")
                continue

            idx = self._find_scene_index(scenes, scene_num)
            if idx < 0:
                print(f"  Scene {scene_num} not found in blueprint.")
                continue

            scene = scenes[idx]
            max_clip = max((c.get("clip_number", 0) for c in (scene.get("veo_clips") or [])), default=0)

            # Inline: "1 clip 3", "1 clip 3 regen", "1 clip 3 regen go", "1 c 3 r continue"
            clip_plan: Optional[Dict[str, Any]] = None
            single = False
            wipe_scene = False
            # "go" / "continue" = after this scene, keep walking the pipeline forward
            continue_forward = False
            rest_parts = parts[1:]
            if rest_parts and rest_parts[-1].lower() in ("go", "continue", "fwd", "forward", "+"):
                continue_forward = True
                rest_parts = rest_parts[:-1]

            if len(rest_parts) >= 2 and rest_parts[0].lower() in ("clip", "c", "clips"):
                clip_raw = " ".join(rest_parts[1:])
                clip_plan = self._parse_clip_spec(clip_raw, max_clip)
                if clip_plan is None:
                    print(
                        "  Bad clip spec. Examples: '1 clip 3', '1 clip 5 regen', "
                        "'1 clip 5 regen go'"
                    )
                    continue
                # Default for clip-targeted work is single-scene; 'go' continues after approve
                single = not continue_forward
                self.state.setdefault("scenes_completed", {})[str(scene_num)] = False
                self.save_state()
                tgt = sorted(clip_plan["targets"] or [])
                wipe_note = " wipe+regen" if clip_plan["wipe"] else ""
                if continue_forward:
                    print(
                        f"  [Select] Scene {scene_num} clip(s) {tgt}{wipe_note}, "
                        f"then continue forward to later scenes after approve."
                    )
                else:
                    print(
                        f"  [Select] Scene {scene_num} clip(s) {tgt}{wipe_note} "
                        f"(scene only; stop after review unless you press S)."
                    )
                return idx, single, clip_plan

            mode_token = rest_parts[0].lower() if rest_parts else ""
            single = mode_token in ("only", "o", "one", "regen", "r", "force", "f")
            wipe_scene = mode_token in ("regen", "r", "force", "f", "wipe", "w")
            if continue_forward and single:
                # e.g. "1 only go" or "1 regen go"
                single = False

            if wipe_scene:
                confirm = input(
                    f"  Wipe ALL generated clips/composite for Scene {scene_num}? [y/N]: "
                ).strip().lower()
                if confirm in ("y", "yes"):
                    self._clear_scene_generation_assets(scene_num, wipe_files=True)
                    if not continue_forward:
                        single = True
                    clip_plan = self._prompt_clip_selection(scene)
                else:
                    print("  Cancelled full wipe.")
                    if not continue_forward:
                        single = True
                    clip_plan = self._prompt_clip_selection(scene)
            elif single or (rest_parts and mode_token in ("only", "o", "one")):
                self.state.setdefault("scenes_completed", {})[str(scene_num)] = False
                self.save_state()
                print(f"  [Select] Scene {scene_num} only.")
                clip_plan = self._prompt_clip_selection(scene)
                if continue_forward:
                    single = False
                    print("  [Select] Will continue to later scenes after this one.")
            else:
                print(f"  [Select] Starting at Scene {scene_num}, then continue forward (all clips each).")
                clip_plan = None
                single = False

            return idx, single, clip_plan

    def run_pipeline(self):
        self.ensure_asset_directories()
        # Fail before any paid work or long Stage 0 / scene loop
        require_environment(self.config)

        try:
            # Stage 0: always check for missing character refs (adults + Young/Teen variants).
            # Already-locked files are skipped inside pre_production_character_design().
            self.pre_production_character_design()

            scenes = self.blueprint.get("scenes", [])
            total_scenes = len(scenes)
            if not scenes:
                print("[Error] Blueprint has no scenes.")
                return

            current_idx, single_scene_mode, clip_plan = self._prompt_scene_selection(scenes)
            if current_idx >= total_scenes:
                self.run_mastering()
                return

            self.state["current_scene_index"] = current_idx
            self.save_state()

            while current_idx < total_scenes:
                self._check_shutdown("pipeline scene loop")
                scene = scenes[current_idx]
                s_num = scene["scene_number"]

                # clip_plan applies to this first selected scene only (may regen one clip,
                # then remux full scene). Later scenes always run all clips.
                plan_for_scene = clip_plan
                self.process_scene(scene, clip_plan=plan_for_scene)
                clip_plan = None  # don't reuse a one-shot clip plan after first process

                print(f"\n*** INTERACTIVE GATE: REVIEW SCENE {s_num}/{total_scenes} ***")
                print(f"    Composite: {composite_output_path(s_num)}")
                user_action = ""
                while user_action not in ["A", "F", "R", "S", "C", "Q"]:
                    self._check_shutdown("interactive review gate")
                    try:
                        user_action = input(
                            "Review. [A] Approve, [C] Clip again, [F] Feedback, "
                            "[R] Rollback, [S] Scene select, [Q] Quit: "
                        ).strip().upper()
                    except EOFError as e:
                        raise PipelineInterrupted("EOF on interactive input") from e

                if user_action == "A":
                    print(f"[Success] Approved Scene {s_num}.")
                    self.state["scenes_completed"][str(s_num)] = True
                    self.save_state()
                    # Rebuild running film from all approved scenes so far
                    self.rebuild_wip_movie(reason=f"after approving Scene {s_num}")
                    if single_scene_mode:
                        print("[Select] Single-scene mode complete.")
                        again = input("Select another scene/clip? [y/N]: ").strip().lower()
                        if again in ("y", "yes"):
                            current_idx, single_scene_mode, clip_plan = self._prompt_scene_selection(scenes)
                            if current_idx >= total_scenes:
                                self.run_mastering()
                                return
                            self.state["current_scene_index"] = current_idx
                            self.save_state()
                            continue
                        break
                    current_idx += 1
                    self.state["current_scene_index"] = current_idx
                    self.save_state()
                elif user_action == "C":
                    # Re-pick clips in the same scene and regenerate
                    self.state.setdefault("scenes_completed", {})[str(s_num)] = False
                    self.save_state()
                    clip_plan = self._prompt_clip_selection(scene)
                    single_scene_mode = True
                    # loop continues same current_idx
                elif user_action == "F":
                    feedback = input("\nEnter your forward feedback modifier: ").strip()
                    scope = input(
                        "Select scoping - [L] Local Clip, [C] Cascading Scene, [G] Global Forward: "
                    ).strip().upper()
                    self.state["scenes_completed"][str(s_num)] = False
                    self.save_state()
                elif user_action == "R":
                    feedback = input("\nEnter retroactive feedback: ").strip()
                    target_scene = 0
                    while target_scene < 1 or target_scene > s_num:
                        try:
                            target_scene = int(
                                input(
                                    f"Enter the past scene number where this modification "
                                    f"should begin (1 to {s_num}): "
                                )
                            )
                        except ValueError:
                            pass
                    self.backpropagate_retroactive_feedback(s_num, target_scene, feedback)
                    current_idx = self.state["current_scene_index"]
                    single_scene_mode = False
                    clip_plan = None
                elif user_action == "S":
                    current_idx, single_scene_mode, clip_plan = self._prompt_scene_selection(scenes)
                    if current_idx >= total_scenes:
                        self.run_mastering()
                        return
                    self.state["current_scene_index"] = current_idx
                    self.save_state()
                elif user_action == "Q":
                    self.graceful_stop("Quit requested from interactive gate")

            if not single_scene_mode:
                do_master = input(
                    "\nAll selected work finished. Run final film mastering? [y/N]: "
                ).strip().lower()
                if do_master in ("y", "yes"):
                    self.run_mastering()
        except PipelineInterrupted as e:
            self.graceful_stop(str(e) or "Interrupted by user")
        except KeyboardInterrupt:
            self.graceful_stop("Interrupted by user (KeyboardInterrupt)")


if __name__ == "__main__":
    # Prefer: python -m cli  (or python -m renderer)
    from pathlib import Path as _Path
    import sys as _sys

    _ws = _Path(__file__).resolve().parent.parent
    if str(_ws) not in _sys.path:
        _sys.path.insert(0, str(_ws))
    from cli.__main__ import main as _cli_main

    raise SystemExit(_cli_main())