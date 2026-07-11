import json

with open('nickandme.clips.grok.json', 'r', encoding='utf-8') as f:
    data = json.load(f)

scene_numbers = [s['scene_number'] for s in data['scenes']]
print("Total scenes:", len(data['scenes']))
print("Scene numbers in JSON:", scene_numbers)
print("Cumulative duration on disk:", data['cumulative_duration_seconds'])
