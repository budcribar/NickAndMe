import json

# Read existing JSON file
with open('nickandme.clips.grok.json', 'r', encoding='utf-8') as f:
    data = json.load(f)

print("Starting programmatic timestamp recalculation for all scenes...")

# Sort scenes by scene_number to be absolutely sure of order
data['scenes'].sort(key=lambda x: x['scene_number'])

current_sec = 0

for s in data['scenes']:
    scene_num = s['scene_number']
    print(f"Processing Scene {scene_num}...")

    scene_duration = s['total_estimated_duration_seconds']
    calculated_scene_dur = 0

    for i, clip in enumerate(s['veo_clips']):
        clip_dur = 8 if i == 0 else 7
        calculated_scene_dur += clip_dur

        ts_start_min = current_sec // 60
        ts_start_sec = current_sec % 60
        ts_end_min = (current_sec + clip_dur) // 60
        ts_end_sec = (current_sec + clip_dur) % 60

        clip['timestamp'] = f"{ts_start_min:02d}:{ts_start_sec:02d}-{ts_end_min:02d}:{ts_end_sec:02d}"

        current_sec += clip_dur

    # Check if calculated scene duration matches estimated
    if calculated_scene_dur != scene_duration:
        print(f"Warning: Scene {scene_num} estimated duration {scene_duration} does not match calculated clip duration sum {calculated_scene_dur}!")
        # Force update estimated duration to match clip sum to preserve mathematical consistency
        s['total_estimated_duration_seconds'] = calculated_scene_dur

data['cumulative_duration_seconds'] = current_sec

with open('nickandme.clips.grok.json', 'w', encoding='utf-8') as f:
    json.dump(data, f, indent=2)

print("All timestamps programmatically recalculated!")
print(f"New total cumulative duration: {data['cumulative_duration_seconds']} seconds ({data['cumulative_duration_seconds'] // 60}m {data['cumulative_duration_seconds'] % 60}s)")
