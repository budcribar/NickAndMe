# Benchmark run `20260722T232720Z_75eba7`

- **UTC:** 2026-07-22 23:27:20Z
- **Project:** `The_Jungle_Book`
- **Models:** grok-4.5
- **Prompts:** 
- **Tasks:** onscreen_cast, silent_beat_action, ambient_sfx, species_kind

| Task | Model | Prompt | Temp | Metric | n | Baseline | AI | Winner | Latency | Gold |
|------|-------|--------|------|--------|---|----------|----|--------|---------|------|
| onscreen_cast | `grok-4.5` | `v2_grounded` | 0 | mean_set_f1 | 20 | 0.812 | 0.975 | **AI** | 34164ms | curated |
| silent_beat_action | `grok-4.5` | `v2_product` | 0 | accuracy | 147 | 0.469 | 0.898 | **AI** | 255046ms | curated |
| ambient_sfx | `grok-4.5` | `v2_grounded` | 0 | mean_token_jaccard | 30 | 0.742 | 0.872 | **AI** | 37839ms | curated |
| species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 51 | 0.490 | 0.843 | **AI** | 20852ms | curated |

Per-sample details: `details.json` in this run folder.
