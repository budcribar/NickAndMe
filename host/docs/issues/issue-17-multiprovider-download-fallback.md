# Issue 17 — Video download falls back across providers on any error

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | open |
| Branch | `fix/issue-17-multiprovider-download-fallback` |
| Related files | host/FilmStudio.Engine/MultiProviderVideoClient.cs (~72-81) |

## Problem

Download tries Grok first and falls back to Gemini on any catch when Gemini is configured. A transient Grok failure downloading a Grok URL may incorrectly hit Gemini (wrong auth/host), obscuring the real error.

## Suggested fix

Route download by tagged request id / URL host, or only fallback on 401/403.

## Notes

Tracked from the FilmStudio.Api / Core / Engine code review (2026-07). This branch documents the problem only; implementation is follow-up work on this branch.