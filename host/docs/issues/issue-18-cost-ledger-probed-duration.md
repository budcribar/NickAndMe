# Issue 18 — Cost ledger records requested duration not probed length

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | **fixed** |
| Branch | `fix/issue-18-cost-ledger-probed-duration` |
| Related files | `host/PageToMovie.Engine/FilmJobService.cs`; `CostReportService.cs` |

## Problem

Estimated/API-requested duration was used for both the video API call and the cost ledger. After silence trim the final file is often shorter; the sidecar was updated but cost still used the request duration.

## Fix implemented

1. **`EnsureClipDurationSidecarAsync`** returns probed seconds when available.
2. **Cost recording** uses probed duration when &gt; 0.05s; otherwise falls back to API request duration.
3. Ledger may include **`request_duration_sec`** and **`duration_source`** (`probed` / `request`) when they differ, for audit.
4. Job log notes when probed and request differ by ≥0.25s.

## Suggested fix (original)

After silence trim + sidecar write, record probed seconds in the cost event when available.
