# Project artifacts — `TellTaleHeartV7`

Map of project-local artifacts for manual whole-project review (e.g. point Claude/Codex at this folder). Zip export deferred; data lives here.

Built: **2026-07-22 20:25:39Z** · Ready for manual final review: **no**

## Stats

- **clipMp4Count**: 20
- **sceneCompositeCount**: 2
- **promptTxtCount**: 20
- **promptMetaCount**: 20
- **autoReviewDraftCount**: 0
- **hasReviewIndex**: False
- **reviewFrameCount**: 0
- **hasWip**: True

## Missing (recommended for manual review)

- `assets/review/index.json`

## Map

| Present | Path | Role |
|--------:|------|------|
| yes | `source/book_full.txt` | Source book / prose text *(core)* |
| yes | `source/screenplay.fountain` | Signed/working Fountain screenplay *(core)* |
| yes | `source/screenplay_meta.json` | Screenplay sign-off metadata |
| yes | `source/cast_seeds.json` | Cast seeds (looks, locks, voices) *(core)* |
| no | `source/tell_tale_heart.fountain` | Imported Poe fountain (if used) |
| yes | `project.json` | Project id/title |
| yes | `project_rules.json` | Approved house rules / style locks *(core)* |
| yes | `pipeline_state.json` | Clip reviews, auto-review state, cost_ledger *(core)* |
| no | `pipeline_config.json` | Per-project gen config (model, resolution) |
| no | `edit_feedback_log.json` | Human edit / pass-fail log |
| yes | `blueprint.clips.grok.json` | Stage 2 shot plan / clips *(core)* |
| yes | `assets/movie_wip.mp4` | Full cut (WIP) *(core)* |
| yes | `assets/movie_wip.mp4.sources.json` | WIP concat sources + assembly note |
| yes | `assets/characters` | Locked character plates + variants *(core)* |
| yes | `assets/video` | Clips + scene composites + duration sidecars *(core)* |
| yes | `assets/video/prompts` | Full prompt .txt + .meta.json per clip *(core)* |
| yes | `assets/review` | Auto-review drafts, frames, index *(core)* |
| no | `assets/review/index.json` | Per-clip review index (rebuild via batch review) *(core)* |
| no | `assets/review/frames` | Durable auto-review sample frames |
| no | `assets/review/final_review.json` | Manual/AI final rubric scores (when filled) |
| yes | `assets/review/FINAL_REVIEW_TEMPLATE.json` | Rubric template for manual final review |
| yes | `telemetry/cost_ledger.json` | Cost events snapshot (from pipeline_state) *(core)* |
| yes | `telemetry/models.json` | Resolved models/options snapshot |
| yes | `telemetry/api_calls.jsonl` | Live API call log (full prompts) |
| yes | `telemetry/ffmpeg.jsonl` | Condensed ffmpeg ops |
| yes | `ARTIFACTS.md` | Human map of this project for Claude/manual review |
| yes | `artifact_index.json` | Machine-readable artifact presence map |
| yes | `assets/video/scene_01.mp4.sources.json` | Scene remux include/exclude manifest |
| yes | `assets/video/scene_02.mp4.sources.json` | Scene remux include/exclude manifest |

## How to review manually (Claude / external AI)

1. Open **this project folder** in Claude Code (or similar) — not a zip.
2. Start with `ARTIFACTS.md` + `artifact_index.json` (this map).
3. Story triad: `source/book_full.txt` + `source/screenplay.fountain` + `assets/movie_wip.mp4`.
4. Identity: `assets/characters/*_ref.png`, `assets/video/prompts/*.meta.json` (`prompt`, `castCount`, `refsAttachedToApi`).
5. QC: `assets/review/*.auto_review.json`, `assets/review/index.json`, `assets/review/frames/`.
6. Assembly: `assets/video/scene_*.mp4.sources.json` (`included` / `excluded`).
7. Telemetry: `telemetry/api_calls.jsonl` (full prompts), `telemetry/ffmpeg.jsonl`, `telemetry/cost_ledger.json`.
8. Scores: copy `assets/review/FINAL_REVIEW_TEMPLATE.json` → `final_review.json` and fill **human** (and optionally **ai** notes).
9. Zip export is deferred — all durable data stays in this directory.

Refresh this map: `POST /api/projects/{id}/artifacts/index` or Review UI **Refresh artifact map**.

