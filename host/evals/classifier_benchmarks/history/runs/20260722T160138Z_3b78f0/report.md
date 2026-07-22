# Benchmark run `20260722T160138Z_3b78f0`

- **UTC:** 2026-07-22 16:01:38Z
- **Project:** `The_Jungle_Book`
- **Models:** grok-4.5
- **Prompts:** v2_grounded, v3_precision
- **Tasks:** ambient_sfx
- **Note:** Testing ambient_sfx v3_precision

| Task | Model | Prompt | Temp | Metric | n | Baseline | AI | Winner | Latency | Gold |
|------|-------|--------|------|--------|---|----------|----|--------|---------|------|
| ambient_sfx | `grok-4.5` | `v2_grounded` | 0 | mean_token_jaccard | 30 | 0.742 | 0.889 | **AI** | 36139ms | curated |
| ambient_sfx | `grok-4.5` | `v3_precision` | 0 | mean_token_jaccard | 30 | 0.742 | 0.878 | **AI** | 31297ms | curated |

## Compare — `ambient_sfx` / `grok-4.5`

| Prompt | Temp | AI score | vs best | Winner vs baseline |
|--------|------|----------|---------|--------------------|
| `v2_grounded` | 0 | 0.889 | best | AI |
| `v3_precision` | 0 | 0.878 | -0.011 | AI |

Per-sample details: `details.json` in this run folder.
