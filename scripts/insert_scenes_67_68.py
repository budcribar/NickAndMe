import json

# Define constants
CHAR_P_HOSPITAL = "Character_P (thin frame, light blue t-shirt, jeans, blue eyes)"
CHAR_P_FUNERAL = "Character_P (thin frame, dark grey sweater, jeans, blue eyes)"

CHAR_S_HOSPITAL = "Character_S (short brown hair, green tank top, jeans, light blue eyes)"
CHAR_S_FUNERAL = "Character_S (short brown hair, black blouse, jeans, light blue eyes)"

CHAR_N_HOSPITAL = "Character_N (muscular build, reddish-brown hair slicked, black leather jacket, jeans)"
CHAR_N_FUNERAL = "Character_N (muscular build, reddish-brown hair, dark funeral suit, white shirt)"
CHAR_N_WORK = "Character_N (muscular build, reddish-brown hair, grey work shirt, jeans)"

CHAR_M_HOSPITAL = "Character_M (elderly woman, silver hair, pale skin, white hospital gown)"

NEG_PROMPT = "no legible text, no watermarks, no logos, no extra limbs, blur/obscure environmental signage or screens"

def make_song_structure(durations, labels, types, notes_list):
    song = []
    for d, l, t, n in zip(durations, labels, types, notes_list):
        song.append({
            "section_label": l,
            "section_type": t,
            "is_repeat_of": None,
            "duration_seconds": d,
            "production_notes": [n],
            "lyrics": None
        })
    return song

def generate_scene_clips(scene_num, duration, chars, prompts, dialogues, speakers, env, scene_start_sec):
    clips = []
    num_clips = len(prompts)
    assert 8 + 7*(num_clips - 1) == duration, f"Clip count mismatch for Scene {scene_num}: expected duration {duration}, got {8 + 7*(num_clips-1)}"

    current_sec = scene_start_sec
    for i in range(num_clips):
        clip_dur = 8 if i == 0 else 7
        ts_start_min = current_sec // 60
        ts_start_sec = current_sec % 60
        ts_end_min = (current_sec + clip_dur) // 60
        ts_end_sec = (current_sec + clip_dur) % 60
        timestamp = f"{ts_start_min:02d}:{ts_start_sec:02d}-{ts_end_min:02d}:{ts_end_sec:02d}"

        prompt = f"{chars[i]} in {env}. {prompts[i]} / 720p, 24fps"
        assert len(prompt) < 400, f"Prompt too long ({len(prompt)}) in Scene {scene_num} Clip {i+1}: {prompt}"

        clips.append({
            "clip_number": i + 1,
            "timestamp": timestamp,
            "veo_continuation_source": "none" if i == 0 else "extend_previous",
            "visual_prompt": prompt,
            "negative_prompt": NEG_PROMPT,
            "audio_payload": {
                "speaker": speakers[i],
                "dialogue": dialogues[i]
            }
        })
        current_sec += clip_dur
    return clips

# Let's rebuild Scene 67 and 68
p_67 = [
  "sits up suddenly in bed in the dim rustic bedroom, looking startled as a phone rings",
  "walks down the dark wooden hallway toward the glowing rotary phone on a small table",
  "reaches out a shaking hand to lift the black rotary receiver from its cradle",
  "holds the phone to his ear, his face tight with rising dread under the cold hallway light",
  "listens intently, his eyes widening in shock and disbelief as he hears Nick's voice on the line",
  "leans heavily against the hallway's wooden paneling, closing his eyes as his face contorts with pain",
  "looks up blankly in the hallway, clutching the phone with white knuckles",
  "stands there in the dark hallway, the cold receiver pressed to his ear, looking hollow",
  "appears in the bedroom doorway behind Character_P, looking concerned and alert",
  "looks over his shoulder, his lips moving silently as he delivers the bad news",
  "steps quickly down the hallway, wrapping her arms around Character_P in a comforting embrace",
  "holds Character_P's hands, speaking to him with calm, fierce determination",
  "throws clothes clumsily into a dark duffel bag on the unmade bed in the rustic bedroom",
  "grabs a light jacket from a chair and holds the car keys, looking determined",
  "zips the duffel bag, his hands visibly trembling in the dim bedroom light",
  "hurry out the wooden screen door of the farmhouse, carrying their bags onto the dark porch",
  "sit inside the red Toyota Civic hatchback, staring through the front windshield as headlights flicker on"
]
d_67 = [
  "It was three in the morning. A sound cut through the silence. At first I thought it was a bird, but then I realized it was the old rotary phone in the hallway ringing.",
  "My heart was pounding in my chest. You never get a phone call at three in the morning for good news. My feet felt cold on the floorboards as I reached for the phone.",
  "I picked up the heavy plastic receiver. Hello? I muttered, my voice raspy with sleep. The static on the line hissed back at me, filled with empty space.",
  "Then I heard Nick's voice. It didn't sound like Nick. It sounded tiny, shaking, and fragile. Little bro? he said. It's Ma. She's in trouble.",
  "He said she had collapsed in the kitchen. She wasn't breathing right. He had called the ambulance. They were taking her to Madison General as fast as they could.",
  "I felt the blood drain from my face. My knees buckled slightly, and I had to lean against the wood wall for support. The hallway seemed to spin around me.",
  "Where is she now? I asked, my voice barely a whisper. Nick said she was in the Intensive Care Unit, hooked up to machines. They didn't know if she would make it.",
  "I stood there in the dark hallway, the cold receiver pressed to my ear, listening to the hum of the long-distance line, unable to form words.",
  "Sionna came out of the bedroom, rubbing her eyes in the dim light, looking at me with growing concern. She knew instantly that something was terribly wrong.",
  "I looked at her, my eyes wide with terror. It's Ma, I whispered. She's in the hospital. It's bad, Sionna. Really bad. My voice broke on her name.",
  "Without a word, Sionna stepped forward and wrapped her arms around me, holding me tight as the realization hit me. Her warmth was the only thing keeping me anchored.",
  "She immediately took charge. We have to go, she said gently but firmly. Pack your bag. I'll get the car keys. We're driving back to Madison right now.",
  "I nodded slowly, my mind completely numb. I felt like I was moving through thick gelatin, unable to think or react, packing items without even seeing them.",
  "Sionna was already at the door, her yellow sun hat left behind on the table, holding her keys and looking at me with fierce, unwavering support.",
  "I zipped the duffel bag, my hands shaking so hard I could barely pull the metal zipper. Every muscle in my body was tense, waiting for a blow.",
  "We ran down the porch steps into the cool night air. The peaceful Wisconsin farm now felt like a distant, cruel memory. The night was dark and silent.",
  "We got into the Toyota Civic. Sionna started the engine, and the headlights flickered on, cutting through the country darkness. We backed out onto the main road."
]
c_67 = [CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_S_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_S_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_S_HOSPITAL, CHAR_P_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_S_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_S_HOSPITAL]
s_67 = ["Character_P"] * 13 + ["Character_S"] + ["Character_P"] * 3

scene_67 = {
    "scene_number": 67,
    "setting": "Night 270 - Mushroom and Ginger's Farm (Interior) - The Call",
    "total_estimated_duration_seconds": 120,
    "scene_filename": "Scene_67_Night270_Farmhouse_TheCall",
    "transition_type": "cut",
    "lighting_continuity_token": "Moody, dim interior with flickering shadows cast by dying embers in the fireplace, interrupted by a single harsh, cold hallway light",
    "music_bed": {
      "style_description": "Low, ominous ambient drone with tense violin tremolo and slow, rhythmic acoustic guitar stabs, building into high-register panic piano keys",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 14, 21, 28, 28, 21],
          ["Silence Shattered", "The Phone Rings", "Tense Tremolos", "Nick's Voice", "Panic Rises", "The Cold Truth / Outro"],
          ["instrumental"]*6,
          [
              "A quiet, low synth pad representing the deep silence of 3 AM.",
              "High, ringing metallic keys mimic a phone ring in a long reverb tail.",
              "A slow, scraping violin tremolo rises, creating intense anxiety.",
              "A dark, heavy acoustic guitar chords enter in slow, irregular intervals.",
              "A rapid, repeating minor piano motif enters, increasing the heartbeat feel.",
              "A low, lingering synthesizer note hangs in the air, cold and empty."
          ]
      )
    },
    "veo_clips": []
}
scene_67["veo_clips"] = generate_scene_clips(67, 120, c_67, p_67, d_67, s_67, "the dim, rustic wooden farmhouse hallway", 2296)


p_68 = [
  "sit inside the red Toyota Civic as rain sheets down heavily on the windshield, wipers sweeping fast",
  "looks out the passenger window into the blackness, his face reflected in the rain-streaked window",
  "grips the steering wheel tightly, staring intently through the rain-streaked windshield",
  "sit in the car cabin as dashboard lights cast a soft green glow on their tense faces",
  "squeezes his eyes shut, rubbing his forehead as if trying to block out a terrible thought",
  "reaches across the center console to squeeze Character_P's hand comforting him",
  "looks down at their joined hands, swallowing hard as he tries to calm his rapid breathing",
  "stares at the spinning numbers of the car odometer as they slowly increase",
  "adjusts the windshield wiper speed, squinting through a sudden splash of water from a passing truck",
  "watches the raindrops slide horizontally across the passenger window pane",
  "leans his head back against the seat headrest, staring up at the dark roof of the car cabin",
  "glances at him with deep love and worry, her face soft in the dashboard light",
  "sits up, watching the distant city lights of Madison appear on the horizon through the windshield",
  "look ahead as the car exits the highway onto a city ramp, driving past quiet streets",
  "steers the car onto the street leading to Madison General Hospital, her face resolute",
  "looks up at the towering, sterile hospital building as the Civic pulls into the parking lot"
]
d_68 = [
  "The rain started about twenty miles outside of Sturgeon Bay. It didn't just rain – it poured. The water came down in heavy, blinding sheets on the glass.",
  "I stared out into the pitch-black fields. Every time the lightning flashed, the trees looked like reaching skeleton arms. The road was a slick black snake.",
  "Sionna kept her eyes glued to the white lines on the asphalt. She didn't say anything, but I could see the muscles in her jaw working, tight with focus.",
  "The only light was the dim green glow of the dashboard and the headlights cutting through the gray wall of water. The engine hummed a steady, anxious tune.",
  "My mind was spinning out of control. I kept thinking about Ma lying in that hospital bed, hooked up to cold plastic tubes. It felt like a physical weight in my chest.",
  "Hey, she said softly, reaching out to squeeze my hand. She's strong, P. She's survived so much already. Don't lose hope yet. Just breathe.",
  "I nodded, but my throat was too tight to reply. I gripped her hand back like it was a life preserver in a stormy ocean. I couldn't let go.",
  "Every mile marker we passed felt like a tick of a giant clock. We were moving as fast as the old Toyota could go, but it felt like we were standing still.",
  "A huge semi-truck roared past us in the opposite direction, throwing a massive wave of dirty water over our hood. The Civic shook, but Sionna held it steady.",
  "I watched the drops of water race across the side glass, merging and disappearing into the dark. I wondered if Nick was sitting by himself at the hospital.",
  "Nick was always the one who dealt with the physical stuff, the violence, the action. But a hospital? That wasn't his arena. He must be terrified in there.",
  "Sionna checked her mirrors and accelerated slightly as the rain began to taper off into a light, gray drizzle. The sky in the east was starting to turn a pale gray.",
  "As the dawn broke, we finally saw the distant lights of Madison reflecting off the wet pavement. My stomach did a slow, painful flip at the sight.",
  "The city was completely silent, asleep. It felt strange that the world was just carrying on, normal and quiet, while our entire lives were about to crash.",
  "Sionna turned onto the main avenue leading to the hospital. The big brick buildings rose up ahead of us like cold monuments under the gray morning sky.",
  "We pulled into the parking lot. The red Civic came to a stop, the engine turning off with a soft hiss. I stared at the emergency entrance, unable to move."
]
c_68 = [CHAR_P_HOSPITAL + ", " + CHAR_S_HOSPITAL, CHAR_P_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_S_HOSPITAL, CHAR_P_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_S_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_HOSPITAL]
s_68 = ["Character_P", "Character_P", "Character_S", "Character_P", "Character_P", "Character_S", "Character_P", "Character_P", "Character_S", "Character_P", "Character_P", "Character_S", "Character_P", "Character_P", "Character_S", "Character_P"]

scene_68 = {
    "scene_number": 68,
    "setting": "Night 270 - Road Trip / Rainy Highway (Exterior) - The Panic Drive",
    "total_estimated_duration_seconds": 113,
    "scene_filename": "Scene_68_Night270_RainyHighway_Drive",
    "transition_type": "cut",
    "lighting_continuity_token": "Dark, moody night highway illuminated only by sweeping headlight beams and the constant, rhythmic sweep of windshield wipers reflection",
    "music_bed": {
      "style_description": "Steady, driving drum beat representing the car's engine, overlaid with a tense, weeping solo viola and cold, wet-sounding synth textures",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 28],
          ["The Storm Outside", "Wiper Rhythm", "Viola Weeps", "Engine Rumble", "Anxious Horizon / Outro"],
          ["instrumental"]*5,
          [
              "A cold, wet-sounding synth texture pad begins, mimicking heavy rainfall.",
              "A steady, ticking woodblock percussion enters, matching the wiper tempo.",
              "A lonely, soaring viola melody enters, expressing grief and speed.",
              "A low, rolling synthesized sub-bass joins, giving a sense of fast travel.",
              "All instruments fade into a quiet, sweeping white-noise sound of rushing water."
          ]
      )
    },
    "veo_clips": []
}
scene_68["veo_clips"] = generate_scene_clips(68, 113, c_68, p_68, d_68, s_68, "a rain-swept highway at night inside the red Toyota Civic hatchback", 2416)


# Let's read the existing JSON
with open('nickandme.clips.grok.json', 'r') as f:
    data = json.load(f)

# The scenes list in data['scenes'] contains scenes 1-66, then 69-79.
# Let's split them
pre_scenes = [s for s in data['scenes'] if s['scene_number'] <= 66]
post_scenes = [s for s in data['scenes'] if s['scene_number'] >= 69]

print(f"Loaded {len(pre_scenes)} pre-scenes and {len(post_scenes)} post-scenes.")

# Let's rebuild the post_scenes programmatically to shift their timestamps and make sure their clip timestamps are shifted!
# Wait, let's look at the post-scenes scene numbers and shift them programmatically if needed.
# No, post scenes already have scene numbers 69-79, which is correct! We just skipped 67 and 68 in the append list.
# We just need to insert Scene 67 and 68 between Scene 66 and Scene 69!
# But we also must shift the clip timestamps of post_scenes!
# Let's see: the duration added by Scene 67 and 68 is: 120 + 113 = 233 seconds.
# Let's shift all clip timestamps in post_scenes by +233 seconds!

for s in post_scenes:
    print(f"Shifting timestamps for Scene {s['scene_number']}...")
    for clip in s['veo_clips']:
        # Format of timestamp: MM:SS-MM:SS
        ts = clip['timestamp']
        start_str, end_str = ts.split('-')

        start_m, start_s = map(int, start_str.split(':'))
        end_m, end_s = map(int, end_str.split(':'))

        start_total = start_m * 60 + start_s + 233
        end_total = end_m * 60 + end_s + 233

        new_start_ts = f"{start_total // 60:02d}:{start_total % 60:02d}"
        new_end_ts = f"{end_total // 60:02d}:{end_total % 60:02d}"

        clip['timestamp'] = f"{new_start_ts}-{new_end_ts}"

# Combine everything back
all_scenes = pre_scenes + [scene_67, scene_68] + post_scenes

# Verify sequence
seq = [s['scene_number'] for s in all_scenes]
print("New scene sequence:", seq)

data['scenes'] = all_scenes
data['next_scene_number'] = 80
data['cumulative_duration_seconds'] = 3644 + 233

with open('nickandme.clips.grok.json', 'w') as f:
    json.dump(data, f, indent=2)

print("Scenes 67 and 68 successfully inserted, and subsequent scene timestamps shifted!")
print(f"New cumulative duration: {data['cumulative_duration_seconds']} seconds ({data['cumulative_duration_seconds'] // 60}m {data['cumulative_duration_seconds'] % 60}s)")
