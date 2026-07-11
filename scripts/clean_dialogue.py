import json

# Load the master blueprint
with open('nickandme.clips.grok.json', 'r') as f:
    data = json.load(f)

# Define exact mapping of scene and clip numbers to their corrected speaker and dialogue
dialogue_cleanups = {
    # (scene_number, clip_number): (new_speaker, cleaned_dialogue)
    (5, 7): ("Character_P", "My mom and dad named her after an Irish goddess, and then something about the River Shannon. I wasn't really listening though, because she had the coolest eyes I ever saw. But it was weird – I felt like I had known her from before. Like we were old friends."),
    (14, 2): ("Character_S", "Did you know that Buddhists consider everything around us to be a dream?"),
    (15, 4): ("Character_S", "You were really great at that. / No, you caught on very quickly."),
    (17, 4): ("Character_N", "Oh, great. A girl. That's worth all this. Don't you think Ma deserves better than that? Try looking at your priorities Bro."),
    (21, 4): ("Character_M", "It just feels like my heart is going to jump out of my chest! It's so strange, the feeling I have right now. I'm a little dizzy too."),
    (22, 3): ("Character_P", "Nick shows up and asks, What the hell is this all about now? And we explained the situation. Ma didn't seem too concerned. She did keep holding onto her chest and saying that she heard ringing in her ears."),
    (22, 4): ("Character_M", "Someone must be talking about me."),
    (23, 4): ("Character_N", "That you are little brother!"),
    (24, 2): ("Character_M", "You're such a good boy. You and Nick are like a God-send to me. What would I do without you two?"),
    (24, 3): ("Character_N", "What's up, little bro? You got Ma all tucked in? Man, you should've been a nurse!"),
    (29, 2): ("Character_N", "So, what's up with you?"),
    (30, 4): ("Character_N", "You're not just my brother, man. You're my best friend."),
    (33, 3): ("Character_S", "The world is full of violent, seemingly useless people. But you aren't violent or useless. You know more. We have to live with people that aren't as advanced spiritually."),
    (34, 3): ("Character_S", "I could never love anyone like I love you."),
    (37, 5): ("Character_N", "Kiss my ass! Did I ask your opinion?"),
    (38, 1): ("Character_N", "What's up Lindsey?"),
    (39, 2): ("Character_S", "Your brother tried to hit you. / No one does that, you know. I mean, no one who really cares about you."),
    (40, 2): ("Character_S", "Me too honey. Me too. / The purpose of your life is to find the true self within you, and to honor it. To be the best person you can while you're here. That's how I see it."),
    (42, 2): ("Character_S", "That sounds about right! / Let's go to bed. We've had an interesting day."),
    (45, 3): ("Character_S", "Geez, what a day! I've been running around like a chicken with its head cut off. Yoga, store, class notes on comparative religion, library, and then here."),
    (45, 6): ("Character_S", "So what's your paper about? / Oh, thoughts on dualism. / Perception is so cool to think about. / Like, is this bed here when you don't see it?"),
    (46, 1): ("Character_S", "You know my parents were hippies, right? / Well, I was born in 1970, and my mom always said she got pregnant with me at Woodstock."),
    (47, 2): ("Kevin McCleary", "Man, I heard what happened at Joe's! What the hell? / I heard you stopped Nick from beating some guy's ass! Man, that's tough shit! They said you hauled off and clocked Nick!"),
    (47, 4): ("Kevin McCleary", "Sure, sure man, I got you covered. / But I'm just saying, it's all over town. You're the hottest topic flying around! People are saying Nick's little brother is a badass!"),
    (49, 2): ("Character_S", "Well, I got a little bit of weed for us to smoke. / The guy I know on campus sold me some because I've known him for four years."),
    (49, 4): ("Character_S", "Oh my gosh, I've had this for ten or twelve years! / I really don't sit around and smoke pot – I've been a little busy. / Well, that's what we're doing today!"),
    (49, 5): ("Character_S", "I am glad I get to do this, because I love just being with you, and talking about fun stuff for once! / I'll go first. / Wow! That is strong!"),
    (51, 5): ("Bob", "Your boyfriend single-handedly conquered the BEAST!"),
    (51, 6): ("Bob", "Man, you were in Nick's face! You moved like Bruce Lee, dog! / Everybody is talking. Sayin' he's a pussy, excuse my language. Sayin' he's not the same."),
    (52, 1): ("Bob", "Nuff said! / Man, you might get pissed and kick my ass! Tell Nick I want to hang sometime."),
    (55, 2): ("Character_N", "What do YOU want? / What for?"),
    (55, 4): ("Character_N", "What do YOU think, moron? / You've ruined my life! I can't go anywhere. People are talking. Saying I'm a pussy, my little brother kicked my ass!"),
    (55, 5): ("Character_N", "All because of your big mouth! Trying to be a hero. You piece of shit! You started some rumor to make yourself look good. My reputation is dog shit. Thanks a lot Bro, thanks a lot."),
    (56, 5): ("Character_N", "She'll probably have to go to an old folks' home one of these days. / Make sure you give her her meds, or she'll end up in the hospital."),
    (57, 1): ("Character_M", "On Halloween, my brothers would watch from the bushes and tip those outhouses over! We were plain hoodlums on the farm!"),
    (57, 2): ("Character_S", "Sounds like you were a bunch of troublemakers."),
    (58, 2): ("Character_M", "Oh my God! It's you Nick! I thought we'd never see you! Where have you been hiding?"),
    (58, 3): ("Character_S", "I'm Sionna, nice to meet you."),
    (58, 4): ("Character_N", "Uh, yea, likewise. / No thanks. I can't eat that crap. Sugar makes me feel bloated and sick."),
    (58, 6): ("Character_N", "So, I hear you're a university lady? / What are you gonna do when you get done?"),
    (58, 7): ("Character_N", "Well professor, I hope the best for ya. School doesn't pay off. I hope you can break through that glass ceiling. Because, you know, it's even harder for a woman to teach at that level, or so I've heard."),
    (59, 2): ("Character_S", "I don't see how education cannot be changed for the better by female professors. / You can't keep a good woman down."),
    (59, 3): ("Character_N", "If you ask me, that's why the country is heading down the tubes! Women can't stay home with kids. No offense Ma."),
    (59, 5): ("Character_N", "Well, someone has a job tomorrow. / Sounds like you have it figured out, with Doctor Sionna's help."),
    (59, 6): ("Character_N", "Take care of this numbskull."),
    (59, 7): ("Character_N", "Take care of Ma."),
    (61, 2): ("Character_P", "She was researching creation myths in different religions. We started discussing it, and stuff started to make sense. Like she said, the more you look into some things, the less you believe any of it can be true."),
    (62, 2): ("Character_M", "What should I wear?"),
    (64, 2): ("Character_S", "They wouldn't even respond to Robert and Margery. / Yes, raised right here in the good old Midwest, christened with proper names and everything!"),
    (64, 3): ("Character_S", "Actually, my father's parents mentally divorced him. I remember seeing them once at the farm. A bad scene, yelling, grandmother crying. Her hair piled up in a beehive, cheap blue polyester dress."),
    (65, 3): ("Character_P", "They adored Sionna, their only child. As we walked up the wooden steps, two old hippies stepped out. Mushroom had a long grey beard and a tie-dye shirt, and Ginger wore a beautiful long floral dress. They waved with pure joy."),
    (67, 14): ("Character_P", "Sionna was already at the door, her yellow sun hat left behind on the table, holding her keys and looking at me with fierce, unwavering support."),
    (68, 3): ("Character_P", "Sionna kept her eyes glued to the white lines on the asphalt. She didn't say anything, but I could see the muscles in her jaw working, tight with focus."),
    (68, 6): ("Character_S", "Hey, she said softly, reaching out to squeeze my hand. She's strong, P. She's survived so much already. Don't lose hope yet. Just breathe."),
    (68, 9): ("Character_P", "A huge semi-truck roared past us in the opposite direction, throwing a massive wave of dirty water over our hood. The Civic shook, but Sionna held it steady."),
    (68, 12): ("Character_P", "Sionna checked her mirrors and accelerated slightly as the rain began to taper off into a light, gray drizzle. The sky in the east was starting to turn a pale gray."),
    (68, 15): ("Character_P", "Sionna turned onto the main avenue leading to the hospital. The big brick buildings rose up ahead of us like cold monuments under the gray morning sky."),
    (69, 8): ("Character_P", "I put my hand on his shoulder. It was the first time I had initiated physical contact with him in years. You did the right thing, Nick. You called the ambulance."),
    (69, 10): ("Character_P", "I looked at his hands – the hands that had won dozens of bar fights, the hands that had protected me. Right now, they were just shaking, empty and weak."),
    (69, 12): ("Character_N", "Thanks, Sionna."),
    (69, 14): ("Character_N", "I always thought I could protect us from anything, P. / But I can't fight a stroke. I can't punch a disease."),
    (70, 6): ("Character_N", "Hey Ma."),
    (70, 9): ("Character_P", "Nick bent over the bed, his forehead resting against the metal side rail. I could see his broad shoulders shaking under his leather jacket. He was crying silently."),
    (70, 13): ("Character_P", "Nick looked up, his face wet with tears, staring at P with raw vulnerability."),
    (70, 14): ("Character_N", "P, she's not there. / That's not Ma. Ma is gone. I don't know where she is."),
    (70, 16): ("Character_N", "Ma, I'm here. / I took care of the house, Ma. I took you to mass, remember? St. Patrick's. You looked so pretty. I'm right here."),
    (71, 3): ("Character_N", "P, remember that winter we lived in that drafty apartment on Main Street? The one where the furnace broke?"),
    (71, 5): ("Character_N", "Yeah. / I stole a bunch of wood from that construction site down the road and made a campfire in the yard. We roasted hotdogs."),
    (71, 7): ("Character_N", "I didn't know how to do anything else, P. / I was just a kid, but I knew I had to feed you. I used to sneak cheese from the store."),
    (71, 8): ("Character_N", "Of course I did. You were my little brother. That was my job."),
    (71, 11): ("Character_N", "Don't be stupid, P. / You were the smart one. You were supposed to study, not worry about food."),
    (71, 14): ("Character_N", "Yeah. We are. / She loved you so much, P. She used to show your report cards to anyone who would look."),
    (71, 17): ("Character_N", "You'll have your chance, P. / She knows. Mothers always know. She's resting now. Let's just sit with her."),
    (72, 4): ("Character_N", "But she's breathing!"),
    (72, 7): ("Character_N", "What are you saying? / You're just going to give up on her?"),
    (72, 12): ("Character_N", "She's our mother! / We don't pull the plug on our mother! We fight for her!"),
    (72, 15): ("Character_P", "Nick stood there, his fists clenched, his breathing ragged. He looked like a cornered animal, ready to lash out at anyone who got close."),
    (73, 3): ("Character_N", "P, they want us to kill her. That's what it is. Removing the machine is killing her. I can't do that."),
    (73, 6): ("Character_N", "What if they're wrong? / What if she wakes up? What if she's trapped inside her mind, screaming for us to help?"),
    (73, 14): ("Character_P", "Nick didn't push me away. He grabbed onto my shirt, burying his face in my shoulder, and sobbed. He wept with a raw, gasping grief that shook his entire body."),
    (73, 17): ("Character_N", "Okay, P. / Okay."),
    (74, 13): ("Character_P", "The doctor checked her eyes and her chest, then looked up at us. Time of death, 10:42 AM, he said softly. I am so sorry for your loss, boys."),
    (75, 8): ("Character_S", "Sit down, P. / You need to rest. You haven't slept in thirty-six hours. Sit with me."),
    (77, 5): ("Character_N", "You think you're better than me, don't you? With your college classes and your pretty girlfriend!"),
    (77, 9): ("Character_N", "Why didn't you save her, P? / You're the smart one! Why didn't you do something? Why did you let her die?"),
    (77, 14): ("Character_S", "Nick, stop! / Let him go! You're hurting him! Nick, please!"),
    (77, 15): ("Character_N", "Leave us alone, Sionna! / This is between me and my brother!"),
    (79, 7): ("Character_N", "I'm sorry about the table, P. / I'll buy a new one before I go."),
    (79, 9): ("Character_P", "I smiled, taking a sip of my coffee. You're not so bad yourself, Nick. Even if you are a mean son of a bitch. He laughed, a quiet, genuine sound."),
    (79, 14): ("Character_N", "Up north. Maybe work on an oil rig, or a barge on the Mississippi. Somewhere big. Somewhere quiet."),
    (80, 5): ("Character_N", "There's nothing left for me here. / The bar is slow, the guys are getting old, and... Ma is gone. The house is empty."),
    (80, 9): ("Character_N", "P, you're the smart one. You know that, right?"),
    (80, 12): ("Character_N", "Yes, you are. / You have a beautiful mind, P. Ma knew it. I knew it. That's why I always fought for you."),
    (82, 3): ("Character_P", "Sionna wouldn't let me stay in the dark. She walked over and yanked the blinds open. The bright, beautiful spring daylight poured inside like a flood."),
    (82, 5): ("Character_S", "You need to eat, P. / You've lost five pounds this week. Just a few spoonfuls. For me."),
    (82, 6): ("Character_P", "I took a sip of the warm broth. My stomach was knotted, but the warmth felt good, melting a little piece of the ice inside my chest."),
    (82, 12): ("Character_P", "I looked at her, her words cutting through the thick fog of my depression like a hot scalpel. The truth of it hit me in my chest."),
    (82, 15): ("Character_P", "Okay, I whispered, my voice steady. Okay, Sionna. I'll write it. I'll write everything. The bar, the fights, the Pontiac, Ma... all of it."),
    (85, 6): ("Character_P", "Sionna didn't say a single word. She just sat beside me, her light blue eyes wide, staring at my face, listening to every syllable."),
    (85, 8): ("Character_P", "I read about the Pontiac's roar, the smell of wet pine, and the silent, unbreakable bond of our blood. The words felt alive, breathing in the room."),
    (86, 5): ("Character_S", "We're almost done. / Just one more trip in the Civic."),
    (86, 13): ("Character_N", "Is that true, P? Are you a real writer now?"),
    (86, 14): ("Character_N", "I knew it, P."),
    (86, 16): ("Character_P", "Thanks, Nick, I whispered, tears of joy streaming down my cheeks. I couldn't have written a word without you. You're the hero of the story, Nick."),
    (87, 5): ("Character_P", "I looked at her instead. Her face was painted in the golden sunset light, her short brown hair catching the warm breeze. She looked like an angel."),
    (87, 8): ("Character_P", "God is just energy, she had said. It's in all of us, it existed before time, and it goes on forever. Standing here, I finally understood it."),
    (87, 12): ("Character_P", "I looked back at the lake. The golden path of light on the water seemed to stretch all the way to the horizon, an endless bridge into the future."),
    (88, 7): ("Character_P", "Sionna walked into the studio. She was wearing a white knit sweater, her short brown hair styled beautifully, her light blue eyes full of pride."),
    (88, 9): ("Character_S", "You did it, P. / You finished the story."),
    (88, 11): ("Character_S", "No, P. You saved your own life. With your words. I'm so proud of you."),
    (88, 13): ("Character_P", "I stood up and wrapped my arms around her waist, pulling her close. We stood in the center of the studio, bathed in the soft afternoon daylight."),
    (89, 10): ("Character_P", "But I know he's happy. I can hear it in his laugh, loud, rough, and free under the snowy mountains. He's doing his job. He's being Nick."),
    (89, 13): ("Character_P", "I look at my finished book, and I don't feel grief anymore. I feel a deep, burning gratitude. I am Ma's boy. I am Nick's little brother."),
}

# Apply cleanups
modified_count = 0
for s in data['scenes']:
    s_num = s['scene_number']
    for c in s.get('veo_clips', []):
        c_num = c['clip_number']
        key = (s_num, c_num)
        if key in dialogue_cleanups:
            new_speaker, cleaned_dial = dialogue_cleanups[key]
            c['audio_payload']['speaker'] = new_speaker
            c['audio_payload']['dialogue'] = cleaned_dial
            modified_count += 1

print(f"Success: Updated {modified_count} clips in memory representation.")

# Write updated JSON back to file
with open('nickandme.clips.grok.json', 'w') as f:
    json.dump(data, f, indent=2)

print("Success: Wrote updated blueprint to nickandme.clips.grok.json.")
