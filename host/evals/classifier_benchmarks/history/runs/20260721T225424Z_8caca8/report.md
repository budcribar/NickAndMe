# Benchmark run `20260721T225424Z_8caca8`

- **UTC:** 2026-07-21 22:54:24Z
- **Project:** `Buster2`
- **Models:** grok-4.5
- **Prompts:** v1_product, v2_picture_book
- **Tasks:** plate_rank
- **Note:** gold verified; v2 picture-book prompt + better baseline

| Task | Model | Prompt | Temp | Metric | n | Baseline | AI | Winner | Latency | Gold |
|------|-------|--------|------|--------|---|----------|----|--------|---------|------|
| plate_rank | `grok-4.5` | `v1_product` | 0 | mean_recall_at_3_capped | 2 | 1.000 | 0.500 | **baseline** | 15220ms | curated |
| plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 2 | 1.000 | 1.000 | **tie** | 6758ms | curated |

## Compare — `plate_rank` / `grok-4.5`

| Prompt | Temp | AI score | vs best | Winner vs baseline |
|--------|------|----------|---------|--------------------|
| `v2_picture_book` | 0 | 1.000 | best | tie |
| `v1_product` | 0 | 0.500 | -0.500 | baseline |

Per-sample details: `details.json` in this run folder.
