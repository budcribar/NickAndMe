# Async I/O migration plan

Goal: keep Kestrel request threads free during disk/JSON work. Multi-pass by design.

## Pass 1 (done) — browse / read path

| Layer | Change |
|-------|--------|
| `ProjectReadCache` | `*Async` APIs; `SemaphoreSlim.WaitAsync`; `File.ReadAllBytesAsync` for blueprints |
| `SceneListCache` | `GetOrBuildAsync` |
| `ProjectStore` | `ListProjectsAsync`, `ListScenesAsync`, `GetSceneDetailAsync`, `LoadBlueprint*Async`, `ActivateAsync`, … |
| API | `/api/projects`, activate, `/scenes`, scene detail → async handlers |

Sync methods remain for job workers (Pass 2). Prefer not adding new sync I/O on request paths.

## Pass 2 — job workers & writes

- `EditLogService`, `RuntimeConfigStore` persist
- `FfmpegRemuxService` remaining sync reads/writes of manifests
- `FilmJobService` / stage services: prefer async file IO where they already use `Task`
- `SaveConfig` / blueprint writes in `ProjectStore`

## Pass 3 — residual

- `CostReportService`, character/book prepare paths
- `MediaDurationProbe` sidecar read (manifest) async when on request path
- Directory enumeration stays sync (no good BCL async API; metadata-only)

## Rules of thumb

1. Request path (MapGet/MapPost handlers): **async all the way**.
2. Background jobs: async preferred; sync OK until converted.
3. Never `GetAwaiter().GetResult()` on the request path.
4. Keep `EnableReadCaches` A/B working for both sync and async.
