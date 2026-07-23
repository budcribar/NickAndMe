# Issue 11 — Partial scene gen reports done instead of partial

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | **fixed** |
| Branch | `fix/issue-11-partial-scene-status` |
| Related files | `host/FilmStudio.Engine/FilmJobService.cs`; `host/FilmStudio.Web/Components/Pages/Scenes.razor` |

## Problem

Scene gen continued after individual clip failures and finished status `"done"` with "Finished with errors (N ok, M failed)" when any clip succeeded. Partial scenes looked successful; later clips then failed with "generate previous first," and remux could assemble a holey sequence without a clear job signal.

## Fix implemented

1. **Job status** — mixed success uses `"partial"` (not `"done"`). All failed → `"error"`. All ok → `"done"`. Same for batch gen.
2. **Stop on first failure** for full-scene gen (`req.Clip` not set): sequential clips need previous on disk; remaining clips are not attempted after the first failure.
3. **UI** — Scenes/Review treat `"partial"` as a terminal status so soft-reload runs.

## Suggested fix (original)

Optional stopOnFirstFailure (default true for full-scene gen); or mark scene incomplete and refuse remux until missing/failed clips are resolved. Keep batch partial success but surface a distinct status ("partial") instead of "done".
