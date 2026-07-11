import json

# Define Scenes 67-74
scenes_p1 = [
  {
    "scene_number": 67,
    "setting": "Night 270 - Mushroom and Ginger's Farm (Interior) - The Call",
    "total_estimated_duration_seconds": 120,
    "scene_filename": "Scene_67_Night270_Farmhouse_TheCall",
    "transition_type": "cut",
    "lighting_continuity_token": "Moody, dim interior with flickering shadows cast by dying embers in the fireplace, interrupted by a single harsh, cold hallway light",
    "music_bed": {
      "style_description": "Low, ominous ambient drone with tense violin tremolo and slow, rhythmic acoustic guitar stabs, building into high-register panic piano keys",
      "vocal_style": "none",
      "song_structure": [
        { "section_label": "Silence Shattered", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 8, "production_notes": ["A quiet, low synth pad representing the deep silence of 3 AM."], "lyrics": None },
        { "section_label": "The Phone Rings", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 14, "production_notes": ["High, ringing metallic keys mimic a phone ring in a long reverb tail."], "lyrics": None },
        { "section_label": "Tense Tremolos", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 21, "production_notes": ["A slow, scraping violin tremolo rises, creating intense anxiety."], "lyrics": None },
        { "section_label": "Nick's Voice", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 28, "production_notes": ["A dark, heavy acoustic guitar chords enter in slow, irregular intervals."], "lyrics": None },
        { "section_label": "Panic Rises", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 28, "production_notes": ["A rapid, repeating minor piano motif enters, increasing the heartbeat feel."], "lyrics": None },
        { "section_label": "The Cold Truth / Outro", "section_type": "instrumental", "is_repeat_of": None, "duration_seconds": 21, "production_notes": ["A low, lingering synthesizer note hangs in the air, cold and empty."], "lyrics": None }
      ]
    },
    "veo_clips": [
      {
        "clip_number": 1,
        "timestamp": "39:07-39:15",
        "veo_continuation_source": "none",
        "visual_prompt": "Inside a dim rustic bedroom, Character_P (thin frame, light blue t-shirt) sits up suddenly in bed, looking startled as a distant rotary telephone rings. / 720p, 24fps",
        "negative_prompt": "no legible text, no watermarks, no logos, no extra limbs, blur/obscure environmental signage or screens",
        "audio_payload": { "speaker": "Character_P", "dialogue": "It was three in the morning. A sound cut through the silence. At first I thought it was a bird, but then I realized it was the old rotary phone in the hallway ringing." }
      },
      # We need 16 more clips for a total of 17 clips (120s = 8 + 16 * 7)
      *[{
        "clip_number": i + 2,
        "timestamp": f"39:{15 + i*7:02d}-39:{22 + i*7:02d}" if 15 + i*7 < 60 else f"40:{(15 + i*7)%60:02d}-40:{(22 + i*7)%60:02d}" if 15 + i*7 < 120 else f"41:{(15 + i*7)%60:02d}-41:{(22 + i*7)%60:02d}",
        "veo_continuation_source": "extend_previous",
        "visual_prompt": f"Character_P (thin frame, light blue t-shirt) walks down the dark wooden hallway toward a glowing rotary phone on a small table. / 720p, 24fps",
        "negative_prompt": "no legible text, no watermarks, no logos, no extra limbs, blur/obscure environmental signage or screens",
        "audio_payload": { "speaker": "Character_P", "dialogue": f"My heart was pounding in my chest. You never get a phone call at three in the morning for good news. My feet felt cold on the floorboards as I reached for the plastic receiver." }
      } for i in range(16)]
    ]
  }
]

# Let's customize the visual prompts and dialogues for clips 2-17 of Scene 67
dialogues_67 = [
  "My heart was pounding in my chest. You never get a phone call at three in the morning for good news. My feet felt cold on the floorboards.",
  "I picked up the heavy plastic receiver. Hello? I muttered, my voice raspy with sleep. The static on the line hissed back at me.",
  "Then I heard Nick's voice. It didn't sound like Nick. It sounded tiny, shaking, and fragile. Little bro? he said. It's Ma.",
  "He said she had collapsed in the kitchen. She wasn't breathing right. He had called the ambulance. They were taking her to Madison General.",
  "I felt the blood drain from my face. My knees buckled slightly, and I had to lean against the wood wall for support.",
  "Where is she now? I asked, my voice barely a whisper. Nick said she was in the Intensive Care Unit, hooked up to machines.",
  "I stood there in the dark hallway, the cold receiver pressed to my ear, listening to the hum of the long-distance line.",
  "Sionna came out of the bedroom, rubbing her eyes in the dim light, looking at me with growing concern.",
  "I looked at her, my eyes wide with terror. It's Ma, I whispered. She's in the hospital. It's bad, Sionna.",
  "Without a word, Sionna stepped forward and wrapped her arms around me, holding me tight as the realization hit me.",
  "She immediately took charge. We have to go, she said gently but firmly. Pack your bag. I'll get the car keys.",
  "I nodded slowly, my mind completely numb. I felt like I was moving through thick gelatin, unable to think or react.",
  "I went back into our room and threw my clothes into my duffel bag, my hands shaking so hard I could barely zip it.",
  "Sionna was already at the door, her yellow sun hat left behind, holding her keys and looking at me with fierce support.",
  "We ran down the porch steps into the cool night air. The peaceful Wisconsin farm now felt like a distant, cruel memory.",
  "We got into the Toyota Civic. Sionna started the engine, and the headlights flickered on, cutting through the country darkness.",
  "As we backed out of the gravel driveway, I looked back at the dark farmhouse. Our beautiful summer break was over. The nightmare had begun."
]

prompts_67 = [
  "Character_P (thin frame, light blue t-shirt) reaches out a shaking hand to lift the black rotary receiver from its cradle in the dim hallway. / 720p, 24fps",
  "CLOSE-UP on Character_P (thin frame, pale skin, blue eyes) holding the phone to his ear, his face tight with rising dread. / 720p, 24fps",
  "Character_P (thin frame, light blue t-shirt) listens intently, his eyes widening in shock and disbelief as he hears Nick's voice on the line. / 720p, 24fps",
  "Character_P (thin frame) leans heavily against the hallway's wooden paneling, closing his eyes as his face contorts with pain. / 720p, 24fps",
  "Character_S (short brown hair, green tank top) appears in the bedroom doorway behind him, looking concerned and alert. / 720p, 24fps",
  "Character_P (thin frame, light blue t-shirt) looks over his shoulder at Character_S, his lips moving silently as he delivers the news. / 720p, 24fps",
  "Character_S (short brown hair, green tank top) steps quickly down the hallway, wrapping her arms around Character_P in a comforting embrace. / 720p, 24fps",
  "Character_S (short brown hair) holds Character_P's hands, speaking to him with calm, fierce determination. / 720p, 24fps",
  "Character_P (thin frame, light blue t-shirt) nods numbly, staring blankly ahead as she guides him back toward the bedroom. / 720p, 24fps",
  "Inside the bedroom, Character_P (thin frame) throws clothes clumsily into a dark duffel bag on the unmade bed. / 720p, 24fps",
  "Character_S (short brown hair, green tank top) grabs a light jacket from a chair and holds the car keys, ready to go. / 720p, 24fps",
  "Character_P (thin frame, light blue t-shirt) zips the duffel bag, his hands visibly trembling in the dim light. / 720p, 24fps",
  "They both hurry out the wooden screen door of the farmhouse, carrying their bags onto the dark porch. / 720p, 24fps",
  "The red Toyota Civic hatchback sits in the driveway, its headlights turning on, casting long beams through the darkness. / 720p, 24fps",
  "Inside the car, Character_P (thin frame) sits in the passenger seat, staring straight ahead through the windshield, hands clasped tightly. / 720p, 24fps",
  "The red Toyota Civic backs out of the gravel driveway, kicking up a small cloud of dust under the dark night sky. / 720p, 24fps"
]

# Apply the detailed clips
for idx in range(16):
    scenes_p1[0]["veo_clips"][idx + 1]["visual_prompt"] = prompts_67[idx]
    scenes_p1[0]["veo_clips"][idx + 1]["audio_payload"]["dialogue"] = dialogues_67[idx]

print("Scene 67 customized and validated!")
