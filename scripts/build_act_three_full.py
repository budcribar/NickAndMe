import json

# Define constants
CHAR_P_HOSPITAL = "Character_P (thin frame, light blue t-shirt, jeans, blue eyes)"
CHAR_P_FUNERAL = "Character_P (thin frame, dark grey sweater, jeans, blue eyes)"
CHAR_P_PRESENT = "Character_P (older, early twenties, thin frame, black sweater, blue eyes)"

CHAR_S_HOSPITAL = "Character_S (short brown hair, green tank top, jeans, light blue eyes)"
CHAR_S_FUNERAL = "Character_S (short brown hair, black blouse, jeans, light blue eyes)"
CHAR_S_PRESENT = "Character_S (older, short brown hair, white knit sweater, light blue eyes)"

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

# Let's define the scene templates
act_three_raw = []

# --- SCENE 67 ---
act_three_raw.append({
    "scene_number": 67,
    "setting": "Night 270 - Mushroom and Ginger's Farm (Interior) - The Call",
    "duration": 120,
    "filename": "Scene_67_Night270_Farmhouse_TheCall",
    "transition": "cut",
    "lighting": "Moody, dim interior with flickering shadows cast by dying embers in the fireplace, interrupted by a single harsh, cold hallway light",
    "style": "Low, ominous ambient drone with tense violin tremolo and slow, rhythmic acoustic guitar stabs, building into high-register panic piano keys",
    "song_durations": [8, 14, 21, 28, 28, 21],
    "song_labels": ["Silence Shattered", "The Phone Rings", "Tense Tremolos", "Nick's Voice", "Panic Rises", "The Cold Truth / Outro"],
    "song_types": ["instrumental"] * 6,
    "song_notes": [
        "A quiet, low synth pad representing the deep silence of 3 AM.",
        "High, ringing metallic keys mimic a phone ring in a long reverb tail.",
        "A slow, scraping violin tremolo rises, creating intense anxiety.",
        "A dark, heavy acoustic guitar chords enter in slow, irregular intervals.",
        "A rapid, repeating minor piano motif enters, increasing the heartbeat feel.",
        "A low, lingering synthesizer note hangs in the air, cold and empty."
    ],
    "env": "the dim, rustic wooden farmhouse hallway",
    "chars_present": [CHAR_P_HOSPITAL, CHAR_S_HOSPITAL],
    "clips_content": [
        ([CHAR_P_HOSPITAL], "sits up suddenly in bed in the dim rustic bedroom, looking startled as a phone rings", "Character_P", "It was three in the morning. A sound cut through the silence. At first I thought it was a bird, but then I realized it was the old rotary phone in the hallway ringing."),
        ([CHAR_P_HOSPITAL], "walks down the dark wooden hallway toward the glowing rotary phone on a small table", "Character_P", "My heart was pounding in my chest. You never get a phone call at three in the morning for good news. My feet felt cold on the floorboards as I reached for the phone."),
        ([CHAR_P_HOSPITAL], "reaches out a shaking hand to lift the black rotary receiver from its cradle", "Character_P", "I picked up the heavy plastic receiver. Hello? I muttered, my voice raspy with sleep. The static on the line hissed back at me, filled with empty space."),
        ([CHAR_P_HOSPITAL], "holds the phone to his ear, his face tight with rising dread under the cold hallway light", "Character_P", "Then I heard Nick's voice. It didn't sound like Nick. It sounded tiny, shaking, and fragile. Little bro? he said. It's Ma. She's in trouble."),
        ([CHAR_P_HOSPITAL], "listens intently, his eyes widening in shock and disbelief as he hears Nick's voice on the line", "Character_P", "He said she had collapsed in the kitchen. She wasn't breathing right. He had called the ambulance. They were taking her to Madison General as fast as they could."),
        ([CHAR_P_HOSPITAL], "leans heavily against the hallway's wooden paneling, closing his eyes as his face contorts with pain", "Character_P", "I felt the blood drain from my face. My knees buckled slightly, and I had to lean against the wood wall for support. The hallway seemed to spin around me."),
        ([CHAR_P_HOSPITAL], "looks up blankly in the hallway, clutching the phone with white knuckles", "Character_P", "Where is she now? I asked, my voice barely a whisper. Nick said she was in the Intensive Care Unit, hooked up to machines. They didn't know if she would make it."),
        ([CHAR_P_HOSPITAL], "stands there in the dark hallway, the cold receiver pressed to his ear, looking hollow", "Character_P", "I stood there in the dark hallway, the cold receiver pressed to my ear, listening to the hum of the long-distance line, unable to form words."),
        ([CHAR_S_HOSPITAL], "appears in the bedroom doorway behind Character_P, looking concerned and alert", "Character_P", "Sionna came out of the bedroom, rubbing her eyes in the dim light, looking at me with growing concern. She knew instantly that something was terribly wrong."),
        ([CHAR_P_HOSPITAL, CHAR_S_HOSPITAL], "interact as Character_P looks over his shoulder, his lips moving silently to deliver the bad news", "Character_P", "I looked at her, my eyes wide with terror. It's Ma, I whispered. She's in the hospital. It's bad, Sionna. Really bad. My voice broke on her name."),
        ([CHAR_P_HOSPITAL, CHAR_S_HOSPITAL], "hug as Character_S steps quickly down the hallway, wrapping her arms around Character_P", "Character_P", "Without a word, Sionna stepped forward and wrapped her arms around me, holding me tight as the realization hit me. Her warmth was the only thing keeping me anchored."),
        ([CHAR_P_HOSPITAL, CHAR_S_HOSPITAL], "hold hands as Character_S speaks to him with calm, fierce determination", "Character_P", "She immediately took charge. We have to go, she said gently but firmly. Pack your bag. I'll get the car keys. We're driving back to Madison right now."),
        ([CHAR_P_HOSPITAL], "throws clothes clumsily into a dark duffel bag on the unmade bed in the rustic bedroom", "Character_P", "I nodded slowly, my mind completely numb. I felt like I was moving through thick gelatin, unable to think or react, packing items without even seeing them."),
        ([CHAR_S_HOSPITAL], "grabs a light jacket from a chair and holds the car keys, looking determined", "Character_S", "Sionna was already at the door, her yellow sun hat left behind on the table, holding her keys and looking at me with fierce, unwavering support."),
        ([CHAR_P_HOSPITAL], "zips the duffel bag, his hands visibly trembling in the dim bedroom light", "Character_P", "I zipped the duffel bag, my hands shaking so hard I could barely pull the metal zipper. Every muscle in my body was tense, waiting for a blow."),
        ([CHAR_P_HOSPITAL, CHAR_S_HOSPITAL], "hurry out the wooden screen door of the farmhouse, carrying their bags onto the dark porch", "Character_P", "We ran down the porch steps into the cool night air. The peaceful Wisconsin farm now felt like a distant, cruel memory. The night was dark and silent."),
        ([CHAR_P_HOSPITAL, CHAR_S_HOSPITAL], "sit inside the red Toyota Civic hatchback, staring through the front windshield as headlights flicker on", "Character_P", "We got into the Toyota Civic. Sionna started the engine, and the headlights flickered on, cutting through the country darkness. We backed out onto the main road.")
    ]
})

# --- SCENE 68 ---
act_three_raw.append({
    "scene_number": 68,
    "setting": "Night 270 - Road Trip / Rainy Highway (Exterior) - The Panic Drive",
    "duration": 113,
    "filename": "Scene_68_Night270_RainyHighway_Drive",
    "transition": "cut",
    "lighting": "Dark, moody night highway illuminated only by sweeping headlight beams and the constant, rhythmic sweep of windshield wipers reflection",
    "style": "Steady, driving drum beat representing the car's engine, overlaid with a tense, weeping solo viola and cold, wet-sounding synth textures",
    "song_durations": [8, 21, 28, 28, 28],
    "song_labels": ["The Storm Outside", "Wiper Rhythm", "Viola Weeps", "Engine Rumble", "Anxious Horizon / Outro"],
    "song_types": ["instrumental"] * 5,
    "song_notes": [
        "A cold, wet-sounding synth texture pad begins, mimicking heavy rainfall.",
        "A steady, ticking woodblock percussion enters, matching the wiper tempo.",
        "A lonely, soaring viola melody enters, expressing grief and speed.",
        "A low, rolling synthesized sub-bass joins, giving a sense of fast travel.",
        "All instruments fade into a quiet, sweeping white-noise sound of rushing water."
    ],
    "env": "a rain-swept highway at night inside the red Toyota Civic hatchback",
    "chars_present": [CHAR_P_HOSPITAL, CHAR_S_HOSPITAL],
    "clips_content": [
        ([CHAR_P_HOSPITAL, CHAR_S_HOSPITAL], "sit inside the red Toyota Civic as rain sheets down heavily on the windshield, wipers sweeping fast", "Character_P", "The rain started about twenty miles outside of Sturgeon Bay. It didn't just rain – it poured. The water came down in heavy, blinding sheets on the glass."),
        ([CHAR_P_HOSPITAL], "looks out the passenger window into the blackness, his face reflected in the rain-streaked window", "Character_P", "I stared out into the pitch-black fields. Every time the lightning flashed, the trees looked like reaching skeleton arms. The road was a slick black snake."),
        ([CHAR_S_HOSPITAL], "grips the steering wheel tightly, staring intently through the rain-streaked windshield", "Character_S", "Sionna kept her eyes glued to the white lines on the asphalt. She didn't say anything, but I could see the muscles in her jaw working, tight with focus."),
        ([CHAR_P_HOSPITAL, CHAR_S_HOSPITAL], "sit in the car cabin as dashboard lights cast a soft green glow on their tense faces", "Character_P", "The only light was the dim green glow of the dashboard and the headlights cutting through the gray wall of water. The engine hummed a steady, anxious tune."),
        ([CHAR_P_HOSPITAL], "squeezes his eyes shut, rubbing his forehead as if trying to block out a terrible thought", "Character_P", "My mind was spinning out of control. I kept thinking about Ma lying in that hospital bed, hooked up to cold plastic tubes. It felt like a physical weight in my chest."),
        ([CHAR_S_HOSPITAL], "reaches across the center console to squeeze Character_P's hand comforting him", "Character_S", "Hey, she said softly, reaching out to squeeze my hand. She's strong, P. She's survived so much already. Don't lose hope yet. Just breathe."),
        ([CHAR_P_HOSPITAL], "looks down at their joined hands, swallowing hard as he tries to calm his rapid breathing", "Character_P", "I nodded, but my throat was too tight to reply. I gripped her hand back like it was a life preserver in a stormy ocean. I couldn't let go."),
        ([CHAR_P_HOSPITAL], "stares at the spinning numbers of the car odometer as they slowly increase", "Character_P", "Every mile marker we passed felt like a tick of a giant clock. We were moving as fast as the old Toyota could go, but it felt like we were standing still."),
        ([CHAR_S_HOSPITAL], "adjusts the windshield wiper speed, squinting through a sudden splash of water from a passing truck", "Character_S", "A huge semi-truck roared past us in the opposite direction, throwing a massive wave of dirty water over our hood. The Civic shook, but Sionna held it steady."),
        ([CHAR_P_HOSPITAL], "watches the raindrops slide horizontally across the passenger window pane", "Character_P", "I watched the drops of water race across the side glass, merging and disappearing into the dark. I wondered if Nick was sitting by himself at the hospital."),
        ([CHAR_P_HOSPITAL], "leans his head back against the seat headrest, staring up at the dark roof of the car cabin", "Character_P", "Nick was always the one who dealt with the physical stuff, the violence, the action. But a hospital? That wasn't his arena. He must be terrified in there."),
        ([CHAR_S_HOSPITAL], "glances at him with deep love and worry, her face soft in the dashboard light", "Character_S", "Sionna checked her mirrors and accelerated slightly as the rain began to taper off into a light, gray drizzle. The sky in the east was starting to turn a pale gray."),
        ([CHAR_P_HOSPITAL], "sits up, watching the distant city lights of Madison appear on the horizon through the windshield", "Character_P", "As the dawn broke, we finally saw the distant lights of Madison reflecting off the wet pavement. My stomach did a slow, painful flip at the sight."),
        ([CHAR_P_HOSPITAL, CHAR_S_HOSPITAL], "look ahead as the car exits the highway onto a city ramp, driving past quiet streets", "Character_P", "The city was completely silent, asleep. It felt strange that the world was just carrying on, normal and quiet, while our entire lives were about to crash."),
        ([CHAR_S_HOSPITAL], "steers the car onto the street leading to Madison General Hospital, her face resolute", "Character_S", "Sionna turned onto the main avenue leading to the hospital. The big brick buildings rose up ahead of us like cold monuments under the gray morning sky."),
        ([CHAR_P_HOSPITAL], "looks up at the towering, sterile hospital building as the Civic pulls into the parking lot", "Character_P", "We pulled into the parking lot. The red Civic came to a stop, the engine turning off with a soft hiss. I stared at the emergency entrance, unable to move.")
    ]
})

print("First two scenes defined!")
# Wait, let's write out all 24 scenes programmatically in a single execution.
# To make it incredibly robust, we will create scenes 69-90 by appending them incrementally to a list on disk.
# Let's write the definitions of all scenes in structured text format, then load them in Python!
