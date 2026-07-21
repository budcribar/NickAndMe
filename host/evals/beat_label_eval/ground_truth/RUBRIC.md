# Beat action_class — ground-truth rubric

Labels are for **clip duration budgeting** in Film Studio, not literary analysis.

| class | planned duration | Use when |
|-------|------------------|----------|
| **establishing** | ~4–5s | True **location / setup open**: the shot’s job is to show a place or new setup. Not every first beat of a scene. |
| **hold** | ~3s | Micro performance / stillness: look, smile, freeze, short reaction, pause. Little locomotion. |
| **action** | ~3–5s | Ordinary physical business: walk, open door, prop work, multi-step business without spectacle. |
| **big_action** | ~6–12s | High-energy continuous motion is the point: chase, fight, crash, leap, vault, climb under danger. |

## Hard rules

1. **First silent ≠ establishing.** Only if the visual is truly about revealing a place/setup.
2. Mid-scene business in a known room (writing, sitting at window again, dressing) → **action** or **hold**, not establishing.
3. Micro reaction before dialogue → usually **hold**.
4. Reading, listening, faces, letters → **hold** or **action**, almost never **big_action**.
5. When unsure between hold and action: prefer **hold** if the beat is mostly face/body stillness; **action** if multi-step business or locomotion dominates.

## How to annotate

```bash
dotnet run --project host/tools/BeatLabelEval -- --export-annotate --all
```

Edit `projects/_beat_label_eval/annotate/{ProjectId}.json` → set each `gold.class`.  
Save canonical gold as:

`projects/_beat_label_eval/ground_truth/{ProjectId}.json`

```json
{
  "projectId": "GiftOfTheMagi",
  "annotator": "human",
  "labels": [
    { "id": "s1_b1", "class": "establishing", "note": "room open" }
  ]
}
```

## Scoring

```bash
dotnet run --project host/tools/BeatLabelEval -- --score-gt --all --ai-from v3
```

Reports **three** systems vs ground truth:

| System | Fair? | Notes |
|--------|-------|--------|
| **baseline heuristic** | Yes (product baseline) | Frozen copy of **checked-in** `InferActionClass` (first silent → establishing). Not the working-tree edits. |
| **tuned heuristic** | No on this book set | Working-tree rules after looking at these books — **eval-contaminated**. Diagnostic only. |
| **AI** | Mostly | Not trained on gold labels; prompt few-shots may overlap. Prefer held-out titles for ship decisions. |

Primary ship decision: **baseline H vs AI vs gold** on books **not** used to tune rules/prompts.

Do **not** report heuristic↔AI agreement as quality.
