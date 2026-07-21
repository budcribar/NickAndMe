# Classifier benchmarks

Durable **AI vs baseline** evals for Film Studio classifiers. Re-run over time as models and prompts change; history and charts show whether scores improve.

**Location:** `host/evals/classifier_benchmarks` (app eval data — not under `projects/`).

## Layout

| Path | Purpose |
|------|---------|
| `gold/{project}/{task}.json` | Curated (or draft) labels |
| `prompts/{task}/{promptId}.txt` | Prompt text variants |
| `prompts/{task}/{promptId}.meta.json` | Optional label/notes |
| `history/index.json` | Run index (newest first) |
| `history/runs/{runId}/` | `summary.json`, `details.json`, `report.md` |
| `reports/LATEST.md` | Latest history table |
| `reports/history.html` | Charts (Chart.js) — open in a browser |

## Tasks

| Task id | Metric | Gold |
|---------|--------|------|
| `ambient_sfx` | mean token Jaccard (ambient + sfx) | **Curated** Jungle Book blind rounds. Product: **`v2_grounded`** |
| `onscreen_cast` | mean set F1 | **Curated** Jungle Book cast blind (20). Product: **`v2_grounded`** |
| `silent_beat_action` | accuracy | **Curated** multi-book GT (147 beats, 7 titles) under `gold/_all_books/`. Product: **`v2_product`** |
| `extend_cut` | accuracy | **Curated** Jungle Book blind (24). Prompts: `v1_product`, **`v2_grounded`** (draft) |
| `plate_rank` | mean recall@3 capped | **Curated** Buster2 (Buster odds; bunny p13). Baseline = **chance 0.5** (no filename heuristic). Prompt: `v2_picture_book` |
| `species_kind` | accuracy | Curated cast species labels |

## Run

```powershell
# From repo root (requires XAI_API_KEY)
cd host
dotnet run --project tools/ClassifierBenchmarks -c Release -- run `
  --project The_Jungle_Book `
  --tasks ambient_sfx `
  --models grok-4.5 `
  --prompts v1_product,v1_no_speech_sfx `
  --note "after ambient gold curation"

# Model matrix
dotnet run --project tools/ClassifierBenchmarks -c Release -- run `
  --tasks ambient_sfx,species_kind `
  --models grok-4.5 `

# Rebuild reports only
dotnet run --project tools/ClassifierBenchmarks -c Release -- report

# List prompt variants
dotnet run --project tools/ClassifierBenchmarks -c Release -- list-prompts --task ambient_sfx
```

## Compare prompts

1. Copy `prompts/ambient_sfx/v1_product.txt` → `prompts/ambient_sfx/my_variant.txt`
2. Edit text; optional `my_variant.meta.json` with `{ "label": "…", "notes": "…" }`
3. Run with `--prompts v1_product,my_variant` (same model)
4. Open the run’s `report.md` **Prompt compare** section and `reports/history.html`

Each result stores `promptId` + `promptHash` so you can see when the text actually changed.

## Compare models

```text
--models grok-4.5,some-other-model --prompts v1_product
```

History charts plot one series per `task · model · prompt`.

## Gold curation notes (ambient_sfx)

- Built from AmbientBlind rounds 1–2 (30 samples).
- Prefer grounded AI labels for action beds/hits.
- Empty gold for dialogue-only / performance parentheticals.
- Dropped speech-as-SFX (`scolding`, `undertone speech`, `purring` as ambient).

## What is stored per run

- Config: project, tasks, models, prompts, temperature, note  
- Per cell: baseline score, AI score, winner, n, latency, parse hits, prompt hash  
- Per sample (details.json): gold vs baseline vs AI strings and scores  

Baseline always uses the product heuristic (`FountainStage1Importer.InferAmbientAndSfx`, `SpeciesKindClassifier.BaselineKind`, …) so model upgrades are not confounded with heuristic drift.
