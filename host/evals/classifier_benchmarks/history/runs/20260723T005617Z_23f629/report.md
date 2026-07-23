# Benchmark run `20260723T005617Z_23f629`

- **UTC:** 2026-07-23 00:56:17Z
- **Project:** `Buster2`
- **Models:** grok-4.5
- **Prompts:** v2_picture_book
- **Tasks:** plate_rank
- **Note:** verify plate_rank after TaskRunners.cs fix

| Task | Model | Prompt | Temp | Metric | n | Baseline | AI | Winner | Latency | Gold |
|------|-------|--------|------|--------|---|----------|----|--------|---------|------|
| plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 2 | 0.500 | 1.000 | **AI** | 8160ms | curated |

Per-sample details: `details.json` in this run folder.
