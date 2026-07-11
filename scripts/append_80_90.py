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

# --- SCENE 80: The Departure (127s / 18 clips) ---
p_80 = [
  "stands in his bedroom doorway, dressed in his grey work shirt, packing a heavy canvas duffel bag",
  "lays folded plaid shirts into the dark canvas bag on his mattress",
  "watches him silently from the hallway, leaning against the door frame",
  "shoves a pair of heavy work boots into the bag, his face serious and focused",
  "looks over at P, a quiet and reflective expression in his light brown eyes",
  "nods slowly, crossing his arms over his thin chest",
  "zips the duffel bag shut, the metallic sound echoing in the quiet bedroom",
  "stands up, lifting the heavy duffel bag by its canvas strap, throwing it over his shoulder",
  "walks out of his bedroom into the hallway, standing face to face with P",
  "looks at P, speaking with surprising softness and mature emotional depth",
  "listens with tears swelling in his eyes, his face full of quiet respect",
  "steps closer to P, placing his large calloused hand gently on P's shoulder",
  "looks down, his voice raspy as he speaks from his heart",
  "looks up into his brother's face, trying to commit every detail to memory",
  "pats P's shoulder with a firm, comforting pat",
  "turns and walks down the hallway carrying his heavy duffel bag toward the kitchen",
  "watches his brother walk away, feeling the shift in their relationship",
  "follows him down the hall, carrying Nick's leather jacket from the coat rack"
]
d_80 = [
  "After we finished our coffee on the porch, Nick went back inside to pack. He was fast, methodical, and quiet. There was no hesitation in his movements.",
  "He was packing light – just a few work shirts, jeans, and his heavy boots. Everything else he was leaving behind. He didn't want the clutter.",
  "I watched him from the doorway. It felt so strange to see him leaving, not in a rush, not fleeing the police, but just choosing to walk away.",
  "You're really going? I asked quietly. He didn't look up, just shoved a heavy pair of socks into the canvas bag. Yeah, P. It's time. I need a change.",
  "There's nothing left for me here, he said, his voice quiet. The bar is slow, the guys are getting old, and... Ma is gone. The house is empty.",
  "I nodded. I understood. Madison was filled with her memory. Every corner of this apartment, every street we drove on, had her face on it.",
  "He zipped the heavy bag. The sharp, metallic sound of the zipper was like a period at the end of a very long, very loud chapter in our lives.",
  "He slung the duffel over his broad shoulder. He looked like a soldier preparing for a long deployment. Brave, silent, and completely focused.",
  "He walked out of his room and stood in front of me in the hallway. P, he said, looking straight into my eyes. You're the smart one. You know that, right?",
  "You're the one who is going to do great things. You're going to write books, go to graduate school, make something of yourself. Don't waste it.",
  "I felt a hot lump in my throat. I'm just trying to get through the semester, Nick. I'm not special. But he shook his head, squeezing my shoulder.",
  "Yes, you are, he said, his voice a gravelly whisper. You have a beautiful mind, P. Ma knew it. I knew it. That's why I always fought for you.",
  "I wanted to keep you clean. I wanted you to have the chances I never had. I did some bad things, P, but protecting you was the best thing I ever did.",
  "I stared at him, my heart breaking with a wave of deep gratitude. He had been my shield, my warrior, my mean, brawling brother, and he had done it all for me.",
  "Thank you, Nick, I whispered, my voice breaking. Thank you for everything. He smiled, a tiny, genuine movement, and patted my shoulder.",
  "Don't get mushy on me, kid, he murmured. You've got work to do. Look after Sionna. She's the best thing that ever happened to you. I smiled.",
  "I will, Nick. I promise. He nodded, satisfied, and walked down the hallway toward the front door. He was ready. His path was set.",
  "I grabbed his black leather jacket from the coat rack and followed him out. The jacket felt heavy, full of the brawler's energy, but it was time for him to carry it somewhere else."
]
c_80 = [CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_N_WORK, CHAR_N_WORK + ", " + CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_N_WORK, CHAR_P_FUNERAL, CHAR_P_FUNERAL]
s_80 = ["Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_N", "Character_N", "Character_P", "Character_N", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P"]

scene_80 = {
    "scene_number": 80,
    "setting": "Day 275 - Narrator's Apartment (Interior) - The Departure",
    "total_estimated_duration_seconds": 127,
    "scene_filename": "Scene_80_Day275_Apt_TheDeparture",
    "transition_type": "cut",
    "lighting_continuity_token": "Beautiful morning sunlight streaming through the bedroom window, casting a warm, dusty rectangular beam over the mattress",
    "music_bed": {
      "style_description": "Melancholy fingerpicked acoustic guitar with a warm electric piano rhythm and a soft, supporting cello harmony",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 21],
          ["Packing Bag", "Soldier's Duffel", "Hallway Speech", "The Smart One", "Shield of Brotherhood", "Packing Up / Outro"],
          ["instrumental"]*6,
          [
              "A melancholy fingerpicked acoustic guitar melody enters, soft and rhythmic.",
              "A warm electric piano chord sequence joins, adding a reflective layer.",
              "A gentle, supporting cello harmony enters as Nick speaks in the hallway.",
              "The guitar and piano play a sweet, bittersweet melody, full of love.",
              "The cello swells beautifully, emphasizing the depth of their sibling bond.",
              "All instruments fade slowly, leaving a single sustained, warm piano chord."
          ]
      )
    },
    "veo_clips": []
}
scene_80["veo_clips"] = generate_scene_clips(80, 127, c_80, p_80, d_80, s_80, "the quiet apartment bedroom and hallway", 3877)
scenes_to_append.append(scene_80)


# --- SCENE 81: Brotherly Farewell (120s / 17 clips) ---
p_81 = [
  "walk out onto the apartment front porch, carrying his heavy canvas duffel bag",
  "stands on the porch steps, dressed in his black funeral blouse, waving a warm farewell",
  "stands beside Character_S, looking on with deep, mature emotion",
  "turns to face P on the porch step, a small, genuine smile on his face",
  "steps forward, wrapping his large arms around P in a tight, protective hug",
  "embraces Nick back with equal strength, his face tight with emotion",
  "holds his brother tightly, a tear escaping his eye onto Nick's shoulder",
  "pulls back, looking into P's eyes with a mixture of pride and warning",
  "shrugs on his black leather jacket, looking like the warrior chameleon once more",
  "walks down the wooden steps carrying his duffel bag toward his parked Pontiac",
  "gently slides his heavy canvas bag into the back seat of his car",
  "stands in the doorway of his Pontiac, looking back at P and Sionna one last time",
  "waves his hand with a small nod of farewell before stepping inside the car",
  "settles behind the steering wheel, his face resolute as he turns the key",
  "stare from the porch steps as the polished Pontiac engine roars to life with a rumble",
  "drives away down the sun-dappled country road, kicking up a tiny trail of dust",
  "stands on the porch step, holding Character_S's hand, watching until the car disappears"
]
d_81 = [
  "We walked out onto the porch together. The morning was absolutely beautiful. The air smelled of wet pine and fresh cut grass, clean and crisp.",
  "Sionna was already waiting for us. She stood on the wooden step, her short brown hair catching the bright sun. She looked at Nick with quiet warmth.",
  "Nick walked up to her first. He dropped his bag and held out his large, rough hand. Thanks, Sionna, he said softly. For looking after my brother.",
  "Sionna didn't shake his hand. She stepped forward and gave him a big, tight hug, burying her face in his plaid shirt. You look after yourself, Nick, she whispered.",
  "Nick looked at me, a tiny, genuine smile on his face. Then he stepped forward and wrapped his massive arms around me. It was a strong, protective hug.",
  "I held him back with everything I had. We stood there on the porch steps, two brothers holding onto each other, letting years of competition melt away.",
  "I'm going to miss you, big bro, I whispered in his ear. He squeezed me tighter. I'll miss you too, kid. But you've got work to do. Make Ma proud.",
  "He pulled back, his light brown eyes shining with pride. Keep your nose in the books, P. You're going to be a real writer. I know it.",
  "He shrugged on his black leather jacket, the polished metal studs catching the bright morning light. The Viking hero, the warrior, was back.",
  "He picked up his heavy bag and walked down the wooden steps. His heavy boots thudded on the planks in a slow, rhythmic, confident stride.",
  "He opened the rear door of his polished Pontiac and threw the duffel onto the vinyl seat. He looked so independent, so free.",
  "He stood by the driver's door, looking back at the two of us standing on the porch steps. He gave us a final, solemn nod of his head.",
  "See you around, kid, he called out. He slid into the driver's seat, and the heavy metal door clicked shut with a solid, familiar thud.",
  "The engine of the Pontiac roared to life with a deep, powerful rumble that shook the porch railings. The exhaust hummed in the morning air.",
  "I watched the tires roll forward, backing out of the driveway into the leafy street. The polished chrome of his bumper gleamed under the sun.",
  "He drove away down the leafy avenue, the Pontiac moving fast and confident, disappearing into the golden morning horizon. He was gone.",
  "I stood on the porch, watching the empty street. Sionna slipped her hand into mine, her warm fingers anchoring me. We stood together, facing the future."
]
c_81 = [CHAR_N_WORK, CHAR_S_FUNERAL, CHAR_P_FUNERAL, CHAR_N_WORK + ", " + CHAR_P_FUNERAL, CHAR_N_WORK + ", " + CHAR_P_FUNERAL, CHAR_N_WORK + ", " + CHAR_P_FUNERAL, CHAR_P_FUNERAL, CHAR_N_WORK, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_N_HOSPITAL, CHAR_P_FUNERAL + ", " + CHAR_S_FUNERAL, CHAR_N_HOSPITAL, CHAR_P_FUNERAL + ", " + CHAR_S_FUNERAL]
s_81 = ["Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P", "Character_N", "Character_P", "Character_P", "Character_P", "Character_P"]

scene_81 = {
    "scene_number": 81,
    "setting": "Day 275 - Narrator's Apartment (Exterior) - Brotherly Farewell",
    "total_estimated_duration_seconds": 120,
    "scene_filename": "Scene_81_Day275_Apt_BrotherlyFarewell",
    "transition_type": "cut",
    "lighting_continuity_token": "Bright, warm morning sun filtering through green maple leaves, bathing the wooden porch in golden light and casting soft shadows",
    "music_bed": {
      "style_description": "Soaring, beautiful orchestral violin with a driving, hopeful acoustic guitar pattern and warm, resolving piano chords",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 14],
          ["Porch Farewell", "Sionna's Hug", "Brotherly Embrace", "The Pontiac Engine", "Driving Away", "The Horizon / Outro"],
          ["instrumental"]*6,
          [
              "Bright, hopeful acoustic guitar strumming enters, warm and rhythmic.",
              "A soaring, beautiful orchestral violin melody joins, full of emotion.",
              "The violin and guitar build in a warm, beautiful crescendo as they embrace.",
              "The music adapts, adding a deep, rolling electric bass matching the Pontiac.",
              "The melody slows down, resolving beautifully on a peaceful theme.",
              "All instruments fade out slowly, leaving a single sustained, comforting violin note."
          ]
      )
    },
    "veo_clips": []
}
scene_81["veo_clips"] = generate_scene_clips(81, 120, c_81, p_81, d_81, s_81, "the sunny apartment wooden front porch", 4004)
scenes_to_append.append(scene_81)


# --- SCENE 82: Sionna's Counsel (120s / 17 clips) ---
p_82 = [
  "sits slumped on a wool rug in a dim, shadow-filled living room with closed blinds",
  "stands near the window in her green tank top, looking at P with deep concern",
  "reaches out to pull a plastic cord, opening the beige blinds to let daylight pour in",
  "blinks and shields his eyes as a massive wave of golden sunlight floods the room",
  "walks over carrying a small white ceramic bowl of steaming vegetable soup",
  "kneels beside P on the woolen rug, gently placing the bowl on his lap",
  "stares down blankly at the steaming vegetable soup, his thin frame hollowed",
  "gently lifts a spoon of soup, holding it to P's lips with a patient smile",
  "takes a tiny sip of soup, swallowing hard as he looks into her light blue eyes",
  "strokes P's pale cheek with her warm, soft hand, speaking with quiet wisdom",
  "speaks with gentle resolve, his voice raspy with long-held sorrow",
  "holds both of P's hands in her lap, looking at him with fierce, unwavering belief",
  "takes a deep breath, his expression transforming as her words sink in",
  "nods slowly in agreement, a small, fragile spark of hope appearing in his eyes",
  "smiles with deep love and pride, leaning forward to kiss his cheek gently",
  "leans his head against her shoulder, feeling the warm sunshine on his face",
  "sit together on the sunny rug, the shadow of grief finally beginning to lift"
]
d_82 = [
  "Five days after Nick left, I hit a wall. The apartment was too big, too empty, and too quiet. I went over to Sionna's duplex, but I couldn't move.",
  "I sat on her living room rug in the dark, the blinds pulled tight against the world. I felt like a ghost, suspended in a cold, grey limbo.",
  "Sionna wouldn't let me stay in the dark. She walked over and yanked the blinds open. The bright, beautiful spring daylight poured inside like a flood.",
  "Hey, I protested, squinting against the glare. It's too bright. But she just knelt beside me, holding a warm bowl of steaming vegetable soup.",
  "You need to eat, P, she said gently, her light blue eyes filled with soft concern. You've lost five pounds this week. Just a few spoonfuls. For me.",
  "I took a sip of the warm broth. My stomach was knotted, but the warmth felt good, melting a little piece of the ice inside my chest.",
  "I can't get his voice out of my head, I whispered, staring at my hands. Nick's voice on the porch. He has so much belief in me, Sionna. It's terrifying.",
  "What if I'm not the smart one? What if I fail? What if I'm just a brawler who uses words instead of fists? I don't know who I am without them.",
  "Sionna took my hands, her palms warm and dry. P, she said, her voice full of fierce, beautiful conviction. You're not going to fail.",
  "You are exactly who Nick and Ma believed you are. You have a beautiful mind, and you have a story to tell. Their story. Your story.",
  "You promised Ma you would write it, remember? That is your purpose, P. That is why you survived the chaos. You have to write 'Nick and Me'.",
  "I looked at her, her words cutting through the thick fog of my depression like a hot scalpel. The truth of it hit me in my chest.",
  "She was right. I had a promise to keep. I couldn't sit here in the dark, wasting the life Nick had fought so hard to protect. I had to write.",
  "I felt a sudden, sharp spark of energy run through my limbs. It was the first time in weeks I felt alive. I looked at Sionna, my eyes clear.",
  "Okay, I whispered, my voice steady. Okay, Sionna. I'll write it. I'll write everything. The bar, the fights, the Pontiac, Ma... all of it.",
  "She smiled, a beautiful, radiant expression, and kissed my temple. I know you will, P. And it's going to be beautiful. I'm right here with you.",
  "We sat on the rug, bathed in the warm spring sun, the heavy shadow of my grief finally beginning to lift. I was ready to begin."
]
c_82 = [CHAR_P_FUNERAL, CHAR_S_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_FUNERAL, CHAR_S_HOSPITAL, CHAR_S_HOSPITAL, CHAR_P_FUNERAL, CHAR_S_HOSPITAL, CHAR_P_FUNERAL, CHAR_S_HOSPITAL, CHAR_P_FUNERAL, CHAR_S_HOSPITAL, CHAR_P_FUNERAL, CHAR_P_FUNERAL, CHAR_S_HOSPITAL, CHAR_P_FUNERAL + ", " + CHAR_S_HOSPITAL, CHAR_P_FUNERAL + ", " + CHAR_S_HOSPITAL]
s_82 = ["Character_P", "Character_P", "Character_S", "Character_P", "Character_S", "Character_S", "Character_P", "Character_S", "Character_P", "Character_S", "Character_P", "Character_S", "Character_P", "Character_P", "Character_S", "Character_P", "Character_P"]

scene_82 = {
    "scene_number": 82,
    "setting": "Day 280 - Sionna's Duplex (Interior) - Sionna's Counsel",
    "total_estimated_duration_seconds": 120,
    "scene_filename": "Scene_82_Day280_SionnasHouse_Counsel",
    "transition_type": "cut",
    "lighting_continuity_token": "Transition from dramatic, deep shadow room to a bright, dazzling spring daylight flooding through the duplex window panes",
    "music_bed": {
      "style_description": "Contemplative solo piano with mystical synth pads, soaring cello, and a gentle, comforting acoustic guitar melody",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 14],
          ["Dark Duplex", "Yanking Blinds", "Warm Broth", "Fierce Belief", "The Purpose / Spark", "Spring Sun / Outro"],
          ["instrumental"]*6,
          [
              "Contemplative, slow solo piano chords enter, representing P's depression.",
              "A mystical synthesizer pad swells as Sionna opens the blinds.",
              "A gentle, comforting acoustic guitar melody enters, warm and intimate.",
              "A sweet, soaring cello note joins, expressing fierce belief.",
              "Piano and cello harmonize beautifully, full of hope and transition.",
              "All instruments resolve slowly on a sustained, warm, bright chord."
          ]
      )
    },
    "veo_clips": []
}
scene_82["veo_clips"] = generate_scene_clips(82, 120, c_82, p_82, d_82, s_82, "Sionna's warm duplex living room", 4124)
scenes_to_append.append(scene_82)


# --- SCENE 83: The Notebooks (127s / 18 clips) ---
p_83 = [
  "walks slowly into Ma's empty apartment bedroom, looking around with a reverent expression",
  "stands near the wooden dresser, touching her small silver jewelry box",
  "opens the bottom drawer of Ma's dresser, pulling out a dusty cardboard shoebox",
  "carries the shoebox to the bed, sitting down on the knitted blanket",
  "lifts the cardboard lid, looking inside at stacks of old black-and-white photos",
  "pulls out an old photo of Nick and P as young kids, smiling through tears",
  "looks at the photo of two little boys standing proudly beside a wooden fence",
  "gently strokes the image of little Nick's protective arm around P's shoulders",
  "finds a collection of P's school report cards with gold stars at the box bottom",
  "holds a report card, a tear spilling onto the paper as he realizes Ma's pride",
  "reaches into his duffel bag, pulling out a thick, blank leather-bound notebook",
  "places the blank notebook on the bed sheet, touching its cover with resolve",
  "unscrews the cap of a polished black fountain pen, his fingers steady",
  "stares at the blank white paper of the notebook, his face serious and focused",
  "writes the first words: 'Part One: The Chameleon', his handwriting elegant",
  "writes fast and confident, his face lit with a sudden, beautiful creative fire",
  "smiles through his tears, watching the ink dry on the white page",
  "stands by Ma's window, holding his notebook close to his chest in quiet triumph"
]
d_83 = [
  "The next day, I went back to the apartment. I walked into Ma's room. It still smelled faint of her lavender soap. It felt sacred, like a temple.",
  "I sat on her bed, running my hands over the knitted blanket she had made. I found a dusty shoebox in the bottom of her wooden dresser drawer.",
  "I opened it. Inside were dozens of old photographs, letters, and memories. There was an old photo of Nick and me when we were just little boys in England.",
  "We were standing by a wooden fence. Nick was about six, his reddish hair messy, his chest puffed out, with his arm wrapped tight around my thin shoulder.",
  "Even then, he was my shield. He was looking at the camera with a fierce, challenging glare, as if warning the photographer not to get too close to me.",
  "At the bottom of the box, I found all of my old report cards from grade school and high school, neatly stacked and tied with a faded pink ribbon.",
  "Every single one of them had her elegant, shaky handwriting on the margins. 'P did so well this term. He's going to be a real writer,' she had written.",
  "I squeezed my eyes shut, my heart overflowing with a bittersweet mix of pain and joy. She had kept everything. She had always believed in my future.",
  "I pulled out a thick, blank leather-bound notebook I had bought at the university bookstore months ago, and my favorite black fountain pen.",
  "I sat down at Ma's small writing table by the window. The afternoon sun filtered through the green lace curtains, casting soft patterns on the desk.",
  "I opened the notebook to the first page. The white paper was clean, empty, and full of endless possibility. I held the pen above it, my fingers steady.",
  "I didn't hesitate. I dipped the pen and began to write. I wrote about the freezing winters, the drafty apartments, the smell of Ma's laundry.",
  "I wrote about the bar fights, the polished Pontiac, the chameleons, and the Viking hero who carried the weight of our survival on his shoulders.",
  "The words came out in a fast, blinding torrent, as if a dam had burst inside my mind. My hand could barely keep up with the speed of my thoughts.",
  "I wrote about the energy that bound us together – the strongest force in my world. I wrote until my wrist ached and the ink stained my fingers black.",
  "I was no longer just reliving a nightmare. I was shaping it. I was giving Ma and Nick the immortality they deserved. I was doing my job.",
  "I wrote for hours, until the sun began to sink below the horizon, casting a beautiful violet light across the room. I felt incredibly peaceful.",
  "I had started. The blueprint was being built, one word at a time. I was going to finish this book, no matter what. For Ma. For Nick. For us."
]
c_83 = [CHAR_P_FUNERAL] * 18
s_83 = ["Character_P"] * 18

scene_83 = {
    "scene_number": 83,
    "setting": "Day 285 - Narrator's Apartment (Interior) - The Notebooks",
    "total_estimated_duration_seconds": 127,
    "scene_filename": "Scene_83_Day285_Apt_TheNotebooks",
    "transition_type": "cut",
    "lighting_continuity_token": "Soft, golden afternoon sun filtering through green lace curtains, casting delicate floral shadow patterns across Ma's writing desk",
    "music_bed": {
      "style_description": "Nostalgic acoustic guitar and soft synth pads with sad solo cello, thoughtful electric piano, and beautiful Celtic harp and flute",
      "vocal_style": "none",
      "song_structure": make_song_structure(
          [8, 21, 28, 28, 21, 21],
          ["Sacred Room", "Jewelry Box", "The Photo", "Report Cards", "First Words", "Creative Fire / Outro"],
          ["instrumental"]*6,
          [
              "A gentle, nostalgic fingerpicked acoustic guitar enters, soft and slow.",
              "A wistful solo cello melody joins, full of bittersweet memory.",
              "A beautiful Celtic harp and light flute theme play, representing childhood.",
              "Thoughtful electric piano chords enter as P opens his report cards.",
              "The guitar and flute play a warm, beautiful melody as P begins to write.",
              "All instruments harmonize beautifully, resolving on a sustained, warm chord."
          ]
      )
    },
    "veo_clips": []
}
scene_83["veo_clips"] = generate_scene_clips(83, 127, c_83, p_83, d_83, s_83, "Ma's quiet, sacred apartment bedroom", 4244)
scenes_to_append.append(scene_83)

print("Scene 83 ready!")
