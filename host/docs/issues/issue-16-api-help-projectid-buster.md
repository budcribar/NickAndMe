# Issue 16 — API help hardcodes projectId=Buster

| Field | Value |
|-------|-------|
| Severity | nit |
| Status | **fixed** |
| Branch | `fix/issue-16-api-help-projectid-buster` |
| Related files | `host/PageToMovie.Api/Program.cs` |

## Problem

Error/help example hardcodes `projectId=Buster` in API surface text. North-star: product code should not use a sample title as the canonical example.

## Fix implemented

`GET /api/jobs` 400 examples use `projectId=MyStory` instead of `Buster`.
