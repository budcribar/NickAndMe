import json

# Define constants
CHAR_P_HOSPITAL = "Character_P (thin frame, light blue t-shirt, jeans, blue eyes)"
CHAR_S_HOSPITAL = "Character_S (short brown hair, green tank top, jeans, light blue eyes)"
CHAR_N_HOSPITAL = "Character_N (muscular build, reddish-brown hair slicked, black leather jacket, jeans)"
CHAR_M_HOSPITAL = "Character_M (elderly woman, silver hair, pale skin, white hospital gown)"

NEG_PROMPT = "no legible text, no watermarks, no logos, no extra limbs, blur/obscure environmental signage or screens"

# --- SCENE 69 ---
scene_69 = {
  "scene_number": 69,
  "setting": "Day 271 - Madison General Hospital (Interior) - The Waiting Room",
  "total_estimated_duration_seconds": 120,
  "scene_filename": "Scene_69_Day271_Hospital_WaitingRoom",
  "transition_type": "cut",
  "lighting_continuity_token": "Harsh, buzzing fluorescent overhead lights casting a sterile, pale green pallor over vinyl chairs and tiled floors",
  "music_bed": {
    "style_description": "Cold, sterile synthesizer pads with a slow, mechanical acoustic guitar pluck, a hollow solo oboe, and soft ambient chimes",
    "vocal_style": "none",
    "song_structure": [
      { "section_label": "Fluorescent Buzz", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 8, "production_notes": ["A cold, humming synth pad starts, mimicking fluorescent lighting buzz."], "lyrics": None },
      { "section_label": "Slumped Figure", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 14, "production_notes": ["A slow, mechanical acoustic guitar pattern plucks, representing exhaustion."], "lyrics": None },
      { "section_label": "Hollow Oboe", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 21, "production_notes": ["A wistful, hollow solo oboe melody enters, full of grief and space."], "lyrics": None },
      { "section_label": "Heavy Steps", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 28, "production_notes": ["A low, muted acoustic bass-drum thump beats slowly, like heavy footsteps."], "lyrics": None },
      { "section_label": "Shared Silence", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 28, "production_notes": ["The oboe and acoustic guitar harmonize, soft and incredibly sad."], "lyrics": None },
      { "section_label": "Hospital Fog / Outro", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 11, "production_notes": ["All instruments fade into a quiet, cold, ringing electronic bell note."], "lyrics": None }
    ]
  },
  "veo_clips": [
    {
      "clip_number": 1,
      "timestamp": "41:00-41:08",
      "veo_continuation_source": "none",
      "visual_prompt": "Character_P (thin frame, light blue t-shirt) and Character_S (short brown hair, green tank top) walk through sliding glass doors into a sterile hospital lobby. / 720p, 24fps",
      "negative_prompt": NEG_PROMPT,
      "audio_payload": { "speaker": "Character_P", "dialogue": "We walked through the sliding glass doors. The air inside smelled of alcohol, bleach, and old coffee. The fluorescent lights buzzed overhead, harsh and cold." }
    },
    *[{
      "clip_number": i + 2,
      "timestamp": f"41:{(8 + i*7)//60 + 0:01d}:{(8 + i*7)%60:02d}-41:{(15 + i*7)//60 + 0:01d}:{(15 + i*7)%60:02d}" if (8 + i*7) < 60 else f"42:{(8 + i*7)%60:02d}-42:{(15 + i*7)%60:02d}",
      "veo_continuation_source": "extend_previous",
      "visual_prompt": "",
      "negative_prompt": NEG_PROMPT,
      "audio_payload": { "speaker": "Character_P", "dialogue": "" }
    } for i in range(16)]
  ]
}

dialogues_69 = [
  "I saw him immediately. Nick. He was slumped in a corner vinyl chair under a dead fern, his head in his hands. He looked so small in his leather jacket.",
  "I walked over to him, my sneakers squeaking on the polished linoleum floor. Every step felt like lifting lead weights. Nick? I said softly.",
  "He looked up. His eyes were bloodshot, and there were dark circles under them. For the first time, I saw fear in my brother's eyes. Real fear.",
  "He didn't make a sarcastic joke. He didn't puff out his chest. He just looked at me. P, he said, his voice raspy. She's in there. They won't let me see her.",
  "Sionna stood a few feet back, giving us space, but her presence was a quiet anchor in the background. She looked at Nick with gentle, sorrowful eyes.",
  "I found her on the kitchen floor, P, Nick whispered, his voice cracking. She was just lying there. I didn't know what to do. I tried to lift her, but she was limp.",
  "I put my hand on his shoulder. It was the first time I had initiated physical contact with him in years. You did the right thing, Nick. You called the ambulance.",
  "They took her in the ambulance, he said, staring at his palms. I followed in the Pontiac. I ran three red lights. I thought she was going to die in my car.",
  "I looked at his hands – the hands that had won dozens of bar fights, the hands that had protected me. Right now, they were just shaking, empty and weak.",
  "Sionna stepped forward and gently handed Nick a small paper cup of water. Drink this, Nick, she said softly. You need to stay hydrated. He took it numbly.",
  "Thanks, Sionna, Nick muttered, looking up at her. He drank the water in one big gulp and crushed the paper cup in his fist, his expression turning grim.",
  "We sat in silence. The double doors of the Intensive Care Unit stared back at us, cold and impassive. Every few minutes, a nurse in blue scrubs would walk through.",
  "Nick leaned his head back against the wall. I always thought I could protect us from anything, P, he murmured. But I can't fight a stroke. I can't punch a disease.",
  "It was the most honest thing he had ever said. He had spent his whole life being our shield, and now the shield was useless. I felt a wave of deep love for him.",
  "Sionna sat down next to me, taking my hand. We were three young people sitting in a cold room, waiting for a doctor to tell us if our world was going to end.",
  "Suddenly, the double doors swung open. A doctor in green scrubs and a white coat stepped out, holding a clipboard. He looked around and walked toward us."
]

prompts_69 = [
  f"{CHAR_N_HOSPITAL} sits slumped in a blue vinyl chair in the waiting room, his face buried in his hands, looking completely defeated. / 720p, 24fps",
  f"{CHAR_P_HOSPITAL} walks slowly toward Nick in the sterile waiting room, his footsteps echoing on the tiles. / 720p, 24fps",
  f"CLOSE-UP on {CHAR_N_HOSPITAL} as he looks up, his eyes bloodshot and rimmed with red, his reddish-brown hair messy and unstyled. / 720p, 24fps",
  f"{CHAR_P_HOSPITAL} and {CHAR_N_HOSPITAL} stand face to face as Character_P sits in the adjacent chair, leaning forward. / 720p, 24fps",
  f"{CHAR_S_HOSPITAL} stands back slightly, holding her jacket in the waiting room, watching the brothers with deep compassion. / 720p, 24fps",
  f"{CHAR_N_HOSPITAL} shakes his head, running a hand through his reddish-brown hair in pure frustration. / 720p, 24fps",
  f"{CHAR_P_HOSPITAL} puts a gentle hand on {CHAR_N_HOSPITAL}'s leather-clad shoulder, squeezing it tightly. / 720p, 24fps",
  f"{CHAR_N_HOSPITAL} looks down at his hands, his fingers interlaced and shaking slightly. / 720p, 24fps",
  f"CLOSE-UP on {CHAR_N_HOSPITAL}'s shaking hands, raw and calloused, held open in front of him. / 720p, 24fps",
  f"{CHAR_S_HOSPITAL} walks over, gently placing a paper cup of water in {CHAR_N_HOSPITAL}'s hand with a warm smile. / 720p, 24fps",
  f"{CHAR_N_HOSPITAL} takes a tiny sip of water, looking up at Sionna with quiet, uncharacteristic gratitude. / 720p, 24fps",
  f"The closed metal doors of the Intensive Care Unit sit in the hospital corridor under a bright exit sign. / 720p, 24fps",
  f"{CHAR_N_HOSPITAL} leans back in the vinyl chair, staring up blankly at the buzzing fluorescent light fixture. / 720p, 24fps",
  f"{CHAR_P_HOSPITAL} looks at {CHAR_N_HOSPITAL}, feeling a sudden surge of deep, adult understanding of his brother's burden. / 720p, 24fps",
  f"{CHAR_S_HOSPITAL} sits beside {CHAR_P_HOSPITAL}, holding his hand, her shoulder resting against his comforting him. / 720p, 24fps",
  f"{CHAR_P_HOSPITAL}, {CHAR_N_HOSPITAL}, and {CHAR_S_HOSPITAL} all look up as a doctor in a green scrub suit walks toward them. / 720p, 24fps"
]

for idx in range(16):
    scene_69["veo_clips"][idx + 1]["visual_prompt"] = prompts_69[idx]
    scene_69["veo_clips"][idx + 1]["audio_payload"]["dialogue"] = dialogues_69[idx]
    scene_69["veo_clips"][idx + 1]["audio_payload"]["speaker"] = "Character_P" if idx != 10 and idx != 6 and idx != 8 and idx != 12 and idx != 14 else "Character_N" if idx in [6, 8, 12] else "Character_S" if idx == 10 else "Character_P"

print("Scene 69 generated programmatically on disk!")
# Write scene_69 out or write a full script that builds scenes 69-75
# Let's write append_act_three_part2.py which will build and append Scenes 69 and 70 to make sure we make rapid progress!
with open('nickandme.clips.grok.json', 'r') as f:
    data = json.load(f)

data['scenes'].append(scene_69)
data['cumulative_duration_seconds'] += 120
data['next_scene_number'] = 70

with open('nickandme.clips.grok.json', 'w') as f:
    json.dump(data, f, indent=2)

print("Scene 69 appended successfully!")
