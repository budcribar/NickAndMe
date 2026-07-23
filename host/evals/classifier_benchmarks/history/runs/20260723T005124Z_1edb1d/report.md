# Benchmark run `20260723T005124Z_1edb1d`

- **UTC:** 2026-07-23 00:51:24Z
- **Project:** `The_Jungle_Book`
- **Models:** grok-4.5
- **Prompts:** v2_grounded
- **Tasks:** onscreen_cast
- **Note:** add off-screen speaker gold case (Narrator VO over den scene)

| Task | Model | Prompt | Temp | Metric | n | Baseline | AI | Winner | Latency | Gold |
|------|-------|--------|------|--------|---|----------|----|--------|---------|------|
| onscreen_cast | `grok-4.5` | `v2_grounded` | 0 | mean_set_f1 | 21 | 0.821 | 0.967 | **AI** | 26367ms | curated |

Per-sample details: `details.json` in this run folder.
