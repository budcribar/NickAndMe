import json
import os
import sys
import time
import subprocess
import urllib.request
import urllib.error
from typing import Dict, Any, List, Optional

# Try to import Google GenAI SDK (mock/fallback if not installed for absolute robustness)
try:
    from google import genai
    from google.genai import types
except ImportError:
    genai = None
    types = None

STATE_FILE = "pipeline_state.json"
BLUEPRINT_FILE = "nickandme.json"
CONFIG_FILE = "pipeline_config.json"


class GenerationFailure(Exception):
    """
    Raised whenever a paid model/API call (Suno music, Veo video, or the local
    FFmpeg mux/master step) fails or produces unusable output.
    """
    pass


def music_output_path(scene_number: int) -> str:
    return f"assets/music/scene_{scene_number:02d}_music.mp3"


def clip_output_path(scene_number: int, clip_number: int) -> str:
    return f"assets/video/scene_{scene_number:02d}_clip_{clip_number:02d}.mp4"


def composite_output_path(scene_number: int) -> str:
    return f"assets/scenes/scene_{scene_number:02d}_complete.mp4"


class AgenticGenerationEngine:
    def __init__(self, blueprint_path: str = BLUEPRINT_FILE, state_path: str = STATE_FILE, config_path: str = CONFIG_FILE):
        self.blueprint_path = blueprint_path
        self.state_path = state_path
        self.config_path = config_path
        
        self.blueprint: Dict[str, Any] = {}
        self.state: Dict[str, Any] = {}
        self.config: Dict[str, Any] = {}
        self.client = None

        # Load configurations first
        self.load_config()
        
        # Initialize Google GenAI client if SDK is available and API key is set
        if genai and os.environ.get("GEMINI_API_KEY"):
            try:
                self.client = genai.Client()
            except Exception as e:
                print(f"[Warning] Failed to initialize GenAI Client: {e}")

        self.load_blueprint()
        self.load_state()

    def load_config(self):
        """Loads execution engine options from a distinct external configuration JSON."""
        if not os.path.exists(self.config_path):
            print(f"[Info] Config file not found. Generating default '{self.config_path}'...")
            self.config = {
                "model_name": "veo-3.1-fast-generate-preview", # Fast loop default
                "use_video_audio_for_music": False,             # Set to True to bypass Suno and use Veo native audio
                "aspect_ratio": "16:9",
                "duration_seconds": 8
            }
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
                self.config = {
                    "model_name": "veo-3.1-fast-generate-preview",
                    "use_video_audio_for_music": False,
                    "aspect_ratio": "16:9",
                    "duration_seconds": 8
                }

    def load_blueprint(self):
        """Loads the master movie blueprint JSON payload."""
        if not os.path.exists(self.blueprint_path):
            print(f"[Error] Movie blueprint not found at: {self.blueprint_path}")
            sys.exit(1)
        try:
            with open(self.blueprint_path, 'r') as f:
                self.blueprint = json.load(f)
            print(f"[Success] Loaded master blueprint '{self.blueprint.get('movie_title', 'Untitled')}'")
        except Exception as e:
            print(f"[Error] Failed to parse blueprint JSON: {e}")
            sys.exit(1)

    def load_state(self):
        """Loads progress state or initializes a new pipeline state cache."""
        if os.path.exists(self.state_path):
            try:
                with open(self.state_path, 'r') as f:
                    self.state = json.load(f)
                print(f"[Success] Resumed execution state from '{self.state_path}'")
            except Exception as e:
                print(f"[Warning] Failed to parse state file, starting fresh: {e}")
                self.initialize_fresh_state()
        else:
            self.initialize_fresh_state()

    def initialize_fresh_state(self):
        """Initializes empty/default pipeline state metadata."""
        self.state = {
            "current_scene_index": 0,
            "scenes_completed": {},  
            "scene_assets": {},      
            "clip_context_ids": {}   
        }
        self.save_state()
        print("[Info] Initialized clean pipeline state cache.")

    def save_state(self):
        """Saves current state cache to disk with atomic safety."""
        temp_file = f"{self.state_path}.tmp"
        try:
            with open(temp_file, 'w') as f:
                json.dump(self.state, f, indent=2)
            os.replace(temp_file, self.state_path)
        except Exception as e:
            print(f"[Error] Failed to write state cache: {e}")

    def save_blueprint_to_disk(self):
        """Saves modifications made to the live blueprint in memory back to disk."""
        temp_file = f"{self.blueprint_path}.tmp"
        try:
            with open(temp_file, 'w') as f:
                json.dump(self.blueprint, f, indent=2)
            os.replace(temp_file, self.blueprint_path)
            print(f"[Success] Persisted blueprint updates to '{self.blueprint_path}'")
        except Exception as e:
            print(f"[Error] Failed to write blueprint to disk: {e}")

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
                label = (section.get("section_label") or section.get("section_