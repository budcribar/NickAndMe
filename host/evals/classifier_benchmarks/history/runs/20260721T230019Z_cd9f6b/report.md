# Benchmark run `20260721T230019Z_cd9f6b`

- **UTC:** 2026-07-21 23:00:19Z
- **Project:** `Buster2`
- **Models:** grok-4.5
- **Prompts:** v2_picture_book
- **Tasks:** plate_rank
- **Note:** baseline=chance 0.5; heuristic rank removed

| Task | Model | Prompt | Temp | Metric | n | Baseline | AI | Winner | Latency | Gold |
|------|-------|--------|------|--------|---|----------|----|--------|---------|------|
| plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 2 | 0.500 | 1.000 | **AI** | 7758ms | curated |

Per-sample details: `details.json` in this run folder.
