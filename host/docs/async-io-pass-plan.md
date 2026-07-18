# Async I/O migration plan

Goal: keep Kestrel request threads free during disk/JSON work. Multi-pass by design.

## Pass 1 (done) — browse / read path

| Layer | Change |
|-------|--------|
| `ProjectReadCache` | `*Async` APIs; `SemaphoreSlim.WaitAsync`; `File.ReadAllBytesAsync` for blueprints |
| `SceneListCache` | `GetOrBuildAsync` |
| `ProjectStore` | `ListProjectsAsync`, `ListScenesAsync`, `GetSceneDetailAsync`, `LoadBlueprint*Async`, `ActivateAsync`, … |
| API | `/api/projects`, activate, `/scenes`, scene detail → async handlers |

## Pass 2 (done) — job workers & writes

| Layer | Change |
|-------|--------|
| `EditLogService` | `LoadAsync` / `SaveAsync` / review APIs; pipeline_state async |
| `RuntimeConfigStore` | `UpdateAsync` + async persist/audit; `SemaphoreSlim` gate |
| `FfmpegRemuxService` | scene/WIP sources manifests `Write*Async`; `LoadConfigAsync` |
| `ProjectStore` | `GetConfigAsync` / `SaveConfigAsync` |
| `MediaDurationProbe` | `WriteDurationSidecarAsync` |
| API | admin config PUT, project config, edit-log, clip review, approve |

Sync wrappers remain for older job callers (`GetAwaiter().GetResult()`). Prefer `*Async` on new code.

## Pass 3 (done) — residual cost / book / stage2 / plates

| Layer | Change |
|-------|--------|
| `CostReportService` | `GetReportAsync`, `BackfillFromDiskAsync`, ledger append, state/hero/clip_jobs async |
| `BookPrepareService` | Embedded extract, manifest, page inventory, extract_meta → async |
| `CharacterBookPlateService` | scenes/blueprint/manifest reads & writes async; inventory async |
| `Stage2PlannerService` | True `async Task` plan + blueprint write; sync overload for jobs |
| API | cost GET/backfill async |

Still sync (acceptable / later polish):
- Directory enumeration (metadata-only)
- Remux composite staleness check (small JSON, not request path)
- Some `ProjectStore` character seed helpers still sync (callable via sync wrappers)
- Runtime config startup load (once)

## Rules of thumb

1. Request path (MapGet/MapPost handlers): **async all the way**.
2. Background jobs: async preferred; sync OK until converted.
3. Never `GetAwaiter().GetResult()` on the request path.
4. Keep `EnableReadCaches` A/B working for both sync and async.
