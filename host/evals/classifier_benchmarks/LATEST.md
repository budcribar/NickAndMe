# Classifier benchmarks — history

Updated: 2026-07-21 23:00:27Z

Open **`reports/history.html`** for interactive charts (model / prompt / task over time).

## Top configuration per task (best AI score in history)

| Task | Metric | Model | Prompt | Temp | AI | Baseline | Δ vs base | Winner | n | When (UTC) | Run |
|------|--------|-------|--------|------|----|----------|-----------|--------|---|------------|-----|
| `ambient_sfx` | mean_token_jaccard | `grok-4.5` | `v2_grounded` | 0.2 | **0.897** | 0.742 | +0.156 | **AI** | 30 | 2026-07-21 21:09:15Z | `20260721T210915Z_acb202` |
| `extend_cut` | accuracy | `grok-4.5` | `v1_product` | 0 | **1.000** | 0.917 | +0.083 | **AI** | 24 | 2026-07-21 22:28:01Z | `20260721T222801Z_acdf1d` |
| `onscreen_cast` | mean_set_f1 | `grok-4.5` | `v2_grounded` | 0 | **0.975** | 0.812 | +0.163 | **AI** | 20 | 2026-07-21 21:35:32Z | `20260721T213532Z_254377` |
| `plate_rank` | mean_recall_at_3_capped | `grok-4.5` | `v2_picture_book` | 0 | **1.000** | 0.500 | +0.500 | **AI** | 2 | 2026-07-21 23:00:19Z | `20260721T230019Z_cd9f6b` |
| `silent_beat_action` | accuracy | `grok-4.5` | `v2_product` | 0 | **0.871** | 0.469 | +0.401 | **AI** | 147 | 2026-07-21 22:00:45Z | `20260721T220045Z_f6fc27` |
| `species_kind` | accuracy | `grok-4.5` | `v1_product` | 0 | **0.863** | 0.490 | +0.373 | **AI** | 51 | 2026-07-21 20:52:32Z | `20260721T205232Z_9f5c1e` |

## Latest runs

| When (UTC) | Run | Task | Model | Prompt | Temp | Metric | Baseline | AI | Winner | n |
|------------|-----|------|-------|--------|------|--------|----------|----|--------|---|
| 2026-07-21 23:00:19Z | `20260721T230019Z_cd9f6b` | plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-21 22:54:24Z | `20260721T225424Z_8caca8` | plate_rank | `grok-4.5` | `v1_product` | 0 | mean_recall_at_3_capped | 1.000 | 0.500 | **baseline** | 2 |
| 2026-07-21 22:54:24Z | `20260721T225424Z_8caca8` | plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 1.000 | 1.000 | **tie** | 2 |
| 2026-07-21 22:47:27Z | `20260721T224727Z_ca0e4c` | plate_rank | `grok-4.5` | `v1_product` | 0 | mean_recall_at_3_capped | 0.333 | 0.333 | **tie** | 2 |
| 2026-07-21 22:47:08Z | `20260721T224708Z_281c87` | plate_rank | `grok-4.5` | `v1_product` | 0 | mean_recall_at_3_capped | 0.833 | 0.833 | **tie** | 2 |
| 2026-07-21 22:43:50Z | `20260721T224350Z_2252c9` | plate_rank | `grok-4.5` | `v1_product` | 0 | mean_recall_at_3_capped | 0.333 | 0.333 | **tie** | 2 |
| 2026-07-21 22:28:01Z | `20260721T222801Z_acdf1d` | extend_cut | `grok-4.5` | `v1_product` | 0 | accuracy | 0.917 | 1.000 | **AI** | 24 |
| 2026-07-21 22:28:01Z | `20260721T222801Z_acdf1d` | extend_cut | `grok-4.5` | `v2_grounded` | 0 | accuracy | 0.917 | 0.958 | **AI** | 24 |
| 2026-07-21 22:00:45Z | `20260721T220045Z_f6fc27` | silent_beat_action | `grok-4.5` | `v2_product` | 0 | accuracy | 0.469 | 0.871 | **AI** | 147 |
| 2026-07-21 21:35:32Z | `20260721T213532Z_254377` | onscreen_cast | `grok-4.5` | `v1_product` | 0 | mean_set_f1 | 0.812 | 0.910 | **AI** | 20 |
| 2026-07-21 21:35:32Z | `20260721T213532Z_254377` | onscreen_cast | `grok-4.5` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.975 | **AI** | 20 |
| 2026-07-21 21:09:15Z | `20260721T210915Z_acb202` | ambient_sfx | `grok-4.5` | `v1_product` | 0 | mean_token_jaccard | 0.742 | 0.850 | **AI** | 30 |
| 2026-07-21 21:09:15Z | `20260721T210915Z_acb202` | ambient_sfx | `grok-4.5` | `v1_product` | 0.2 | mean_token_jaccard | 0.742 | 0.761 | **tie** | 30 |
| 2026-07-21 21:09:15Z | `20260721T210915Z_acb202` | ambient_sfx | `grok-4.5` | `v1_no_speech_sfx` | 0 | mean_token_jaccard | 0.742 | 0.814 | **AI** | 30 |
| 2026-07-21 21:09:15Z | `20260721T210915Z_acb202` | ambient_sfx | `grok-4.5` | `v1_no_speech_sfx` | 0.2 | mean_token_jaccard | 0.742 | 0.853 | **AI** | 30 |
| 2026-07-21 21:09:15Z | `20260721T210915Z_acb202` | ambient_sfx | `grok-4.5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.872 | **AI** | 30 |
| 2026-07-21 21:09:15Z | `20260721T210915Z_acb202` | ambient_sfx | `grok-4.5` | `v2_grounded` | 0.2 | mean_token_jaccard | 0.742 | 0.897 | **AI** | 30 |
| 2026-07-21 20:52:32Z | `20260721T205232Z_9f5c1e` | ambient_sfx | `grok-4.5` | `v1_product` | 0 | mean_token_jaccard | 0.742 | 0.739 | **tie** | 30 |
| 2026-07-21 20:52:32Z | `20260721T205232Z_9f5c1e` | species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 0.490 | 0.863 | **AI** | 51 |
| 2026-07-21 20:44:53Z | `20260721T204453Z_dd5b72` | species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 0.490 | 0.863 | **AI** | 51 |
| 2026-07-21 20:43:08Z | `20260721T204308Z_22056f` | ambient_sfx | `grok-4.5` | `v1_product` | 0 | mean_token_jaccard | 0.742 | 0.786 | **AI** | 30 |
| 2026-07-21 20:43:08Z | `20260721T204308Z_22056f` | ambient_sfx | `grok-4.5` | `v1_no_speech_sfx` | 0 | mean_token_jaccard | 0.742 | 0.800 | **AI** | 30 |

## AI score trend by task (newest first)

### `ambient_sfx`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260721T210915Z_acb202` | `grok-4.5` | `v1_product` | 0 | 0.850 | 0.742 |
| `20260721T210915Z_acb202` | `grok-4.5` | `v1_product` | 0.2 | 0.761 | 0.742 |
| `20260721T210915Z_acb202` | `grok-4.5` | `v1_no_speech_sfx` | 0 | 0.814 | 0.742 |
| `20260721T210915Z_acb202` | `grok-4.5` | `v1_no_speech_sfx` | 0.2 | 0.853 | 0.742 |
| `20260721T210915Z_acb202` | `grok-4.5` | `v2_grounded` | 0 | 0.872 | 0.742 |
| `20260721T210915Z_acb202` | `grok-4.5` | `v2_grounded` | 0.2 | 0.897 | 0.742 |
| `20260721T205232Z_9f5c1e` | `grok-4.5` | `v1_product` | 0 | 0.739 | 0.742 |
| `20260721T204308Z_22056f` | `grok-4.5` | `v1_product` | 0 | 0.786 | 0.742 |
| `20260721T204308Z_22056f` | `grok-4.5` | `v1_no_speech_sfx` | 0 | 0.800 | 0.742 |

### `extend_cut`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260721T222801Z_acdf1d` | `grok-4.5` | `v1_product` | 0 | 1.000 | 0.917 |
| `20260721T222801Z_acdf1d` | `grok-4.5` | `v2_grounded` | 0 | 0.958 | 0.917 |

### `onscreen_cast`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260721T213532Z_254377` | `grok-4.5` | `v1_product` | 0 | 0.910 | 0.812 |
| `20260721T213532Z_254377` | `grok-4.5` | `v2_grounded` | 0 | 0.975 | 0.812 |

### `plate_rank`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260721T230019Z_cd9f6b` | `grok-4.5` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260721T225424Z_8caca8` | `grok-4.5` | `v1_product` | 0 | 0.500 | 1.000 |
| `20260721T225424Z_8caca8` | `grok-4.5` | `v2_picture_book` | 0 | 1.000 | 1.000 |
| `20260721T224727Z_ca0e4c` | `grok-4.5` | `v1_product` | 0 | 0.333 | 0.333 |
| `20260721T224708Z_281c87` | `grok-4.5` | `v1_product` | 0 | 0.833 | 0.833 |
| `20260721T224350Z_2252c9` | `grok-4.5` | `v1_product` | 0 | 0.333 | 0.333 |

### `silent_beat_action`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260721T220045Z_f6fc27` | `grok-4.5` | `v2_product` | 0 | 0.871 | 0.469 |

### `species_kind`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260721T205232Z_9f5c1e` | `grok-4.5` | `v1_product` | 0 | 0.863 | 0.490 |
| `20260721T204453Z_dd5b72` | `grok-4.5` | `v1_product` | 0 | 0.863 | 0.490 |
