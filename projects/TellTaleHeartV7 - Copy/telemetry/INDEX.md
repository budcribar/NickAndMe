# Project telemetry

| File | Purpose |
|------|---------|
| `cost_ledger.json` | Snapshot of cost events from `pipeline_state` (list rates) |
| `models.json` | Resolved model/options snapshot at last artifact-index rebuild |
| `api_calls.jsonl` | Append-only: one JSON line per live API call (full prompts) |
| `ffmpeg.jsonl` | Append-only: condensed remux / WIP / frame-sample ops |

`api_calls` and `ffmpeg` are written during jobs (project scope).  
Rebuild this folder’s snapshots via `POST /api/projects/{id}/artifacts/index`.
