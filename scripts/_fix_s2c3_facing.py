import json
from pathlib import Path

p = Path("nickandme.clips.grok.json")
d = json.loads(p.read_text(encoding="utf-8"))
s = next(x for x in d["scenes"] if x["scene_number"] == 2)
c = s["veo_clips"][2]

# Orientation: Nick faces Mrs. Engel / the window (not the camera with her behind him)
c["visual_prompt"] = (
    "FLASHBACK alley, camera behind Character_N_Young looking toward a brick house wall. "
    "Mrs. Engel (elderly, apron) yells from a shattered kitchen window ABOVE. "
    "Character_N_Young (12, sturdy preteen, dirty white t-shirt, jeans — child, NOT adult) stands in the alley "
    "FACING the house, looking UP at her; he takes two defiant steps TOWARD the window/wall "
    "(away from camera). Never face the camera with his back to her. / 720p, 24fps"
)
assert len(c["visual_prompt"]) < 400, len(c["visual_prompt"])
print(len(c["visual_prompt"]))
print(c["visual_prompt"])
p.write_text(json.dumps(d, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("saved")
