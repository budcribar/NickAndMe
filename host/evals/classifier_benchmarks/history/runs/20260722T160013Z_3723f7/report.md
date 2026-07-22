# Benchmark run `20260722T160013Z_3723f7`

- **UTC:** 2026-07-22 16:00:13Z
- **Project:** `The_Jungle_Book`
- **Models:** grok-4.5
- **Prompts:** 
- **Tasks:** ambient_sfx, species_kind, onscreen_cast, silent_beat_action, extend_cut, plate_rank
- **Note:** Grok-only benchmark run

| Task | Model | Prompt | Temp | Metric | n | Baseline | AI | Winner | Latency | Gold |
|------|-------|--------|------|--------|---|----------|----|--------|---------|------|
| ambient_sfx | `grok-4.5` | `v2_grounded` | 0 | mean_token_jaccard | 30 | 0.742 | 0.872 | **AI** | 37526ms | curated |
| species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 51 | 0.490 | 0.863 | **AI** | 23132ms | curated |
| onscreen_cast | `grok-4.5` | `v2_grounded` | 0 | mean_set_f1 | 20 | 0.812 | 0.975 | **AI** | 41011ms | curated |
| silent_beat_action | `grok-4.5` | `v2_product` | 0 | accuracy | 147 | 0.469 | 0.864 | **AI** | 311340ms | curated |
| extend_cut | `grok-4.5` | `v2_grounded` | 0 | accuracy | 24 | 0.917 | 1.000 | **AI** | 32332ms | curated |
| plate_rank | `grok-4.5` | `v2_picture_book` | 0 | error | 0 | 0.000 | 0.000 | **error** | 0ms | draft |

Per-sample details: `details.json` in this run folder.
