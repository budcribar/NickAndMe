# Benchmark run `20260722T160120Z_122ac4`

- **UTC:** 2026-07-22 16:01:20Z
- **Project:** `The_Jungle_Book`
- **Models:** grok-4.5
- **Prompts:** v1_product, v2_focused
- **Tasks:** species_kind
- **Note:** Testing species_kind v2_focused

| Task | Model | Prompt | Temp | Metric | n | Baseline | AI | Winner | Latency | Gold |
|------|-------|--------|------|--------|---|----------|----|--------|---------|------|
| species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 51 | 0.490 | 0.843 | **AI** | 26085ms | curated |
| species_kind | `grok-4.5` | `v2_focused` | 0 | accuracy | 51 | 0.490 | 0.804 | **AI** | 22424ms | curated |

## Compare — `species_kind` / `grok-4.5`

| Prompt | Temp | AI score | vs best | Winner vs baseline |
|--------|------|----------|---------|--------------------|
| `v1_product` | 0 | 0.843 | best | AI |
| `v2_focused` | 0 | 0.804 | -0.039 | AI |

Per-sample details: `details.json` in this run folder.
