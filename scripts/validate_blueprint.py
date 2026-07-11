import json
import sys

def validate():
    print("Loading nickandme.clips.grok.json for final compliance validation...")
    try:
        with open('nickandme.clips.grok.json', 'r', encoding='utf-8') as f:
            data = json.load(f)
    except Exception as e:
        print(f"FAILED: JSON parsing failed: {e}")
        sys.exit(1)

    print("Checking metadata keys...")
    required_meta = ["movie_title", "source_book_title", "next_scene_number", "cumulative_duration_seconds", "global_production_variables", "scenes"]
    for k in required_meta:
        if k not in data:
            print(f"FAILED: Missing metadata key: {k}")
            sys.exit(1)

    print("Checking next_scene_number...")
    if data["next_scene_number"] is not None:
        print(f"FAILED: next_scene_number must be null, found {data['next_scene_number']}")
        sys.exit(1)

    scenes = data["scenes"]
    print(f"Total scenes found: {len(scenes)}")
    if len(scenes) != 90:
        print(f"FAILED: Scene count is not 90, found {len(scenes)}")
        sys.exit(1)

    # Validate scene numbering and sequential integrity
    print("Checking scene numbers and sequential order...")
    for index, s in enumerate(scenes):
        expected_num = index + 1
        if s["scene_number"] != expected_num:
            print(f"FAILED: Scene out of order or missing. Index {index} has scene_number {s['scene_number']}, expected {expected_num}")
            sys.exit(1)

    # Validate cumulative duration and range
    cumulative_duration = data["cumulative_duration_seconds"]
    print(f"Cumulative duration: {cumulative_duration} seconds ({cumulative_duration/60:.2f} minutes)")
    if not (4860 <= cumulative_duration <= 5940):
        print(f"FAILED: Cumulative duration {cumulative_duration} is outside the 90-minute ±10% window (4860 to 5940s)")
        sys.exit(1)

    # Validate detailed scene rules
    fixed_neg = "no legible text, no watermarks, no logos, no extra limbs, blur/obscure environmental signage or screens"

    print("Performing deep clip and music bed checks for all scenes...")
    errors = []
    for s in scenes:
        s_num = s["scene_number"]
        veo_clips = s.get("veo_clips", [])
        if not veo_clips:
            errors.append(f"Scene {s_num} has no veo_clips!")
            continue

        scene_dur = s.get("total_estimated_duration_seconds", 0)
        calc_scene_dur = 0

        # Music bed check
        music_bed = s.get("music_bed", {})
        if not music_bed:
            errors.append(f"Scene {s_num} is missing music_bed!")
            continue

        song_structure = music_bed.get("song_structure", [])
        music_dur_sum = 0
        for section in song_structure:
            music_dur_sum += section.get("duration_seconds", 0)

        # Clip chaining check
        for c_idx, c in enumerate(veo_clips):
            clip_num = c["clip_number"]
            if clip_num != c_idx + 1:
                errors.append(f"Scene {s_num} Clip {clip_num} is out of order")

            # Veo 3.1 duration checks
            clip_dur = 8 if c_idx == 0 else 7
            calc_scene_dur += clip_dur

            # Veo continuation source
            expected_src = "none" if c_idx == 0 else "extend_previous"
            if c.get("veo_continuation_source") != expected_src:
                errors.append(f"Scene {s_num} Clip {clip_num} has incorrect veo_continuation_source: '{c.get('veo_continuation_source')}', expected '{expected_src}'")

            # Prompts length and suffix
            prompt = c.get("visual_prompt", "")
            if len(prompt) >= 400:
                errors.append(f"Scene {s_num} Clip {clip_num} visual_prompt exceeds 400 chars ({len(prompt)})")

            if "720p, 24fps" not in prompt:
                errors.append(f"Scene {s_num} Clip {clip_num} visual_prompt is missing '720p, 24fps'")

            # Fixed negative prompt match
            if c.get("negative_prompt") != fixed_neg:
                errors.append(f"Scene {s_num} Clip {clip_num} has mismatched negative prompt")

            # Dialogue formatting
            audio = c.get("audio_payload", {})
            dialogue = audio.get("dialogue", "")
            if "\n" in dialogue or "\r" in dialogue:
                errors.append(f"Scene {s_num} Clip {clip_num} has prohibited raw linebreaks in dialogue")

        # Check calculated scene duration
        if scene_dur != calc_scene_dur:
            errors.append(f"Scene {s_num} total_estimated_duration_seconds ({scene_dur}) does not match sum of clip durations ({calc_scene_dur})")

        if music_dur_sum != scene_dur:
            errors.append(f"Scene {s_num} music_bed song_structure duration sum ({music_dur_sum}) does not match scene duration ({scene_dur})")

    if errors:
        print(f"\nFAILED: {len(errors)} validation errors found:")
        for err in errors[:50]:  # Cap display
            print(f" - {err}")
        if len(errors) > 50:
            print(f" ... and {len(errors) - 50} more errors.")
        sys.exit(1)

    print("\nSUCCESS: All 90 scenes, 100% of veo_clips, and all music bed configurations are FULLY COMPLIANT with the production blueprint schema!")

if __name__ == "__main__":
    validate()
