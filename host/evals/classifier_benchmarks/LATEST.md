# Classifier benchmarks — history

Updated: 2026-07-23 00:56:25Z

Open **`reports/history.html`** for interactive charts (model / prompt / task over time).

## Top configuration per task (best AI score in history)

| Task | Metric | Model | Prompt | Temp | AI | Baseline | Δ vs base | Winner | n | When (UTC) | Run |
|------|--------|-------|--------|------|----|----------|-----------|--------|---|------------|-----|
| `ambient_sfx` | mean_token_jaccard | `grok-4.5` | `v2_grounded` | 0.2 | **0.897** | 0.742 | +0.156 | **AI** | 30 | 2026-07-21 21:09:15Z | `20260721T210915Z_acb202` |
| `extend_cut` | accuracy | `grok-4.5` | `v2_grounded` | 0 | **1.000** | 0.917 | +0.083 | **AI** | 24 | 2026-07-22 16:00:13Z | `20260722T160013Z_3723f7` |
| `onscreen_cast` | mean_set_f1 | `claude-fable-5` | `v2_grounded` | 0 | **0.990** | 0.812 | +0.178 | **AI** | 20 | 2026-07-22 02:25:02Z | `20260722T022502Z_146ee3` |
| `plate_rank` | mean_recall_at_3_capped | `grok-4.5` | `v2_picture_book` | 0 | **1.000** | 0.500 | +0.500 | **AI** | 2 | 2026-07-23 00:56:17Z | `20260723T005617Z_23f629` |
| `silent_beat_action` | accuracy | `grok-4.5` | `v2_product` | 0 | **0.898** | 0.469 | +0.429 | **AI** | 147 | 2026-07-22 23:27:20Z | `20260722T232720Z_75eba7` |
| `species_kind` | accuracy | `claude-sonnet-5` | `v1_product` | 0 | **0.882** | 0.490 | +0.392 | **AI** | 51 | 2026-07-22 02:23:55Z | `20260722T022355Z_c5b399` |

## Latest runs

| When (UTC) | Run | Task | Model | Prompt | Temp | Metric | Baseline | AI | Winner | n |
|------------|-----|------|-------|--------|------|--------|----------|----|--------|---|
| 2026-07-23 00:56:17Z | `20260723T005617Z_23f629` | plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-23 00:53:23Z | `20260723T005323Z_8d18a1` | onscreen_cast | `grok-4.5` | `v2_grounded` | 0 | mean_set_f1 | 0.811 | 0.976 | **AI** | 21 |
| 2026-07-23 00:51:24Z | `20260723T005124Z_1edb1d` | onscreen_cast | `grok-4.5` | `v2_grounded` | 0 | mean_set_f1 | 0.821 | 0.967 | **AI** | 21 |
| 2026-07-22 23:27:20Z | `20260722T232720Z_75eba7` | onscreen_cast | `grok-4.5` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.975 | **AI** | 20 |
| 2026-07-22 23:27:20Z | `20260722T232720Z_75eba7` | silent_beat_action | `grok-4.5` | `v2_product` | 0 | accuracy | 0.469 | 0.898 | **AI** | 147 |
| 2026-07-22 23:27:20Z | `20260722T232720Z_75eba7` | ambient_sfx | `grok-4.5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.872 | **AI** | 30 |
| 2026-07-22 23:27:20Z | `20260722T232720Z_75eba7` | species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 0.490 | 0.843 | **AI** | 51 |
| 2026-07-22 23:17:56Z | `20260722T231756Z_36b727` | ambient_sfx | `grok-4.5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.889 | **AI** | 30 |
| 2026-07-22 19:30:44Z | `20260722T193044Z_1a22a5` | silent_beat_action | `grok-4.5` | `v2_product` | 0 | accuracy | 0.469 | 0.898 | **AI** | 147 |
| 2026-07-22 16:14:12Z | `20260722T161412Z_7d7369` | plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-22 16:00:13Z | `20260722T160013Z_3723f7` | ambient_sfx | `grok-4.5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.872 | **AI** | 30 |
| 2026-07-22 16:00:13Z | `20260722T160013Z_3723f7` | species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 0.490 | 0.863 | **AI** | 51 |
| 2026-07-22 16:00:13Z | `20260722T160013Z_3723f7` | onscreen_cast | `grok-4.5` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.975 | **AI** | 20 |
| 2026-07-22 16:00:13Z | `20260722T160013Z_3723f7` | silent_beat_action | `grok-4.5` | `v2_product` | 0 | accuracy | 0.469 | 0.864 | **AI** | 147 |
| 2026-07-22 16:00:13Z | `20260722T160013Z_3723f7` | extend_cut | `grok-4.5` | `v2_grounded` | 0 | accuracy | 0.917 | 1.000 | **AI** | 24 |
| 2026-07-22 16:00:13Z | `20260722T160013Z_3723f7` | plate_rank | `grok-4.5` | `v2_picture_book` | 0 | error | 0.000 | 0.000 | **error** | 0 |
| 2026-07-22 16:01:38Z | `20260722T160138Z_3b78f0` | ambient_sfx | `grok-4.5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.889 | **AI** | 30 |
| 2026-07-22 16:01:38Z | `20260722T160138Z_3b78f0` | ambient_sfx | `grok-4.5` | `v3_precision` | 0 | mean_token_jaccard | 0.742 | 0.878 | **AI** | 30 |
| 2026-07-22 16:01:20Z | `20260722T160120Z_122ac4` | species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 0.490 | 0.843 | **AI** | 51 |
| 2026-07-22 16:01:20Z | `20260722T160120Z_122ac4` | species_kind | `grok-4.5` | `v2_focused` | 0 | accuracy | 0.490 | 0.804 | **AI** | 51 |
| 2026-07-22 15:59:41Z | `20260722T155941Z_d384d0` | species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 0.490 | 0.863 | **AI** | 51 |
| 2026-07-22 02:46:52Z | `20260722T024652Z_69384f` | extend_cut | `grok-4.5` | `v2_grounded` | 0 | accuracy | 0.917 | 1.000 | **AI** | 24 |
| 2026-07-22 02:46:52Z | `20260722T024652Z_69384f` | extend_cut | `claude-sonnet-5` | `v2_grounded` | 0 | accuracy | 0.917 | 0.958 | **AI** | 24 |
| 2026-07-22 02:46:52Z | `20260722T024652Z_69384f` | extend_cut | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | accuracy | 0.917 | 0.958 | **AI** | 24 |
| 2026-07-22 02:46:52Z | `20260722T024652Z_69384f` | extend_cut | `claude-fable-5` | `v2_grounded` | 0 | accuracy | 0.917 | 1.000 | **AI** | 24 |
| 2026-07-22 02:22:29Z | `20260722T022229Z_26f5a0` | silent_beat_action | `grok-4.5` | `v2_product` | 0 | accuracy | 0.469 | 0.850 | **AI** | 147 |
| 2026-07-22 02:22:29Z | `20260722T022229Z_26f5a0` | silent_beat_action | `claude-sonnet-5` | `v2_product` | 0 | accuracy | 0.469 | 0.769 | **AI** | 147 |
| 2026-07-22 02:22:29Z | `20260722T022229Z_26f5a0` | silent_beat_action | `claude-haiku-4-5-20251001` | `v2_product` | 0 | accuracy | 0.469 | 0.782 | **AI** | 147 |
| 2026-07-22 02:22:29Z | `20260722T022229Z_26f5a0` | silent_beat_action | `claude-fable-5` | `v2_product` | 0 | accuracy | 0.469 | 0.850 | **AI** | 147 |
| 2026-07-22 02:27:34Z | `20260722T022734Z_ad3b25` | plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-22 02:27:34Z | `20260722T022734Z_ad3b25` | plate_rank | `claude-sonnet-5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-22 02:27:34Z | `20260722T022734Z_ad3b25` | plate_rank | `claude-haiku-4-5-20251001` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-22 02:27:34Z | `20260722T022734Z_ad3b25` | plate_rank | `claude-fable-5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-22 02:26:29Z | `20260722T022629Z_dab9ab` | extend_cut | `grok-4.5` | `v2_grounded` | 0 | accuracy | 0.917 | 0.958 | **AI** | 24 |
| 2026-07-22 02:26:29Z | `20260722T022629Z_dab9ab` | extend_cut | `claude-sonnet-5` | `v2_grounded` | 0 | accuracy | 0.917 | 0.958 | **AI** | 24 |
| 2026-07-22 02:26:29Z | `20260722T022629Z_dab9ab` | extend_cut | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | accuracy | 0.917 | 0.958 | **AI** | 24 |
| 2026-07-22 02:26:29Z | `20260722T022629Z_dab9ab` | extend_cut | `claude-fable-5` | `v2_grounded` | 0 | accuracy | 0.917 | 0.958 | **AI** | 24 |
| 2026-07-22 02:25:02Z | `20260722T022502Z_146ee3` | onscreen_cast | `grok-4.5` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.975 | **AI** | 20 |
| 2026-07-22 02:25:02Z | `20260722T022502Z_146ee3` | onscreen_cast | `claude-sonnet-5` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.950 | **AI** | 20 |
| 2026-07-22 02:25:02Z | `20260722T022502Z_146ee3` | onscreen_cast | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.910 | **AI** | 20 |
| 2026-07-22 02:25:02Z | `20260722T022502Z_146ee3` | onscreen_cast | `claude-fable-5` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.990 | **AI** | 20 |
| 2026-07-22 02:23:55Z | `20260722T022355Z_c5b399` | species_kind | `grok-4.5` | `v1_product` | 0 | accuracy | 0.490 | 0.863 | **AI** | 51 |
| 2026-07-22 02:23:55Z | `20260722T022355Z_c5b399` | species_kind | `claude-sonnet-5` | `v1_product` | 0 | accuracy | 0.490 | 0.882 | **AI** | 51 |
| 2026-07-22 02:23:55Z | `20260722T022355Z_c5b399` | species_kind | `claude-haiku-4-5-20251001` | `v1_product` | 0 | accuracy | 0.490 | 0.882 | **AI** | 51 |
| 2026-07-22 02:23:55Z | `20260722T022355Z_c5b399` | species_kind | `claude-fable-5` | `v1_product` | 0 | accuracy | 0.490 | 0.882 | **AI** | 51 |
| 2026-07-22 02:22:27Z | `20260722T022227Z_f73a7a` | ambient_sfx | `grok-4.5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.872 | **AI** | 30 |
| 2026-07-22 02:22:27Z | `20260722T022227Z_f73a7a` | ambient_sfx | `claude-sonnet-5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.867 | **AI** | 30 |
| 2026-07-22 02:22:27Z | `20260722T022227Z_f73a7a` | ambient_sfx | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.783 | **AI** | 30 |
| 2026-07-22 02:22:27Z | `20260722T022227Z_f73a7a` | ambient_sfx | `claude-fable-5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.864 | **AI** | 30 |
| 2026-07-22 02:17:25Z | `20260722T021725Z_46e8d2` | plate_rank | `claude-haiku-4-5-20251001` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-22 02:15:10Z | `20260722T021510Z_b85532` | plate_rank | `claude-haiku-4-5-20251001` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 0.000 | **baseline** | 2 |
| 2026-07-22 01:55:16Z | `20260722T015516Z_000209` | plate_rank | `claude-haiku-4-5-20251001` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 0.000 | **baseline** | 2 |
| 2026-07-22 01:55:16Z | `20260722T015516Z_000209` | plate_rank | `claude-fable-5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-22 01:54:55Z | `20260722T015455Z_23bce2` | extend_cut | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | accuracy | 0.917 | 0.958 | **AI** | 24 |
| 2026-07-22 01:54:55Z | `20260722T015455Z_23bce2` | extend_cut | `claude-fable-5` | `v2_grounded` | 0 | accuracy | 0.917 | 0.958 | **AI** | 24 |
| 2026-07-22 01:51:06Z | `20260722T015106Z_55356a` | silent_beat_action | `claude-haiku-4-5-20251001` | `v2_product` | 0 | accuracy | 0.469 | 0.701 | **AI** | 147 |
| 2026-07-22 01:51:06Z | `20260722T015106Z_55356a` | silent_beat_action | `claude-fable-5` | `v2_product` | 0 | accuracy | 0.469 | 0.864 | **AI** | 147 |
| 2026-07-22 01:50:33Z | `20260722T015033Z_c40f36` | onscreen_cast | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.910 | **AI** | 20 |
| 2026-07-22 01:50:33Z | `20260722T015033Z_c40f36` | onscreen_cast | `claude-fable-5` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.990 | **AI** | 20 |
| 2026-07-22 01:50:05Z | `20260722T015005Z_62ba96` | species_kind | `claude-haiku-4-5-20251001` | `v1_product` | 0 | accuracy | 0.490 | 0.882 | **AI** | 51 |
| 2026-07-22 01:50:05Z | `20260722T015005Z_62ba96` | species_kind | `claude-fable-5` | `v1_product` | 0 | accuracy | 0.490 | 0.863 | **AI** | 51 |
| 2026-07-22 01:49:19Z | `20260722T014919Z_70cbcd` | ambient_sfx | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.839 | **AI** | 30 |
| 2026-07-22 01:49:19Z | `20260722T014919Z_70cbcd` | ambient_sfx | `claude-fable-5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.864 | **AI** | 30 |
| 2026-07-22 01:43:22Z | `20260722T014322Z_c4c719` | extend_cut | `claude-sonnet-5` | `v2_grounded` | 0 | accuracy | 0.917 | 0.000 | **baseline** | 24 |
| 2026-07-22 01:43:22Z | `20260722T014322Z_c4c719` | extend_cut | `claude-sonnet-5` | `v3_speaker_cue` | 0 | accuracy | 0.917 | 0.917 | **tie** | 24 |
| 2026-07-22 01:41:39Z | `20260722T014139Z_fde75a` | plate_rank | `claude-sonnet-5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-22 01:41:17Z | `20260722T014117Z_cae4d8` | extend_cut | `claude-sonnet-5` | `v2_grounded` | 0 | accuracy | 0.917 | 0.917 | **tie** | 24 |
| 2026-07-22 01:39:02Z | `20260722T013902Z_f13a84` | silent_beat_action | `claude-sonnet-5` | `v2_product` | 0 | accuracy | 0.469 | 0.762 | **AI** | 147 |
| 2026-07-22 01:38:42Z | `20260722T013842Z_6a2b84` | onscreen_cast | `claude-sonnet-5` | `v2_grounded` | 0 | mean_set_f1 | 0.812 | 0.967 | **AI** | 20 |
| 2026-07-22 01:38:26Z | `20260722T013826Z_2a0d1a` | species_kind | `claude-sonnet-5` | `v1_product` | 0 | accuracy | 0.490 | 0.882 | **AI** | 51 |
| 2026-07-22 01:38:07Z | `20260722T013807Z_1bb9b9` | ambient_sfx | `claude-sonnet-5` | `v2_grounded` | 0 | mean_token_jaccard | 0.742 | 0.881 | **AI** | 30 |
| 2026-07-22 01:37:20Z | `20260722T013720Z_da862d` | ambient_sfx | `claude-sonnet-5` | `v2_grounded` | 0 | error | 0.000 | 0.000 | **error** | 0 |
| 2026-07-22 01:32:40Z | `20260722T013240Z_e4b765` | ambient_sfx | `claude-sonnet-5` | `v2_grounded` | 0 | error | 0.000 | 0.000 | **error** | 0 |
| 2026-07-21 23:00:19Z | `20260721T230019Z_cd9f6b` | plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 0.500 | 1.000 | **AI** | 2 |
| 2026-07-21 22:54:24Z | `20260721T225424Z_8caca8` | plate_rank | `grok-4.5` | `v1_product` | 0 | mean_recall_at_3_capped | 1.000 | 0.500 | **baseline** | 2 |
| 2026-07-21 22:54:24Z | `20260721T225424Z_8caca8` | plate_rank | `grok-4.5` | `v2_picture_book` | 0 | mean_recall_at_3_capped | 1.000 | 1.000 | **tie** | 2 |
| 2026-07-21 22:47:27Z | `20260721T224727Z_ca0e4c` | plate_rank | `grok-4.5` | `v1_product` | 0 | mean_recall_at_3_capped | 0.333 | 0.333 | **tie** | 2 |
| 2026-07-21 22:47:08Z | `20260721T224708Z_281c87` | plate_rank | `grok-4.5` | `v1_product` | 0 | mean_recall_at_3_capped | 0.833 | 0.833 | **tie** | 2 |
| 2026-07-21 22:43:50Z | `20260721T224350Z_2252c9` | plate_rank | `grok-4.5` | `v1_product` | 0 | mean_recall_at_3_capped | 0.333 | 0.333 | **tie** | 2 |

## AI score trend by task (newest first)

### `ambient_sfx`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260722T232720Z_75eba7` | `grok-4.5` | `v2_grounded` | 0 | 0.872 | 0.742 |
| `20260722T231756Z_36b727` | `grok-4.5` | `v2_grounded` | 0 | 0.889 | 0.742 |
| `20260722T160013Z_3723f7` | `grok-4.5` | `v2_grounded` | 0 | 0.872 | 0.742 |
| `20260722T160138Z_3b78f0` | `grok-4.5` | `v2_grounded` | 0 | 0.889 | 0.742 |
| `20260722T160138Z_3b78f0` | `grok-4.5` | `v3_precision` | 0 | 0.878 | 0.742 |
| `20260722T022227Z_f73a7a` | `grok-4.5` | `v2_grounded` | 0 | 0.872 | 0.742 |
| `20260722T022227Z_f73a7a` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.867 | 0.742 |
| `20260722T022227Z_f73a7a` | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | 0.783 | 0.742 |
| `20260722T022227Z_f73a7a` | `claude-fable-5` | `v2_grounded` | 0 | 0.864 | 0.742 |
| `20260722T014919Z_70cbcd` | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | 0.839 | 0.742 |
| `20260722T014919Z_70cbcd` | `claude-fable-5` | `v2_grounded` | 0 | 0.864 | 0.742 |
| `20260722T013807Z_1bb9b9` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.881 | 0.742 |
| `20260722T013720Z_da862d` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.000 | 0.000 |
| `20260722T013240Z_e4b765` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.000 | 0.000 |
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
| `20260722T160013Z_3723f7` | `grok-4.5` | `v2_grounded` | 0 | 1.000 | 0.917 |
| `20260722T024652Z_69384f` | `grok-4.5` | `v2_grounded` | 0 | 1.000 | 0.917 |
| `20260722T024652Z_69384f` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.958 | 0.917 |
| `20260722T024652Z_69384f` | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | 0.958 | 0.917 |
| `20260722T024652Z_69384f` | `claude-fable-5` | `v2_grounded` | 0 | 1.000 | 0.917 |
| `20260722T022629Z_dab9ab` | `grok-4.5` | `v2_grounded` | 0 | 0.958 | 0.917 |
| `20260722T022629Z_dab9ab` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.958 | 0.917 |
| `20260722T022629Z_dab9ab` | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | 0.958 | 0.917 |
| `20260722T022629Z_dab9ab` | `claude-fable-5` | `v2_grounded` | 0 | 0.958 | 0.917 |
| `20260722T015455Z_23bce2` | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | 0.958 | 0.917 |
| `20260722T015455Z_23bce2` | `claude-fable-5` | `v2_grounded` | 0 | 0.958 | 0.917 |
| `20260722T014322Z_c4c719` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.000 | 0.917 |
| `20260722T014322Z_c4c719` | `claude-sonnet-5` | `v3_speaker_cue` | 0 | 0.917 | 0.917 |
| `20260722T014117Z_cae4d8` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.917 | 0.917 |
| `20260721T222801Z_acdf1d` | `grok-4.5` | `v1_product` | 0 | 1.000 | 0.917 |
| `20260721T222801Z_acdf1d` | `grok-4.5` | `v2_grounded` | 0 | 0.958 | 0.917 |

### `onscreen_cast`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260723T005323Z_8d18a1` | `grok-4.5` | `v2_grounded` | 0 | 0.976 | 0.811 |
| `20260723T005124Z_1edb1d` | `grok-4.5` | `v2_grounded` | 0 | 0.967 | 0.821 |
| `20260722T232720Z_75eba7` | `grok-4.5` | `v2_grounded` | 0 | 0.975 | 0.812 |
| `20260722T160013Z_3723f7` | `grok-4.5` | `v2_grounded` | 0 | 0.975 | 0.812 |
| `20260722T022502Z_146ee3` | `grok-4.5` | `v2_grounded` | 0 | 0.975 | 0.812 |
| `20260722T022502Z_146ee3` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.950 | 0.812 |
| `20260722T022502Z_146ee3` | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | 0.910 | 0.812 |
| `20260722T022502Z_146ee3` | `claude-fable-5` | `v2_grounded` | 0 | 0.990 | 0.812 |
| `20260722T015033Z_c40f36` | `claude-haiku-4-5-20251001` | `v2_grounded` | 0 | 0.910 | 0.812 |
| `20260722T015033Z_c40f36` | `claude-fable-5` | `v2_grounded` | 0 | 0.990 | 0.812 |
| `20260722T013842Z_6a2b84` | `claude-sonnet-5` | `v2_grounded` | 0 | 0.967 | 0.812 |
| `20260721T213532Z_254377` | `grok-4.5` | `v1_product` | 0 | 0.910 | 0.812 |
| `20260721T213532Z_254377` | `grok-4.5` | `v2_grounded` | 0 | 0.975 | 0.812 |

### `plate_rank`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260723T005617Z_23f629` | `grok-4.5` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260722T161412Z_7d7369` | `grok-4.5` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260722T160013Z_3723f7` | `grok-4.5` | `v2_picture_book` | 0 | 0.000 | 0.000 |
| `20260722T022734Z_ad3b25` | `grok-4.5` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260722T022734Z_ad3b25` | `claude-sonnet-5` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260722T022734Z_ad3b25` | `claude-haiku-4-5-20251001` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260722T022734Z_ad3b25` | `claude-fable-5` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260722T021725Z_46e8d2` | `claude-haiku-4-5-20251001` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260722T021510Z_b85532` | `claude-haiku-4-5-20251001` | `v2_picture_book` | 0 | 0.000 | 0.500 |
| `20260722T015516Z_000209` | `claude-haiku-4-5-20251001` | `v2_picture_book` | 0 | 0.000 | 0.500 |
| `20260722T015516Z_000209` | `claude-fable-5` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260722T014139Z_fde75a` | `claude-sonnet-5` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260721T230019Z_cd9f6b` | `grok-4.5` | `v2_picture_book` | 0 | 1.000 | 0.500 |
| `20260721T225424Z_8caca8` | `grok-4.5` | `v1_product` | 0 | 0.500 | 1.000 |
| `20260721T225424Z_8caca8` | `grok-4.5` | `v2_picture_book` | 0 | 1.000 | 1.000 |
| `20260721T224727Z_ca0e4c` | `grok-4.5` | `v1_product` | 0 | 0.333 | 0.333 |
| `20260721T224708Z_281c87` | `grok-4.5` | `v1_product` | 0 | 0.833 | 0.833 |
| `20260721T224350Z_2252c9` | `grok-4.5` | `v1_product` | 0 | 0.333 | 0.333 |

### `silent_beat_action`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260722T232720Z_75eba7` | `grok-4.5` | `v2_product` | 0 | 0.898 | 0.469 |
| `20260722T193044Z_1a22a5` | `grok-4.5` | `v2_product` | 0 | 0.898 | 0.469 |
| `20260722T160013Z_3723f7` | `grok-4.5` | `v2_product` | 0 | 0.864 | 0.469 |
| `20260722T022229Z_26f5a0` | `grok-4.5` | `v2_product` | 0 | 0.850 | 0.469 |
| `20260722T022229Z_26f5a0` | `claude-sonnet-5` | `v2_product` | 0 | 0.769 | 0.469 |
| `20260722T022229Z_26f5a0` | `claude-haiku-4-5-20251001` | `v2_product` | 0 | 0.782 | 0.469 |
| `20260722T022229Z_26f5a0` | `claude-fable-5` | `v2_product` | 0 | 0.850 | 0.469 |
| `20260722T015106Z_55356a` | `claude-haiku-4-5-20251001` | `v2_product` | 0 | 0.701 | 0.469 |
| `20260722T015106Z_55356a` | `claude-fable-5` | `v2_product` | 0 | 0.864 | 0.469 |
| `20260722T013902Z_f13a84` | `claude-sonnet-5` | `v2_product` | 0 | 0.762 | 0.469 |
| `20260721T220045Z_f6fc27` | `grok-4.5` | `v2_product` | 0 | 0.871 | 0.469 |

### `species_kind`

| Run | Model | Prompt | Temp | AI | Baseline |
|-----|-------|--------|------|----|----------|
| `20260722T232720Z_75eba7` | `grok-4.5` | `v1_product` | 0 | 0.843 | 0.490 |
| `20260722T160013Z_3723f7` | `grok-4.5` | `v1_product` | 0 | 0.863 | 0.490 |
| `20260722T160120Z_122ac4` | `grok-4.5` | `v1_product` | 0 | 0.843 | 0.490 |
| `20260722T160120Z_122ac4` | `grok-4.5` | `v2_focused` | 0 | 0.804 | 0.490 |
| `20260722T155941Z_d384d0` | `grok-4.5` | `v1_product` | 0 | 0.863 | 0.490 |
| `20260722T022355Z_c5b399` | `grok-4.5` | `v1_product` | 0 | 0.863 | 0.490 |
| `20260722T022355Z_c5b399` | `claude-sonnet-5` | `v1_product` | 0 | 0.882 | 0.490 |
| `20260722T022355Z_c5b399` | `claude-haiku-4-5-20251001` | `v1_product` | 0 | 0.882 | 0.490 |
| `20260722T022355Z_c5b399` | `claude-fable-5` | `v1_product` | 0 | 0.882 | 0.490 |
| `20260722T015005Z_62ba96` | `claude-haiku-4-5-20251001` | `v1_product` | 0 | 0.882 | 0.490 |
| `20260722T015005Z_62ba96` | `claude-fable-5` | `v1_product` | 0 | 0.863 | 0.490 |
| `20260722T013826Z_2a0d1a` | `claude-sonnet-5` | `v1_product` | 0 | 0.882 | 0.490 |
| `20260721T205232Z_9f5c1e` | `grok-4.5` | `v1_product` | 0 | 0.863 | 0.490 |
| `20260721T204453Z_dd5b72` | `grok-4.5` | `v1_product` | 0 | 0.863 | 0.490 |
