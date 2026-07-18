# Learning loop (Phase A)

Goal: every review note can improve the **next** plan/render, without auto-poisoning prompts.

## Layers

| Layer | Meaning | Dirty? | Apply to |
|-------|---------|--------|----------|
| `clip` | One take | No | Blueprint prompt on regen |
| `stage2` | Shot design / policy | Yes → stage2 | `prompts/stage2_shot_planner.txt` |
| `stage1` | Screenplay / story wrong | Yes → fountain + stage2 | `prompts/book_to_fountain.txt` + `source/screenplay.fountain` |
| `verifier` | Detection rubric | No | `prompts/verifier_clip.txt` |
| `engine` | Tooling / renderer | No | `review_feedback/SCRIPT_NOTES.md` |

Shared durable facts: `prompts/shared_rules.txt`.

## Dirty cascade

`pipeline_state.json`:

```json
"scene_dirty": {
  "2": {
    "stage1": false,
    "stage2": true,
    "reason": "…",
    "updated_at": "…",
    "entry_ids": ["abc12"]
  }
}
```

Operator checklist (UI):

1. Edit / re-run Stage prompts or JSON for that scene  
2. Regenerate clips  
3. Clear dirty flag  

Auto Stage 1/2 LLM + PR promote = later phases.

## GUI

- **Scenes** — layer selectbox; cascade buttons; dirty banner  
- **Edit Log** — layer filter; → Layer prompts / Shared rules  
- **Home** — dirty scene count  
