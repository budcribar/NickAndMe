# Issue 9 — Spoofable identity and open spend endpoints

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | open |
| Branch | `fix/issue-9-spoofable-user-spend-gates` |
| Related files | host/FilmStudio.Api/Auth/UserContext.cs; Program.cs job start / project mutators |

## Problem

Identity defaults to spoofable X-User-Id or "local". Expensive endpoints (gen-scene, gen-batch, stage1, character variants, remux) are not JWT-gated. Admin endpoints check IsAdmin, but spend paths do not require auth. Capacity and cast gates limit accidental waste, but there is no hard spend cap — the cost ledger is observational only.

## Suggested fix

For multi-user: require JWT or a shared secret for job starts; optional per-user/project daily USD gate via CostReportService before submit. If single-operator LAN-only is intentional, document that assumption explicitly.

## Notes

Tracked from the FilmStudio.Api / Core / Engine code review (2026-07). This branch documents the problem only; implementation is follow-up work on this branch.