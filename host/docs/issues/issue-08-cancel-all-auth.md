# Issue 8 — Cancel-all has no user/admin check

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | open |
| Branch | `fix/issue-8-cancel-all-auth` |
| Related files | host/FilmStudio.Api/Program.cs (POST /api/jobs/cancel); host/FilmStudio.Engine/FilmJobService.cs (~155-174) |

## Problem

POST /api/jobs/cancel with no body cancels all in-flight job cancellation tokens with no user or admin check. On multi-user hosts, any client can cancel everyone else's work. Per-job cancel at /api/jobs/{jobId}/cancel correctly scopes non-admin callers to the job owner.

## Suggested fix

Remove cancel-all from the non-admin API, or restrict it to admin only. Default cancel without a job id to the current user's jobs only.

## Notes

Tracked from the FilmStudio.Api / Core / Engine code review (2026-07). This branch documents the problem only; implementation is follow-up work on this branch.

