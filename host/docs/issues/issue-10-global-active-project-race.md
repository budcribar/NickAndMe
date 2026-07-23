# Issue 10 — Global ActiveProjectId races across concurrent jobs

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | open |
| Branch | `fix/issue-10-global-active-project-race` |
| Related files | host/FilmStudio.Engine/ProjectStore.cs (ActivateAsync); FilmJobService ActivateAsync call sites |

## Problem

ActivateAsync writes a process-global ActiveProjectId and projects/workspace.json. Concurrent jobs on different projects race this singleton. Fallbacks to _projects.ActiveProjectId when ProjectId is omitted can target the wrong project mid-run. Explicit projectId on most job requests mitigates but does not fix global activate side effects from background jobs.

## Suggested fix

Stop mutating global active project from background jobs; keep activate as a UI-only preference. Require projectId on all job DTOs (reject empty).

## Notes

Tracked from the FilmStudio.Api / Core / Engine code review (2026-07). This branch documents the problem only; implementation is follow-up work on this branch.