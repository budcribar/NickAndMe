# Issue 11 — Partial scene gen reports done instead of partial

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | open |
| Branch | `fix/issue-11-partial-scene-status` |
| Related files | host/FilmStudio.Engine/FilmJobService.cs (RunSceneGenAsync finish path) |

## Problem

Scene gen continues after individual clip failures and finishes status "done" with "Finished with errors (N ok, M failed)" when any clip succeeded. Partial scenes are operable, but clip N+1 then fails with "generate previous first," and remux may assemble a holey sequence without a hard gate.

## Suggested fix

Optional stopOnFirstFailure (default true for full-scene gen); or mark scene incomplete and refuse remux until missing/failed clips are resolved. Keep batch partial success but surface a distinct status ("partial") instead of "done".

## Notes

Tracked from the FilmStudio.Api / Core / Engine code review (2026-07). This branch documents the problem only; implementation is follow-up work on this branch.