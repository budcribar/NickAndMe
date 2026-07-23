# Issue 8 — Cancel-all has no user/admin check

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | **fixed** |
| Branch | `fix/issue-8-cancel-all-auth` |
| Related files | `host/FilmStudio.Api/Program.cs`; `host/FilmStudio.Engine/FilmJobService.cs` |

## Problem

`POST /api/jobs/cancel` with no body cancelled **all** in-flight job CTSes with no user or admin check. Multi-user hosts: any client could cancel everyone else's work. Per-job cancel at `/api/jobs/{jobId}/cancel` already scoped non-admin to the job owner.

## Fix implemented

1. **`FilmJobService.CancelAsync`**: bulk cancel requires `userId` (own jobs) or `cancelAllUsers: true`. Unscoped bulk returns 0 cancelled. Single-job path unchanged.
2. **`POST /api/jobs/cancel`**: injects `IUserContext`. Non-admin always cancels **own** jobs only. Admin may pass `?all=true` to cancel every user. Non-admin `?all=true` → 403.
3. Response includes `cancelled` count and `scope` (`user` | `all`).
4. Web continues to call cancel without id → now cancels only that user's jobs (correct for Scenes/Adaptation Cancel).

## Suggested fix (original)

Remove cancel-all from the non-admin API, or restrict it to admin only. Default cancel without a job id to the current user's jobs only.

