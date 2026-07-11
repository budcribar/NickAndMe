import json

d = json.load(open("nickandme.clips.grok.json", encoding="utf-8"))
for s in d["scenes"]:
    if s.get("scene_number") == 5:
        print("setting:", s.get("setting"))
        print("duration:", s.get("total_estimated_duration_seconds"))
        print("filename:", s.get("scene_filename"))
        print("lighting:", s.get("lighting_continuity_token"))
        print()
        for c in s.get("veo_clips") or []:
            ap = c.get("audio_payload") or {}
            print(f"--- Clip {c.get('clip_number')} src={c.get('veo_continuation_source')} ---")
            print("VISUAL:", c.get("visual_prompt"))
            print("SPEAKER:", ap.get("speaker"))
            print("DIALOGUE:", repr(ap.get("dialogue")))
            print()
        break
