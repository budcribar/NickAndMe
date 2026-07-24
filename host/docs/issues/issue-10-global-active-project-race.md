# Issue 10 — Global ActiveProjectId races across concurrent jobs

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | fixed |
| Branch | `fix/issue-10-global-active-project-race` |
| Related files | host/PageToMovie.Engine/ProjectStore.cs (ActivateAsync); FilmJobService ActivateAsync call sites |

## Problem

ActivateAsync writes a process-global ActiveProjectId and projects/workspace.json. Concurrent jobs on different projects race this singleton. Fallbacks to _projects.ActiveProjectId when ProjectId is omitted can target the wrong project mid-run. Explicit projectId on most job requests mitigates but does not fix global activate side effects from background jobs.

## Suggested fix

Stop mutating global active project from background jobs; keep activate as a UI-only preference. Require projectId on all job DTOs (reject empty).

## Fix

- Added `ProjectStore.RequireProjectAsync` — validates a project exists without touching
  `_activeProjectId` or writing `workspace.json`. `ActivateAsync` now delegates to it and
  is reserved for the explicit `POST /api/projects/{id}/activate` UI action.
- Every background job runner in `FilmJobService` (`RunSceneGenAsync`, `RunBatchGenAsync`,
  `RunStage1Async`, `RunStage2Async`, `RunBookPrepareAsync`, `RunBookImportAsync`,
  `RunCharacterVariantsAsync`, `RunVoicePreviewAsync`, `RunClipAutoReviewAsync`,
  `RunClipAutoReviewBatchAsync`, `RunSortCharacterPlatesAsync`, `RunRemuxAsync`) now calls
  `RequireProjectAsync` instead of `ActivateAsync`, so executing a job never flips the
  global active project out from under the UI or another concurrent job.
- The `projectId` fallback to `_projects.ActiveProjectId` (when a request omits it) is now
  resolved exactly once, in the synchronous `Start*Async` method at enqueue time, and
  threaded explicitly into the corresponding `Run*Async` method as a parameter — the
  background task no longer re-reads the global active project a second time at execution,
  which could previously return a different (raced) value than what was used for locks/
  metadata at enqueue time.
- Covered by `PageToMovie.Tests/Api/ProjectActivationRaceTests.cs`.

## Notes

Tracked from the PageToMovie.Api / Core / Engine code review (2026-07). This branch documents the problem only; implementation is follow-up work on this branch.