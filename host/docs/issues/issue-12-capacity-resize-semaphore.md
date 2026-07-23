# Issue 12 — Capacity resize disposes live SemaphoreSlim under load

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | **fixed** |
| Branch | `fix/issue-12-capacity-resize-semaphore` |
| Related files | `host/FilmStudio.Engine/WorkerPools.cs` |

## Problem

Capacity resize disposed the live `SemaphoreSlim` and replaced it. In-flight work could overshoot caps briefly, and waiters on the disposed semaphore could fault with `ObjectDisposedException`. Rare (admin config change under load) but multi-user-relevant.

## Fix implemented

1. **Replace only when fully idle** (`CurrentCount == configured max`) for global API and local ffmpeg pools.
2. **Capture semaphore instances** under lock before `WaitAsync` so a concurrent resize cannot swap the reference mid-call; `Release` tolerates disposed (defensive).
3. **Per-user map**: clear entries on per-user cap change without disposing held semaphores (in-flight work keeps its old instance until release).
4. Tests: resize under load does not fault waiters.

## Suggested fix (original)

Drain-then-replace, or only allow capacity changes when `InFlight == 0`.
