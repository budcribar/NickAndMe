# Silent beat `action_class` eval (keep for model comparison)

Offline harness + gold labels for duration budgeting classes:

`establishing` | `hold` | `action` | `big_action`

## Product path (shipped)

- **Service:** `SilentBeatActionClassifier` (prompt **v2**, temp **0**, model from config)
- **When:** Stage 2 shot plan (`Stage2PlannerService`)
- **Policy:** AI preferred → retry on flake → **baseline** `InferActionClass` fallback only if no valid label
- **Config:**
  - `FilmStudio__ClassifySilentBeatsWithChat` (default true)
  - `FilmStudio__SilentBeatClassifyModel` (default `grok-4.5`)
  - `FilmStudio__SilentBeatClassifyTemperature` (default `0`)
  - `FilmStudio__SilentBeatClassifyMaxAttempts` (default `3`)

Blueprint records meta under `stage2_meta.silent_beat_classify` (prompt version, model, counts).

## Fair scoring (do not use heuristic↔AI agreement)

```bash
cd host/tools/BeatLabelEval
dotnet run -c Release -- --score-gt --all --ai-from v3
```

Compares against `ground_truth/`:

| System | Meaning |
|--------|---------|
| baseline heuristic | Checked-in first-silent=establishing (product fallback) |
| tuned heuristic | Contaminated edits — diagnostic only |
| AI (folder) | Cached labels from `--label-ai --prompt v1\|v2\|v3` |

## Compare a new model

1. Point config / env at the model, or run eval tool with that model (extend `--model` if needed).
2. Label AI cache:
   ```bash
   dotnet run -c Release -- --label-ai --all --fresh --prompt v2
   ```
   (Store under a new folder name if you add `--out` later; today writes `projects/_beat_label_eval/{prompt}/`.)
3. Score:
   ```bash
   dotnet run -c Release -- --score-gt --all --ai-from v2
   ```
4. Diff `gt_score/summary.json` vs prior model.

## Gold

- Rubric: `ground_truth/RUBRIC.md`
- Labels: `ground_truth/{ProjectId}.json`
- Export packs: `--export-annotate --all` → `annotate/`

## Historical results (do not delete)

| Path | Notes |
|------|--------|
| `summary.json` / `*_beat_labels.json` | v1 AI vs heuristic agreement (obsolete metric) |
| `v2/`, `v3/` | Prompt variants; **vs gold ~86% tied** |
| `v4h/` | Tuned heuristic rescore (contaminated) |
| `gt_score/` | **Canonical:** baseline H / tuned H / AI vs gold |

### Snapshot (2026-07-21, 147 gold beats)

| System | Class accuracy | Duration OK |
|--------|----------------|-------------|
| Baseline H | ~46.9% | ~68% |
| AI v1/v2/v3 | ~85.7–86.4% | ~88–90% |

No clear AI prompt winner among v1/v2/v3; ship **v2**.
