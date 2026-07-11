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

# Define Scene Data
scenes_to_append = []

# --- SCENE 70: ICU / Life Support (127s / 18 clips) ---
p_70 = [
  "stares tearfully at her from the bedside",
  "lies motionless with her silver hair spread across the pillow",
  "strokes her pale arm with absolute gentleness",
  "looks up at the heart rate monitor",
  "looks down, eyes filling with tears",
  "gently holds her small, cool hand",
  "watches the chest rise and fall rhythmically with the machine's hiss",
  "stares at her serene, quiet face",
  "bends his head down to weep",
  "wipes away a tear, looking at his brother",
  "looks at the quiet face, holding his breath",
  "kisses her cool, soft forehead in farewell",
  "wipes his eyes, his shoulders shaking",
  "stares back at his brother with raw emotion",
  "leans close to her ear to whisper comfort",
  "holds her cool hand against his cheek",
  "stands in silent vigil beside the bed",
  "looks up as a nurse steps into the doorway"
]
d_70 = [
  "The doctor led us into the Intensive Care Unit. The room was dim, filled with the steady, rhythmic hum and hiss of machines. It sounded like an alien spaceship.",
  "And there she was. Ma. She looked so incredibly small in that big metal bed. There was a thick plastic tube in her mouth, connected to a bellows machine.",
  "I walked over to the side of the bed. Her skin was pale, almost translucent, and her silver hair was spread across the white pillow. I took her hand. It felt cool.",
  "Nick stood on the other side. He didn't touch her at first, just stared at her face, his arms hanging limp at his sides. He looked like he was in shock.",
  "Hey Ma, I whispered, leaning close to her ear. It's P. I'm here. We're both here, Ma. Nick is here too. Her eyelids didn't even flutter. Nothing.",
  "Nick slowly reached out and took her other hand, his huge fingers completely wrapping around her small, wrinkled palm. Hey Ma, he said, his voice a gravelly whisper.",
  "The ventilator hissed, rising and falling in her chest in a slow, mechanical rhythm. It was doing all the work. Her chest rose, then fell with a click.",
  "I watched the green lines on the monitor. They bounced in a steady, synthetic rhythm. It felt so artificial, as if her life was just a drawing on a screen.",
  "Nick bent over the bed, his forehead resting against the metal side rail. I could see his broad shoulders shaking under his leather jacket. He was crying silently.",
  "I had never seen Nick cry. Not when he was beaten, not when he was angry. Seeing him sob by Ma's bed broke something inside of me. I let my own tears fall.",
  "Ma looked completely peaceful, as if she was just taking a deep afternoon nap. No worries, no stress, no flustered questions about what dress to wear.",
  "I leaned down and kissed her forehead. She smelled faint, like her favorite lavender soap, but underneath it was the sharp, metallic smell of the hospital.",
  "Nick looked up, his face wet with tears, staring at P with raw vulnerability.",
  "P, she's not there, Nick whispered, looking up at me. His eyes were wide with a childlike terror. That's not Ma. Ma is gone. I don't know where she is.",
  "She's still here, Nick, I said, though my voice was trembling. She can hear us. The doctors say they can hear you. Just talk to her. Tell her you're here.",
  "Ma, I'm here, Nick said, his voice cracking. I took care of the house, Ma. I took you to mass, remember? St. Patrick's. You looked so pretty. I'm right here.",
  "We stood there, holding her hands, one brother on each side, the machines humming their endless, cold song. We were completely united in our vigil.",
  "We knew the next few days would decide everything. But standing there, holding Ma's hands, I knew that whatever happened, we were in this together."
]
c_70 = [CHAR_P_HOSPITAL, CHAR_M_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_M_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_M_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL]
s_70 = ["Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_N", "Character_N", "Character_P", "Character_N", "Character_P", "Character_P"]

scene_70 = {
    "scene_number": 70,
    "setting": "Day 271 - Madison General Hospital (Interior) - ICU / Life Support",
    "total_estimated_duration_seconds": 127,
    "scene_filename": "Scene_70_Day271_ICU_LifeSupport",
    "transition_type": "cut",
    "lighting_continuity_token": "Dim, cold room dominated by the glowing green and blue graphs of medical monitors and the soft orange light of a small bedside lamp",
    "music_bed": {
      "style_description": "Slow, rhythmic synthesizer pulse mimicking a heartbeat, overlaid with a soaring, tragic solo violin and delicate, weeping piano notes",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 21],
          ["The ICU Pulse", "Tragic Entrance", "Delicate Piano", "The Machine's Breath", "Overwhelming Sorrow", "The Rhythm Continues / Outro"],
          ["instrumental"]*6,
          [
              "A slow, low-frequency synth pulse starts, representing the heart monitor.",
              "A beautiful, tragic violin melody enters softly, full of grief.",
              "High, weeping piano notes pluck a slow melody alongside the violin.",
              "A soft, rhythmic synthesized hiss enters, mimicking the ventilator's bellows.",
              "Violin and piano swell together, creating a wave of pure emotional grief.",
              "The music fades back to just the slow, quiet, electronic synth heartbeat pulse."
          ]
      )
    },
    "veo_clips": []
}
scene_70["veo_clips"] = generate_scene_clips(70, 127, c_70, p_70, d_70, s_70, "the quiet, equipment-filled ICU room", 2416)
scenes_to_append.append(scene_70)


# --- SCENE 71: Sibling Vigil (127s / 18 clips) ---
p_71 = [
  "sit in plastic chairs on opposite sides of the room as night falls outside the window",
  "leans back in his chair, rubbing his eyes from exhaustion",
  "stares down at his boots, looking reflective",
  "looks over at his brother, listening with a gentle expression",
  "smiles softly, a fleeting moment of warmth in his eyes",
  "nods slowly, remembering a happy childhood moment",
  "frowns slightly, looking at his large calloused hands",
  "speaks with absolute sincerity, his voice low",
  "looks at the older brother, feeling a deep wave of respect",
  "shifts in his seat, looking at Ma's quiet form",
  "looks down at his hands, speaking softly",
  "shakes his head, looking surprised but pleased",
  "nods in agreement, his face filled with genuine emotion",
  "looks out the dark window reflecting their shapes",
  "looks back at the bed, feeling the weight of their bond",
  "reaches out to touch Ma's foot under the blanket",
  "sits forward, his hands clasped between his knees",
  "looks up as the door handle begins to move"
]
d_71 = [
  "Night fell over the hospital, wrapping the room in deep shadows. Sionna had gone down to the cafeteria to get us some lukewarm coffee, leaving the two of us alone.",
  "I sat in the vinyl armchair, my bones aching with exhaustion. Nick sat opposite, staring at his boots. He hadn't spoken for over an hour.",
  "P, he said suddenly, his voice scraping the silence. Remember that winter we lived in that drafty apartment on Main Street? The one where the furnace broke?",
  "I looked at him, surprised. Yeah, I remember. We slept in our coats for three days. You told me it was an indoor camping adventure to keep me from crying.",
  "Nick smiled, a tiny, fleeting movement. Yeah. I stole a bunch of wood from that construction site down the road and made a campfire in the yard. We roasted hotdogs.",
  "I laughed softly. We did. Ma was so mad about the stolen wood, but she ate the hotdogs anyway. You always made sure we had something, Nick.",
  "Nick looked down at his calloused hands. I didn't know how to do anything else, P. I was just a kid, but I knew I had to feed you. I used to sneak cheese from the store.",
  "My jaw tightened. I never knew that. You stole food for me? Nick shrugged. Of course I did. You were my little brother. That was my job.",
  "I stared at him, seeing the fierce, desperate boy he had been. He had carried the weight of our survival on his shoulders, and I had judged him for his rough edges.",
  "I'm sorry, Nick, I said quietly. I'm sorry I was so critical of you. I thought you just liked trouble. I didn't see what you were actually doing.",
  "Nick looked at me, his eyes wide and honest in the dim light. Don't be stupid, P. You were the smart one. You were supposed to study, not worry about food.",
  "I always knew you'd get out of this town, P, he murmured. You have a beautiful brain. You're going to write books. I'm just a brawler. But you, you're special.",
  "I felt a massive lump form in my throat. I'm not special, Nick. I'm just your brother. We're both Ma's boys. That's all that matters.",
  "Nick nodded slowly, looking over at Ma's peaceful face. Yeah. We are. She loved you so much, P. She used to show your report cards to anyone who would look.",
  "She was so proud of you, P. Even when she was flustered, she'd whisper to me: P is going to make us proud. He's going to be a real writer.",
  "I squeezed my eyes shut as the tears came again, warm and fast. I wished I could tell her that I was proud of her too. I wished I had said it more.",
  "You'll have your chance, P, Nick said softly. She knows. Mothers always know. She's resting now. Let's just sit with her.",
  "We sat together in the quiet room, the silent bond of brotherhood stronger than any machine, waiting together for the morning sun."
]
c_71 = [CHAR_P_HOSPITAL + ", " + CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL]
s_71 = ["Character_P", "Character_P", "Character_N", "Character_P", "Character_N", "Character_P", "Character_N", "Character_N", "Character_P", "Character_P", "Character_N", "Character_N", "Character_P", "Character_N", "Character_N", "Character_P", "Character_N", "Character_P"]

scene_71 = {
    "scene_number": 71,
    "setting": "Night 271 - Madison General Hospital (Interior) - Sibling Vigil",
    "total_estimated_duration_seconds": 127,
    "scene_filename": "Scene_71_Night271_ICU_SiblingVigil",
    "transition_type": "cut",
    "lighting_continuity_token": "Deep night shadows with only the rhythmic, blue-green glow of the heart monitor casting long, quiet shapes on the walls",
    "music_bed": {
      "style_description": "Whispering, atmospheric synthesizer pads with a very soft, sparse fingerpicked acoustic guitar and a gentle, comforting flute theme",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 21],
          ["Night Falls", "Vinyl Chairs", "Main Street Memories", "Stealing Wood", "Brothers Reconnected", "Quiet Sleep / Outro"],
          ["instrumental"]*6,
          [
              "A whispering atmospheric synth pad starts, establishing the quiet night.",
              "A very soft, sparse fingerpicked acoustic guitar enters, gentle and intimate.",
              "The guitar plucks a nostalgic minor-key melody, warm and reflective.",
              "A gentle, comforting wooden flute theme enters, expressing brotherly love.",
              "Guitar and flute play a sweet, peaceful harmony together in the dimness.",
              "Instruments fade out slowly, leaving a single sustained, warm synthesizer pad."
          ]
      )
    },
    "veo_clips": []
}
scene_71["veo_clips"] = generate_scene_clips(71, 127, c_71, p_71, d_71, s_71, "the dim ICU room in the quiet night", 2543)
scenes_to_append.append(scene_71)


# --- SCENE 72: The Prognosis (120s / 17 clips) ---
p_72 = [
  "sit in plastic chairs as an elderly doctor in a white coat sits opposite them at a walnut desk",
  "looks at the doctor, his face pale and alert",
  "listens intently, his hands tightly clasped on his lap",
  "adjusts his gold wire-rimmed glasses, looking grave",
  "looks at P, his forehead furrowing as he hears the diagnosis",
  "nods slightly as the doctor explains, his heart sinking",
  "stares down at his boots, a low growl of anger in his throat",
  "looks up, his jaw set in stubborn denial",
  "leans forward on the desk, explaining gently but firmly",
  "squeezes his eyes shut, trying to process the medical terms",
  "shakes his head, looking completely resistant to the truth",
  "looks at Nick, trying to offer silent comfort",
  "speaks with professional compassion, his hands flat on the desk",
  "watches the doctor's mouth move, the words feeling distant",
  "grips his armrest, looking like he wants to punch the wall",
  "looks at his brother, his eyes wide with a painful realization",
  "nods slowly to the doctor, acknowledging the finality"
]
d_72 = [
  "The next morning, the primary physician called us into his office. It was a small room with walnut bookshelves and a desk cluttered with medical charts.",
  "He took off his glasses and looked at us. His eyes were tired. I knew before he even spoke that there was no miracle waiting for us.",
  "The stroke was massive, he said, his voice flat and clinical. It affected her brainstem. The damage is irreversible, boys. She cannot recover.",
  "I felt a cold wind blow through my soul. But she's breathing, Nick protested, his voice rising, angry and defensive. The machine is doing it, Nick.",
  "The doctor explained that without the machines, her body would shut down. She has no brain activity. It's only the ventilator keeping her heart beating.",
  "I nodded slowly. I had known it, but hearing a professional say it made it a hard, concrete wall. There was no climbing over it.",
  "What are you saying? Nick demanded, standing up, his large frame towering over the doctor's desk. You're just going to give up on her?",
  "We don't give up, son, the doctor said gently, not backing down from Nick's anger. But there is a point where medical treatment becomes unnecessary suffering.",
  "He looked at both of us, his face soft. You need to think about what she would want. Would she want to live like this? Hooked to a machine?",
  "I thought about Ma. Ma, who was always flustered about the small stuff, but had a fierce, independent dignity. She would hate this.",
  "She wouldn't want to be a vegetable, I said quietly, my voice shaking. Nick looked at me, his eyes blazing. Shut up, P! You don't know what she wants!",
  "She's our mother! Nick roared, his chest heaving under his leather jacket. We don't pull the plug on our mother! We fight for her!",
  "Nick, there is no fight left, the doctor said, his voice a quiet, heavy hammer. The fight is over. Now it's about letting her go with peace.",
  "He told us to take some time, talk to each other, and make a decision. He patted Nick's shoulder as he walked out, leaving us in the quiet office.",
  "Nick stood there, his fists clenched, his breathing ragged. He looked like a cornered animal, ready to lash out at anyone who got close.",
  "I stood up and faced him. We have to talk about this, Nick, I said softly. We can't keep her alive for our sake if she's gone.",
  "He didn't reply. He just pushed past me, slamming the door behind him. I sat back down in the office, alone with the humming of the air vents."
]
c_72 = [CHAR_P_HOSPITAL + ", " + CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL]
s_72 = ["Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P"]

scene_72 = {
    "scene_number": 72,
    "setting": "Day 272 - Madison General Hospital (Interior) - The Prognosis",
    "total_estimated_duration_seconds": 120,
    "scene_filename": "Scene_72_Day272_Hospital_ThePrognosis",
    "transition_type": "cut",
    "lighting_continuity_token": "Pale morning daylight streaming through a dusty window pane, cutting across walnut bookshelves and a cluttered desk",
    "music_bed": {
      "style_description": "Cold, clinical piano chords accompanied by a low, dragging bass drone and a weeping solo oboe theme",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 14],
          ["Medical Office", "The Doctor's Voice", "Irreversible Damage", "Brainstem Stroke", "Letting Her Go", "Alone with Vents / Outro"],
          ["instrumental"]*6,
          [
              "Cold, slow clinical piano chords enter, establishing a somber setting.",
              "A low, dragging synthesized bass drone joins, heavy and immovable.",
              "A weeping solo oboe theme enters, full of tragic truth.",
              "The piano melody becomes slightly more fragmented and tense.",
              "The bass drone swells, emphasizing the gravity of the decision.",
              "The music fades down to a single sustained, cold oboe note."
          ]
      )
    },
    "veo_clips": []
}
scene_72["veo_clips"] = generate_scene_clips(72, 120, c_72, p_72, d_72, s_72, "the small walnut-walled doctor's office", 2670)
scenes_to_append.append(scene_72)


# --- SCENE 73: The Decision (127s / 18 clips) ---
p_73 = [
  "stand in the quiet hallway with pale yellow walls, speaking with hushed intensity",
  "paces back and forth, rubbing his temples, looking completely overwhelmed",
  "stands still, watching him, his face reflecting sorrow and heavy resolve",
  "stops pacing, staring out the glass pane of the corridor window, shoulders tight",
  "walks over to stand beside him, looking out the same window",
  "turns to face him, his eyes filled with a desperate, childish panic",
  "speaks with gentle but firm logic, placing a hand on his arm",
  "pulls his arm away, shaking his head in stubborn resistance",
  "takes a deep breath, trying to control his own breaking voice",
  "looks down at his clenched fists, his jaw trembling",
  "speaks softly, pleading with him to see her dignity",
  "leans his head against the glass pane, closing his eyes as a tear slides down",
  "steps closer, wrapping a strong arm around his shaking shoulders",
  "hugs his brother back, burying his face in his shoulder as he finally weeps",
  "holds him tight in the empty, silent hallway, tears in his own eyes",
  "pulls back slightly, wiping his face with the back of his hand",
  "looks into his eyes, nodding slowly with tragic acceptance",
  "both turn to look back at the ICU double doors, ready to proceed"
]
d_73 = [
  "I found Nick in the west wing corridor. He was pacing back and forth like a caged beast, his heavy boots squeaking on the tiles, his leather jacket rustling.",
  "I didn't say anything at first. I just stood there, letting him run out of energy. He needed to burn off the anger before he could face the truth.",
  "P, he said, stopping suddenly and staring at me. They want us to kill her. That's what it is. Removing the machine is killing her. I can't do that.",
  "I walked over and stood beside him by the window. The city below was busy, people driving to work, living normal lives. It felt so incredibly distant.",
  "It's not killing her, Nick, I said softly, my voice surprisingly steady. Her spirit is already gone. She's just being held here by plastic and electricity.",
  "What if they're wrong? he whispered, his eyes wide and wet. What if she wakes up? What if she's trapped inside her mind, screaming for us to help?",
  "I shook my head. The doctor showed us the brain scans, Nick. There's nothing left. She's not trapped. She's already at peace. We're the ones holding her back.",
  "But she's our mother! he choked out, his voice cracking. We're supposed to protect her! I'm supposed to protect both of you! That's what I do!",
  "I placed my hand on his arm. You have protected us, Nick. Your whole life. But protecting her now means letting her go with dignity, not keeping her as a machine.",
  "She would hate being hooked up to these things, Nick. You know how independent she is. She'd be so embarrassed to have people feeding and cleaning her.",
  "Nick looked down at his boots. His chest was heaving. He was trying so hard to hold onto the brawler, the tough guy, but the brawler had no power here.",
  "I saw a tear run down his cheek, dripping onto his leather sleeve. He looked so incredibly young, so vulnerable. I felt my own heart break for him.",
  "I stepped forward and wrapped my arms around him. It was a real hug, the kind we hadn't shared since we were little kids sleeping in the freezing cold bedroom.",
  "Nick didn't push me away. He grabbed onto my shirt, burying his face in my shoulder, and sobbed. He wept with a raw, gasping grief that shook his entire body.",
  "I held him tight, letting him cry, feeling the deep, ancient bond of our blood. We were the only ones who knew what it was to be Ma's boys.",
  "It's okay, Nick, I whispered, tears streaming down my own face. I've got you. We're going to let her rest. Together. It's the right thing to do.",
  "He slowly pulled back, wiping his face with his sleeve. He looked at me, his eyes bloodshot but clear of the anger. Okay, P, he whispered. Okay.",
  "We turned together and walked back toward the ICU double doors, our hands clasped tightly, ready to face the hardest walk of our lives."
]
c_73 = [CHAR_P_HOSPITAL + ", " + CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_N_HOSPITAL]
s_73 = ["Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_N", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_N", "Character_P"]

scene_73 = {
    "scene_number": 73,
    "setting": "Night 272 - Madison General Hospital (Interior) - The Decision",
    "total_estimated_duration_seconds": 127,
    "scene_filename": "Scene_73_Night272_Hospital_TheDecision",
    "transition_type": "cut",
    "lighting_continuity_token": "Moody, cold blue-grey night light flooding through a large corridor glass window pane, casting long shadows",
    "music_bed": {
      "style_description": "Tense, rising cello and viola duet, accompanied by low, dramatic synthesizer swells and a slow, sorrowful piano melody",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 21],
          ["Hallway Pacing", "Nick's Panic", "The Glass Window", "Sibling Embrace", "Letting Her Rest", "Tragic Acceptance / Outro"],
          ["instrumental"]*6,
          [
              "A tense, rising cello note enters, establishing the quiet hall.",
              "A viola joins the cello in a discordant, emotional harmony.",
              "Low, dramatic synthesizer swells enter, representing rising panic.",
              "A slow, sorrowful piano melody enters as the brothers embrace.",
              "The strings and piano harmonize beautifully, full of grief and relief.",
              "The music fades out slowly, leaving a single sustained, comforting string chord."
          ]
      )
    },
    "veo_clips": []
}
scene_73["veo_clips"] = generate_scene_clips(73, 127, c_73, p_73, d_73, s_73, "the quiet hospital west-wing corridor", 2790)
scenes_to_append.append(scene_73)


# --- SCENE 74: Saying Goodbye (120s / 17 clips) ---
p_74 = [
  "stand on opposite sides of Ma's metal ICU bed, holding her cool hands",
  "stands quietly near the door, her hands folded, weeping softly",
  "reaches out to gently stroke Ma's silver hair, speaking in a whisper",
  "leans down, kissing her pale cheek with pure love",
  "adjusts a dialysis machine in the background, looking solemn",
  "stands over Ma, his hand pressing a dial on the respirator panel",
  "reaches over to turn off the main switch of the mechanical ventilator",
  "watches the glowing screens of the monitor slowly turn blank and quiet",
  "lies peaceful, her silver hair spread, her chest no longer rising artificially",
  "takes a slow, shallow breath, her chest rising naturally one last time",
  "exhales a final, soft sigh, her face completely tranquil and relaxed",
  "squeezes her hand, tears streaming down his face as he watches her go",
  "stares down, his shoulders shaking as he squeezes her other hand",
  "watches the heart rate monitor flatline with a quiet, continuous tone",
  "turns off the heart monitor's audio, bringing a sudden, heavy silence",
  "both bend over her body, weeping together in a shared embrace of grief",
  "stands over her, looking peaceful in the dim, quiet hospital room"
]
d_74 = [
  "We gathered around her bed. Sionna stood near the doorway, a silent, comforting presence. The doctor and nurse stood at the foot of the bed, their faces grave.",
  "I held her left hand; Nick held her right. We looked at each other across her small, frail body, our hearts beating in a ragged, matching rhythm.",
  "We're ready, I whispered to the doctor. He nodded slowly and reached for the dials on the respirator panel. The machine's steady hum suddenly stopped.",
  "The quiet was deafening. The thick plastic tube was removed, and for a second, her chest was completely still. I held my breath, waiting.",
  "Then, she took a breath. A real, natural breath. It was slow, shallow, but it was all hers. Her chest rose, then fell with a soft, quiet sigh.",
  "She looked so incredibly peaceful. The tension in her face, the flustered worries of a lifetime, just melted away. She looked beautiful in the dim light.",
  "She took another breath, even shallower, as if she was slowly drifting away into a deep, warm ocean. She was letting go, and we were letting her.",
  "Nick squeezed her hand. I love you, Ma, he whispered, his voice incredibly sweet and gentle. Thank you for everything. You can sleep now. We'll be okay.",
  "I leaned down and kissed her cheek. Goodbye, Ma, I sobbed. I'll write our story, I promise. I'll make sure everyone knows how much we loved you.",
  "She took one final, tiny breath. It was barely a whisper. Her chest fell, and then... it didn't rise again. She was still. Completely still.",
  "The green lines on the heart monitor began to flatten. The rhythmic beeping turned into a single, long, continuous tone, echoing in the quiet room.",
  "The nurse reached over and turned off the monitor's alarm. The sudden silence was absolute. It felt as if the entire world had stopped spinning.",
  "The doctor checked her eyes and her chest, then looked up at us. Time of death, 10:42 AM, he said softly. I am so sorry for your loss, boys.",
  "I let go of her hand and stepped around the bed. Nick met me half-way, and we fell into each other's arms, weeping with a shared, pure heartbreak.",
  "We had lost our mother, our only anchor, the woman who had bound our chaotic lives together. We were completely alone in the world now.",
  "But as we held each other, sobbing in the quiet room, I knew we still had each other. We were brothers. Nick and P. And that would have to be enough.",
  "I looked back at Ma's quiet face. She looked like she was sleeping, beautiful and free. The energy that had animated her was gone, back to the vast universe."
]
c_74 = [CHAR_P_HOSPITAL + ", " + CHAR_N_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_M_HOSPITAL, CHAR_M_HOSPITAL, CHAR_M_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_N_HOSPITAL, CHAR_M_HOSPITAL, CHAR_M_HOSPITAL, CHAR_M_HOSPITAL, CHAR_P_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL, CHAR_P_HOSPITAL + ", " + CHAR_N_HOSPITAL, CHAR_M_HOSPITAL]
s_74 = ["Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P"]

scene_74 = {
    "scene_number": 74,
    "setting": "Day 273 - Madison General Hospital (Interior) - Saying Goodbye",
    "total_estimated_duration_seconds": 120,
    "scene_filename": "Scene_74_Day273_Hospital_SayingGoodbye",
    "transition_type": "fade_to_black",
    "lighting_continuity_token": "Quiet, sacred morning sunlight pouring through the ICU window, bathing Ma's bed in a peaceful, golden rectangle",
    "music_bed": {
      "style_description": "Gentle, soaring orchestral strings with a soft acoustic guitar and a beautiful, mournful solo violin melody, fading into absolute silence",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 14],
          ["Gathered Bedside", "The Ventilator Stops", "Natural Breath", "The Final Sigh", "Flatline Tone", "Mournful Silence / Outro"],
          ["instrumental"]*6,
          [
              "Gentle, soaring orchestral strings enter, soft and sacred.",
              "A quiet acoustic guitar joins, adding a warm, intimate layer.",
              "A mournful solo violin melody rises beautifully as her breath fades.",
              "The strings swell with an overwhelming, bittersweet sorrow.",
              "The music slows down, matching the flatline tone of the monitor.",
              "All instruments fade out slowly into a profound, respectful silence."
          ]
      )
    },
    "veo_clips": []
}
scene_74["veo_clips"] = generate_scene_clips(74, 120, c_74, p_74, d_74, s_74, "the quiet, sun-lit ICU room", 2917)
scenes_to_append.append(scene_74)


# --- SCENE 75: The Silent House (113s / 16 clips) ---
p_75 = [
  "walks through the apartment door carrying his duffel bag, his face heavy with grief",
  "stands near the entryway, holding a cardboard box of Ma's hospital belongings",
  "looks toward Ma's empty floral armchair, bathed in a dusty shaft of sunlight",
  "paces into the quiet kitchen, looking at Ma's empty coffee mug on the counter",
  "walks past P without a word, his face a dark, silent mask of pain",
  "enters his bedroom, slamming the wooden door shut with a heavy click",
  "stands in the hallway, staring at the closed bedroom door with a look of worry",
  "walks over to P, gently placing a warm mug of chamomile tea in his hands",
  "sits at the dark wooden kitchen table, staring blankly down at the tea steam",
  "sits beside P, gently rubbing his back in a slow, comforting circle",
  "looks out the dark kitchen window reflecting the quiet night",
  "closes his eyes, leaning his forehead against the cool table surface",
  "holds P's hand tightly, her light blue eyes filled with tears of empathy",
  "stares at Ma's empty floral armchair, feeling the heavy silence of the house",
  "looks toward the dark hallway, the quietness of the apartment overwhelming",
  "lies on the sofa under a knitted blanket, staring blankly at the ceiling"
]
d_75 = [
  "We returned to the apartment. The key turned in the lock with a loud, metallic click. We stepped inside, carrying the small box of Ma's belongings.",
  "The apartment was exactly the same as we had left it. Her reading glasses were still on the side table; her slippers were tucked under the chair.",
  "But she wasn't there. The silence in the house was a physical thing. It pressed against my ears, heavy, thick, and suffocating.",
  "Nick walked past me without saying a single word. He went straight into his bedroom and shut the door behind him. The click of his lock was final.",
  "I stood in the kitchen, staring at Ma's blue coffee mug sitting in the sink, half-filled with cold, dried liquid from Saturday morning.",
  "It was a small, stupid thing, but it broke me. She had made coffee, expecting to drink it, expecting to live. And now she was gone.",
  "Sionna came up behind me. She took the cup out of the sink, washed it gently, and put it away. Then she made me a mug of hot chamomile tea.",
  "Sit down, P, she whispered, guiding me to the kitchen table. You need to rest. You haven't slept in thirty-six hours. Sit with me.",
  "I sat at the wooden table, staring at the steam rising from the mug. I felt completely empty, as if my insides had been scooped out with a spoon.",
  "Her absence was larger than her presence had ever been. She had filled this house with her flustered movements, her laundry, her worries. Now, just empty air.",
  "I leaned my head against the cool wood of the table, feeling the smooth grain against my temple. The wood felt real, solid. I needed something solid.",
  "Sionna sat next to me, her hand warm and soft on my shoulder, rubbing in slow, comforting circles. She didn't try to fill the silence with useless words.",
  "She stayed there, a quiet, fierce presence, keeping the dark shadows of the empty house from swallowing me completely.",
  "I looked toward the dark hallway leading to her bedroom. I half-expected to see her shuffle out in her pink housecoat, asking what we wanted for dinner.",
  "But the hallway remained dark and silent. Ma was gone. There would be no more flustered questions, no more mass, no more lavender soap.",
  "I lay down on the sofa, pulling Ma's knitted blanket over my shoulders. I stared up at the plaster ceiling, watching the car headlights sweep past the window."
]
c_75 = [CHAR_P_FUNERAL, CHAR_S_FUNERAL, CHAR_P_FUNERAL, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_S_FUNERAL, CHAR_P_FUNERAL, CHAR_S_FUNERAL, CHAR_P_FUNERAL, CHAR_P_FUNERAL, CHAR_S_FUNERAL, CHAR_P_FUNERAL, CHAR_P_FUNERAL, CHAR_P_FUNERAL]
s_75 = ["Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_S", "Character_P", "Character_P", "Character_P", "Character_P", "Character_S", "Character_P", "Character_P", "Character_P"]

scene_75 = {
    "scene_number": 75,
    "setting": "Night 273 - Narrator's Apartment (Interior) - The Silent House",
    "total_estimated_duration_seconds": 113,
    "scene_filename": "Scene_75_Night273_Apt_TheSilentHouse",
    "transition_type": "cut",
    "lighting_continuity_token": "Moody, dark evening shadows swallowing the apartment living room, with only a warm light from the kitchen stove casting long, soft shapes",
    "music_bed": {
      "style_description": "Dissonant, quiet ambient synthesizer pads with a very sparse, melancholy acoustic guitar and hollow, distant keyboard chords",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 28],
          ["Empty Apartment", "Armchair in Shadow", "Closed Bedroom Door", "Washing the Mug", "Table Steam / Outro"],
          ["instrumental"]*5,
          [
              "Dissonant, quiet ambient synth pads enter, expressing suffocating silence.",
              "A very sparse, melancholy acoustic guitar plucks a few slow notes.",
              "The synthesizer pad swells slightly with a cold, hollow quality.",
              "Distant, hollow keyboard chords enter, slow and incredibly heavy.",
              "The music slowly fades down to a single sustained, empty synth note."
          ]
      )
    },
    "veo_clips": []
}
scene_75["veo_clips"] = generate_scene_clips(75, 113, c_75, p_75, d_75, s_75, "the quiet, shadow-filled apartment living room", 3037)
scenes_to_append.append(scene_75)


# --- SCENE 76: The Burial (120s / 17 clips) ---
p_76 = [
  "stands under a black umbrella in the rainy cemetery, dressed in a black funeral coat",
  "stands rigid beside P, staring blankly ahead in a dark funeral suit and white shirt",
  "stands on P's other side, weeping silently under a black umbrella",
  "lies suspended on thick straps over a freshly dug grave in the wet grass",
  "holds a single red rose, his fingers gripping the stem tightly",
  "holds a small black prayer book, his lips moving in silent recitation",
  "stands in the background under umbrellas, their heads bowed respectfully",
  "looks toward Nick, his heart breaking as he sees his brother's rigid posture",
  "steps forward, his large hand trembling as he holds the red rose over the grave",
  "drops the single red rose onto the polished wood of the coffin",
  "watches the rose land softly on the wood surface, raindrops splashing around it",
  "presses a lever on the metal lowering device, and the straps begin to slide",
  "watches the coffin slowly descend into the dark, wet earth of the grave",
  "leans her head against P's shoulder, weeping softly under the rain",
  "weeps silently, his shoulders shaking under the dark grey sweater",
  "stares down into the empty dark hole, his face a cold, unmoving stone",
  "all stand in a quiet circle under the falling rain, bidding a final farewell"
]
d_76 = [
  "The funeral was small and quiet. St. Patrick's was empty except for a few neighbors and the priest. Then we drove out to the cemetery in the pouring rain.",
  "We stood in a semi-circle under black umbrellas, the wet grass squelching under our dress shoes. The rain fell in a steady, gray, mournful sheet.",
  "Nick stood beside me. He had refused to wear a coat, standing in his dark suit, his reddish-brown hair getting soaked, staring at the coffin like a sentinel.",
  "The priest read the final prayers, his voice competing with the sound of the rain tapping on our umbrellas and the distant rumble of thunder.",
  "Ashes to ashes, dust to dust, he said, his hand making a sign of the cross over the polished wood. The words felt so incredibly old, so heavy.",
  "Nick stepped forward. He had a single red rose in his large hand. He stood at the edge of the open grave, staring down into the dark, wet earth.",
  "He held the rose for a long, quiet moment, as if he was trying to think of something to say. But he didn't speak. He just let the rose drop.",
  "I watched the rose fall, tumbling through the gray air to land on the center of the coffin lid. It looked so small, so bright against the dark wood.",
  "Then the straps creaked. The metal lowering device groaned softly, and the coffin began its slow, final descent into the dark ground.",
  "It sank slowly, disappearing inch by inch into the dark earth. It felt so incredibly permanent. This was the final door closing, once and for all.",
  "Sionna held my arm tightly, her black umbrella shielding both of us. She was crying softly, her tears merging with the raindrops on her cheeks.",
  "I put my arm around her, but my eyes were glued to the grave. I watched the wet dirt at the edge slide down, landing on the rose and the wood.",
  "Nick didn't move. He stood at the grave's edge, his hands shoved deep into his suit pockets, watching the coffin descend until it was completely out of sight.",
  "He looked like he wanted to jump in after her, or fight the dirt, or fight the rain. But there was nothing to fight. The silence of the earth had won.",
  "When the grave was filled, the priest shook our hands and left. The few neighbors walked back to their cars, leaving the three of us alone under the gray sky.",
  "I looked at Nick. His suit was soaked, his hair plastered to his forehead. Come on, Nick, I said gently. Let's go home. He didn't answer, just turned.",
  "He walked back to his Pontiac, his heavy steps slow and methodical. We got into the Civic, and followed him back to the empty, quiet apartment."
]
c_76 = [CHAR_P_FUNERAL, CHAR_N_FUNERAL, CHAR_S_FUNERAL, CHAR_M_HOSPITAL, CHAR_N_FUNERAL, CHAR_P_FUNERAL, CHAR_S_FUNERAL, CHAR_P_FUNERAL, CHAR_N_FUNERAL, CHAR_N_FUNERAL, CHAR_N_FUNERAL, CHAR_P_FUNERAL, CHAR_P_FUNERAL, CHAR_S_FUNERAL, CHAR_P_FUNERAL, CHAR_N_FUNERAL, CHAR_P_FUNERAL + ", " + CHAR_N_FUNERAL]
s_76 = ["Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P"]

scene_76 = {
    "scene_number": 76,
    "setting": "Day 274 - Madison Cemetery (Exterior) - The Burial",
    "total_estimated_duration_seconds": 120,
    "scene_filename": "Scene_76_Day274_Cemetery_TheBurial",
    "transition_type": "cut",
    "lighting_continuity_token": "Cold, gloomy, overcast daylight with a constant, gray rain misting the green hills of the cemetery",
    "music_bed": {
      "style_description": "Sparse, mournful solo cello melody with a low synthesizer sub-bass drone and the rhythmic, wet sound of falling rain",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 14],
          ["Cemetery Rain", "Ashes to Ashes", "The Rose Drop", "Coffin Descends", "Wet Earth", "Gray Departure / Outro"],
          ["instrumental"]*6,
          [
              "A sparse, mournful solo cello melody enters, establishing the wet cemetery.",
              "A low synthesized sub-bass drone joins, heavy and immovable.",
              "The cello plays a beautiful, tragic descending pattern as the rose falls.",
              "A soft, rhythmic synthesizer pad swells, full of deep sorrow.",
              "The cello melody becomes slow, solemn, and incredibly quiet.",
              "The music slowly fades into the sound of falling rain and gray wind."
          ]
      )
    },
    "veo_clips": []
}
scene_76["veo_clips"] = generate_scene_clips(76, 120, c_76, p_76, d_76, s_76, "the wet, rain-swept cemetery graveside", 3150)
scenes_to_append.append(scene_76)


# --- SCENE 77: The Outburst (127s / 18 clips) ---
p_77 = [
  "sits on the sofa, holding an empty glass bottle, his face flushed and angry",
  "sits at the kitchen table, watching him with growing fear and worry",
  "stands near the hallway, her face tight with anxiety as she watches the brothers",
  "takes a swig from a bottle of whiskey, his eyes wild and bloodshot",
  "stands up, pointing an angry finger at P, his breathing ragged",
  "stands up to face him, his own face tight with rising anger",
  "slams his whiskey bottle down on the side table, shattering a ceramic lamp",
  "flinches at the sudden crash of the lamp, stepping back toward the kitchen",
  "shouts at P, his face contorted in a mask of grief and fury",
  "shouts back, his hands clenched at his sides, refusing to back down",
  "glares at P, his chest heaving under his black leather jacket",
  "speaks with trembling anger, laying bare all his old childhood hurts",
  "steps forward, grabbing P's collar with a violent yank of his large hand",
  "reaches out to try and intervene, her voice filled with panic",
  "shoves her back gently but firmly with his other arm, shouting in her face",
  "holds P's collar tightly, shaking him with a terrifying, wild energy",
  "stares into his brother's wild eyes, refusing to show fear despite the threat",
  "glare at each other, the tension in the room reaching an explosive peak"
]
d_77 = [
  "Back at the apartment, the silence exploded. Nick had changed back into his leather jacket and jeans. He sat on the sofa with a bottle of whiskey, drinking fast.",
  "He was getting drunk, and he was getting mean. The grief inside of him was turning into a violent, toxic poison. I could feel the heat radiating off him.",
  "You're so quiet, P, he sneered suddenly, his voice loud and slurred. The writer has nothing to say? No big words for Ma's funeral? No smart stories?",
  "I didn't answer. I just sat at the kitchen table, watching him. This wasn't the Nick who held Ma's hand. This was the wild, dangerous brawler.",
  "Nick stood up, stumbling slightly, and pointed a finger at me. You think you're better than me, don't you? With your college classes and your pretty girlfriend!",
  "I stood up to face him. I was tired of being afraid of him. I was tired of his chaos. Shut up, Nick, I said, my voice cold. Just shut the hell up.",
  "He roared, a wild, animal sound, and slammed his whiskey bottle down. It missed the table, hitting Ma's favorite ceramic lamp and shattering it into a thousand pieces.",
  "Sionna gasped, stepping back into the hallway, her face white with terror. She wanted to help, but she knew that entering this arena was dangerous.",
  "Why didn't you save her, P? Nick screamed, tears springing to his wild eyes. You're the smart one! Why didn't you do something? Why did you let her die?",
  "I felt a wave of hot, blinding fury crash through my chest. How dare he? How dare he blame me? I did everything, Nick! I took care of her while you were out fighting!",
  "I screamed back, my voice cracking with the strain. I was the one who was here! You were never here, Nick! You only showed up when you wanted to play hero!",
  "Nick's face turned purple. He lunged across the room. Before I could move, his large hand shot out, grabbing my collar and yanking me forward with massive force.",
  "He pulled me so close I could smell the hot whiskey on his breath. His eyes were wide, bloodshot, and completely insane with grief and rage.",
  "Nick, stop! Sionna screamed, running forward to grab his leather sleeve. Let him go! You're hurting him! Nick, please!",
  "Nick shoved her back, not hard, but enough to send her stumbling against the wall. Leave us alone, Sionna! he roared. This is between me and my brother!",
  "He turned back to me, shaking me by my collar, his teeth bared in a snarl. I should have broken your neck years ago, P! You're a coward! A useless coward!",
  "I didn't flinch. I stared straight into his bloodshot eyes, my hands gripping his wrists, my own face contorted in a matching mask of fury.",
  "Do it then, Nick! I screamed in his face. Do it! Hit me! Prove that you're just a monster! That's all you know how to do anyway!"
]
c_77 = [CHAR_N_HOSPITAL, CHAR_P_FUNERAL, CHAR_S_FUNERAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_FUNERAL, CHAR_N_HOSPITAL, CHAR_S_FUNERAL, CHAR_N_HOSPITAL, CHAR_P_FUNERAL, CHAR_N_HOSPITAL, CHAR_P_FUNERAL, CHAR_N_HOSPITAL, CHAR_S_FUNERAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_FUNERAL, CHAR_P_FUNERAL + ", " + CHAR_N_HOSPITAL]
s_77 = ["Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_N", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P", "Character_S", "Character_N", "Character_N", "Character_P", "Character_P"]

scene_77 = {
    "scene_number": 77,
    "setting": "Night 274 - Narrator's Apartment (Interior) - The Outburst",
    "total_estimated_duration_seconds": 127,
    "scene_filename": "Scene_77_Night274_Apt_TheOutburst",
    "transition_type": "cut",
    "lighting_continuity_token": "Harsh, unshaded yellow light bulb casting aggressive, jagged shadows across the messy living room",
    "music_bed": {
      "style_description": "Aggressive, distorted electric guitar chords with a rapid, chaotic drum beat and a screaming synthesizer drone",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 21],
          ["Tension Builds", "Whiskey Rage", "Ceramic Shatters", "Grief and Fury", "Collar Grab", "Explosive Peak / Outro"],
          ["instrumental"]*6,
          [
              "Aggressive, distorted electric guitar chords enter, tense and heavy.",
              "A rapid, chaotic drum beat starts, building a sense of physical danger.",
              "A screaming synthesizer drone rises, mimicking a high-register scream.",
              "The guitar and drums play a heavy, driving rock rhythm, intense and chaotic.",
              "The music reaches a deafening, discordant peak as the brothers clash.",
              "The rock beat stops suddenly, leaving a ringing, tense guitar feedback."
          ]
      )
    },
    "veo_clips": []
}
scene_77["veo_clips"] = generate_scene_clips(77, 127, c_77, p_77, d_77, s_77, "the messy, shadow-filled apartment living room", 3270)
scenes_to_append.append(scene_77)


# --- SCENE 78: The Fistfight (127s / 18 clips) ---
p_78 = [
  "reaches out and shoves Nick back with all his strength, breaking his collar hold",
  "stumbles back against the dining table, his face a wild mask of shock and fury",
  "lunges forward, throwing a wild, heavy punch toward P's head",
  "ducks his head, the heavy fist grazing his ear, and throws a wild punch back",
  "feels P's fist hit his jaw with a sharp crack, his head snapping sideways",
  "tackle each other, crashing heavily into the dark wooden dining table",
  "groan as they tumble to the floorboards amidst splintered wood and broken glass",
  "stands in the doorway, her hands over her mouth, screaming in terror and panic",
  "grapple on the floor, rolling over and over in a chaotic tangle of limbs",
  "grabs P's throat, Pinning him down onto the floor, his face red with effort",
  "struggles violently beneath him, scratching and clawing at Nick's large arms",
  "stops suddenly, his heavy fist raised in the air, his face contorting in pain",
  "stares up at him, panting heavily, his face wet with tears and sweat",
  "slowly lowers his raised fist, his shoulders beginning to shake violently",
  "bends his head down, resting his forehead against P's chest as he bursts into tears",
  "wraps his arms around Nick's broad shoulders, clutching his brother tightly",
  "both lie on the floorboards in the wreckage of the table, weeping in a shared embrace",
  "hold each other tightly, the physical fight completely resolved in shared grief"
]
d_78 = [
  "I didn't wait for him to hit me. I reached out and shoved him back with all my strength, breaking his grip on my collar. He stumbled back against the table.",
  "Nick looked at me, completely shocked. Nobody shoved Nick. Nobody stood up to him. His face contorted in a wild, terrifying mask of pure fury.",
  "He lunged, throwing a massive, wild punch. I ducked, feeling the wind of his fist pass my ear, and threw a punch of my own, straight from the shoulder.",
  "My fist hit his jaw with a sharp, satisfying crack. His head snapped sideways. But instead of falling, he roared and tackled me around the waist.",
  "We crashed into the dark wooden dining table. The old wood splintered with a deafening bang, and we both tumbled to the floor in a heap of limbs and glass.",
  "We rolled over and over on the floorboards, grabbing, scratching, clawing. We weren't brothers anymore. We were two animals fighting for survival.",
  "Sionna was screaming in the background, her voice a distant, panic-filled siren. But we didn't hear her. We were locked in our own private war.",
  "Nick rolled on top of me, Pinning my shoulders to the floor with his massive weight. He raised his heavy fist, ready to smash my face in.",
  "I stared up at his fist, my heart pounding, refusing to close my eyes. Hit me, Nick! I choked out. Hit me! But he didn't. He froze.",
  "His fist remained raised in the air, trembling. His face contorted, not with anger, but with an overwhelming, unbearable wave of pure pain.",
  "His mouth opened in a silent, silent scream of agony. He looked at his fist, then at my face, and his large shoulders suddenly collapsed, shaking violently.",
  "He lowered his fist. He let go of my collar. And then, he fell forward, his forehead resting against my chest, and he wept.",
  "He sobbed with a raw, gasping heartbreak that shook his entire frame. He wasn't the brawler anymore. He was just a boy who had lost his mother.",
  "I lay there on the cold floorboards, panting heavily, my chest rising and falling beneath his weight. The anger inside of me just evaporated, leaving only sorrow.",
  "I reached up and wrapped my arms around his broad shoulders, holding him tightly against me. It's okay, Nick, I whispered, my own tears falling fast.",
  "I've got you, big bro. I'm right here. We're going to be okay. He gripped my shirt, burying his face in my chest, crying like a child.",
  "We lay there in the wreckage of our dining table, surrounded by splintered wood and broken glass, two brothers holding each other in the dark, quiet room.",
  "The war was over. The anger was gone. All that was left was the shared, pure grief of our loss, and the silent, unbreakable bond of our blood."
]
c_78 = [CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_P_FUNERAL + ", " + CHAR_N_WORK, CHAR_P_FUNERAL + ", " + CHAR_N_WORK, CHAR_S_FUNERAL, CHAR_P_FUNERAL + ", " + CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_P_FUNERAL + ", " + CHAR_N_WORK, CHAR_P_FUNERAL + ", " + CHAR_N_WORK]
s_78 = ["Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_S", "Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P"]

scene_78 = {
    "scene_number": 78,
    "setting": "Night 274 - Narrator's Apartment (Interior) - The Fistfight",
    "total_estimated_duration_seconds": 127,
    "scene_filename": "Scene_78_Night274_Apt_TheFistfight",
    "transition_type": "cut",
    "lighting_continuity_token": "Dim, dramatic shadows stretching across the floorboards, illuminated by the single unshaded yellow bulb hanging overhead",
    "music_bed": {
      "style_description": "Violent, driving rock beat with screaming guitar feedback, transitioning into a slow, emotional solo cello and tender piano notes",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 21],
          ["The First Blow", "Table Splinters", "Floorboard Grapple", "Fist Raised", "The Collapse", "Crying in Wreckage / Outro"],
          ["instrumental"]*6,
          [
              "A violent, driving rock beat continues with screaming guitar feedback.",
              "A sudden, dramatic orchestral stab matches the crash of the table.",
              "Guitar feedback screeches wildly as they roll and struggle on the floor.",
              "The rock beat stops suddenly, replaced by a tense, ringing silence.",
              "A slow, emotional solo cello enters softly as Nick collapses.",
              "Tender, sad piano notes harmonize with the cello in the wreckage."
          ]
      )
    },
    "veo_clips": []
}
scene_78["veo_clips"] = generate_scene_clips(78, 127, c_78, p_78, d_78, s_78, "the messy, damaged apartment living room", 3397)
scenes_to_append.append(scene_78)


# --- SCENE 79: Morning Light (120s / 17 clips) ---
p_79 = [
  "sweeps splintered wood and ceramic shards into a small dustpan on the floor",
  "stands beside him, holding a dark plastic trash bag, her face calm and serene",
  "stands by the stove, pouring steaming coffee from a glass pot into two mugs",
  "sits on the front porch step, staring quiet and thoughtful at the street",
  "walks out onto the porch, carrying the two steaming ceramic mugs",
  "sits beside Nick on the wooden step, handing him one of the mugs",
  "takes the mug, looking at the steaming coffee with a quiet, peaceful expression",
  "sip their coffee together in the cool morning air, watching the street",
  "looks at P, his face clean and quiet, the wild brawler completely gone",
  "smiles softly, a warm and mature expression on his face",
  "speaks with gentle resolve, staring out at the green trees across the road",
  "nods slowly in agreement, his own face filled with quiet peace",
  "reaches out to squeeze Nick's shoulder with a strong, brotherly grip",
  "places his hand over P's hand, squeezing it back in silent gratitude",
  "stand up on the porch, stretching under the beautiful morning sun",
  "both look out toward the street, a sense of healing and clean slate in the air",
  "stands in the doorway, watching the brothers with a peaceful, loving smile"
]
d_79 = [
  "The next morning, the apartment was bathed in soft, beautiful spring daylight. The storm had passed, leaving the air outside clean, fresh, and quiet.",
  "I spent the morning sweeping up the broken glass and splintered wood of our dining table. Sionna helped me, her presence a calm, healing balm.",
  "We didn't talk about last night. We didn't need to. The air in the house was different now – the thick, suffocating fog of grief had cleared.",
  "I saw Nick sitting on the porch steps. He was dressed in his grey work shirt and jeans, staring quiet and thoughtful at the street. He looked peaceful.",
  "I poured two mugs of black coffee and walked out to join him. I sat down on the wooden step beside him and handed him a mug. He took it with a soft nod.",
  "Thanks, P, he whispered, taking a slow sip. We sat in silence for a long time, watching the morning sun filter through the green maple leaves across the street.",
  "I'm sorry about the table, P, he said quietly, not looking at me. I'll buy a new one before I go. I nodded. Don't worry about it, Nick. It was old anyway.",
  "He looked at me. His face was clean, the wild brawler completely gone, replaced by a quiet, mature dignity. P, he said, you're a good brother.",
  "I smiled, taking a sip of my coffee. You're not so bad yourself, Nick. Even if you are a mean son of a bitch. He laughed, a quiet, genuine sound.",
  "It was the first time we had laughed since Ma died. It felt so incredibly good, like a warm light turning on in a dark, dusty cellar.",
  "I had to think: we had survived the nightmare. We had clashed, we had broken, but we had held together. The blueprint of Nick and P was still intact.",
  "And standing there, looking at him, I knew that whatever happened next, we would be okay. We had Ma's strength inside of us. We were survivors.",
  "Nick squeezed my shoulder. I'm going to pack my things, P, he said gently. I need to clear my head for a while. Get out of Madison. I nodded.",
  "Where are you going? I asked. He shrugged. Up north. Maybe work on an oil rig, or a barge on the Mississippi. Somewhere big. Somewhere quiet.",
  "I knew he had to go. Madison was too small for him now, filled with too many ghosts. He needed a wider horizon to find his own path.",
  "I'll look after the apartment, P, he said, standing up and stretching. You look after Sionna. She's a keeper. I smiled, looking back at her in the doorway.",
  "Sionna stood in the door frame, watching us with a beautiful, peaceful smile. She looked like a spring flower under the morning sun. Perfect."
]
c_79 = [CHAR_P_FUNERAL, CHAR_S_FUNERAL, CHAR_S_FUNERAL, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_P_FUNERAL + ", " + CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL + ", " + CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_P_FUNERAL + ", " + CHAR_N_WORK, CHAR_P_FUNERAL + ", " + CHAR_N_WORK, CHAR_S_FUNERAL]
s_79 = ["Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P"]

scene_79 = {
    "scene_number": 79,
    "setting": "Day 275 - Narrator's Apartment (Interior) - Morning Light",
    "total_estimated_duration_seconds": 120,
    "scene_filename": "Scene_79_Day275_Apt_MorningLight",
    "transition_type": "cut",
    "lighting_continuity_token": "Bright, warm, beautiful spring daylight flooding through the porch doors, creating a peaceful, golden atmosphere",
    "music_bed": {
      "style_description": "Warm, peaceful acoustic-orchestral harmony with gentle piano chords, soft cello, and a soaring, beautiful flute theme",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 14],
          ["Morning Sweep", "Clean Slate", "Coffee Pour", "Porch Steps", "Spring Sun", "A New Horizon / Outro"],
          ["instrumental"]*6,
          [
              "Warm, peaceful acoustic guitar strumming enters, gentle and bright.",
              "A soft piano melody joins, adding a warm, contemplative layer.",
              "A beautiful, soaring wooden flute theme enters, full of hope and healing.",
              "A quiet, melodic cello notes join, grounding the peaceful atmosphere.",
              "All instruments harmonize beautifully under the morning sun.",
              "The music resolves slowly on a warm, sustained, cozy chord."
          ]
      )
    },
    "veo_clips": []
}
scene_79["veo_clips"] = generate_scene_clips(79, 120, c_79, p_79, d_79, s_79, "the clean, sun-lit apartment front porch", 3524)
scenes_to_append.append(scene_79)


# Read existing JSON file
with open('nickandme.clips.grok.json', 'r') as f:
    data = json.load(f)

# Append new scenes (70-79)
for sc in scenes_to_append:
    data['scenes'].append(sc)

# Update metadata
data['next_scene_number'] = 80
data['cumulative_duration_seconds'] = 2416 + 127 + 127 + 120 + 127 + 120 + 113 + 120 + 127 + 127 + 120

with open('nickandme.clips.grok.json', 'w') as f:
    json.dump(data, f, indent=2)

print("Scenes 70-79 generated programmatically on disk!")
print(f"New cumulative duration: {data['cumulative_duration_seconds']} seconds ({data['cumulative_duration_seconds'] // 60}m {data['cumulative_duration_seconds'] % 60}s)")
