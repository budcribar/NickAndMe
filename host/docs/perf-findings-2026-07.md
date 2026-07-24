# Multi-user perf findings (2026-07)

**Status:** Optimization paused. Async I/O + read caches + capacity caps are good enough for the current multi-user bar. Remaining items are optional polish or become natural when project files move to **database tables**.

**Related:** `async-io-pass-plan.md` (what shipped), `loadsim-soak.md` (how to run soaks), `multi-user-100-plan.md` (roadmap).

---

## Exit criteria met

| Criterion | Evidence |
|-----------|----------|
| 100 VU mixed load survives | 10m soak **PASS**, **0 × 5xx** |
| Browse usable under gen/remux/play | Final browse p50 **~1–2 ms**, browse p95 **~120 ms** |
| Throughput | Steady **~1k+ actions/s** mid-run (admin UI); final ~1.1k HTTP req/s over 10m |
| Fairness | Capacity caps 4 video / 1 per user / 2 ffmpeg; intentional **409** rejects, not collapse |
| Async purity (hot path) | No `GetAwaiter().GetResult()` on request path; caches async-only |

### Canonical soak artifact

- `host/loadsim-async-mixed-100x10m.json`  
  (copy of LoadSim output: `PageToMovie.LoadSim/bin/Release/net10.0/loadsim-results.json`)

| Field | Value |
|-------|--------|
| Scenario | mixed, 100 users, 600s, LoadSimBuster |
| HTTP total | 686,375 |
| Error rate (excl. intentional 409) | ~0.40% |
| Intentional 409 | 2,498 |
| Jobs submitted / rejected | 1,838 / 2,498 |
| Browse p50 / p95 | 1 ms / 120 ms |
| HTTP p50 / p95 / p99 | 2 / 122 / 283 ms |
| Gates | all PASS |

Other useful baselines:

| File | Notes |
|------|--------|
| `loadsim-ab-caches-ON-60s.json` / `OFF-60s.json` | Browse A/B (caches) |
| `loadsim-ab-caches-ON-10m.json` / `OFF-10m.json` | Browse-only 10m A/B |
| `loadsim-async-caches-ON-60s*.json` | Post-async pure-browse CLI (noisier; prefer mixed 10m) |

---

## What we shipped (summary)

1. **Capacity caps** — multi-user fairness (browse not starved by gen).
2. **Read caches** — `ProjectReadCache` (projects list TTL, blueprint path/doc/bytes, dir index), `SceneListCache` (scene list single-flight + TTL). Toggle: `PageToMovie:EnableReadCaches`.
3. **Async I/O multipass** — browse/API/jobs prefer `*Async`; see `async-io-pass-plan.md`.
4. **GetAwaiter cleanup** — removed sync-over-async wrappers from caches and ProjectStore hot APIs; residual character/WIP helpers use **true-sync** `File.*` privates.
5. **HTTP metrics middleware** — made hot path cheap (1s ring buckets; window math only on admin `Snapshot`). File: `PageToMovie.Api/Services/HttpRequestMetrics.cs`.
6. **Optional ThreadPool pre-warm** — `PageToMovie:ThreadPool:MinWorkerThreads` (default **0**). A/B at 64 did **not** help; leave off.

---

## Profiler findings (VS Diagnostic Session)

### CPU — metrics middleware

- Early hotspot: `HttpRequestMetricsMiddleware` → `Record` (~30%+ samples under soak).
- Cause: per-request queue enqueue + **lock prune**.
- **Fixed:** lock-free increments into fixed 30×1s buckets; prune/window only in `Snapshot()`.

### File reads

Dominant under mixed soak (illustrative order of magnitude from session):

| Path | Role | Verdict |
|------|------|---------|
| `blueprint.clips.grok.json` | Many full loads, large total MB | Metadata over-read; shared cache helps browse; still bypasses on remux/plates/sync helpers |
| `assets/video/*.mp4` | Thousands of reads | **Expected** — LoadSim **play** weight |
| `pipeline_state.json` | ~5k+ opens | Review / ledger / plates — **no cache** today |
| `pipeline_config.json` | ~5k+ opens | WIP path, image model, remux — **no cache** today |

### Async activities

- Top Total Time = long-lived **Kestrel connections / SignalR** — not CPU bugs.
- Real work: `RunFfmpegAsync`, duration probe, stderr read, `OnRemuxProgressAsync` (high call count).
- Browse GETs: high count, modest avg (~14–19 ms for scenes/detail in one session).
- Admin: `GET /api/admin/state` ~100 ms × frequent poll when dashboard open (observer cost).

---

## Metrics architecture (do not confuse)

| System | Where aggregated | Purpose |
|--------|------------------|---------|
| LoadSim **actions** (browse p95, per-action table, gates, results JSON) | **Client** (`PageToMovie.LoadSim` / `MetricsCollector`) | Authoritative soak numbers |
| Live admin LoadSim charts | Client → `POST /api/loadsim/progress` → `LoadSimLiveStore` | Display only |
| Green **HTTP traffic** banner | **Server** `HttpRequestMetrics` | Coarse path-prefix RPS last 5s/30s |

Action p95 is **not** computed on the server.

---

## Explicitly deferred (another day)

Priority if perf work resumes **before** a DB migration:

| # | Item | Why |
|---|------|-----|
| 1 | **Config + state file cache** (mtime/size, invalidate on write) | Biggest remaining disk chat for small JSON |
| 2 | Route **FfmpegRemux / plates** through shared blueprint + config APIs | Removes cache bypasses |
| 3 | Throttle **remux progress** SignalR publishes | Cuts async chatter, not browse p95 |
| 4 | Slimmer / slower **admin state** poll | Observer effect during soaks |
| 5 | Classify **review 4xx** vs real errors in LoadSim gates/UI | Noise (~24% of review actions “failed” in 10m run) |

**Do not chase** unless user-facing pain:

- ThreadPool / GC knobs (already tested / Server GC on)
- Pure-browse CLI 0 ms p50
- Play mp4 I/O volume under LoadSim
- Raising video in-flight caps (risks browse)

---

## Future: files → database tables

When (if) project store moves off JSON files:

### Mapping (conceptual)

| Today (files) | Likely table / row shape |
|---------------|---------------------------|
| `pipeline_config.json` | `project_config` (project_id PK, jsonb/columns) |
| `pipeline_state.json` | `project_state` or split: `clip_reviews`, `cost_ledger`, `character_plates`, … |
| `blueprint.clips.grok.json` | `blueprints` / `scenes` / `clips` normalized or jsonb blob |
| `scenes.json` (Stage 1) | `stage1_scenes` / seeds tables |
| `project.json` | `projects` metadata |
| Dir indexes (`assets/video`) | blob store + `media_assets` rows, or keep filesystem for binaries |

### What to reuse from this work

1. **Async all the way** on request path — maps cleanly to EF/Dapper async.
2. **Read caches with explicit invalidation** — become Redis/memory cache keyed by `project_id` + version, or rely on DB + short TTL.
3. **Single-flight** (`SemaphoreSlim` / cache stampede control) — still useful for expensive aggregates (scene list).
4. **Capacity caps** — independent of storage; keep.
5. **LoadSim client-side action metrics** — keep; storage change shouldn’t move p95 aggregation to the API unless you build a real APM.

### What goes away or shrinks

- File mtime/size validation → row `updated_at` / optimistic concurrency.
- `ProjectReadCache` dir indexes for video → optional if media metadata is in DB.
- Much of the File I/O profiler pain (config/state/blueprint re-read storms).
- True-sync `File.ReadAllText` residual helpers — delete, not wrap.

### Design caution

- **Don’t** put large **mp4** bodies in SQL; keep object storage / disk, store paths + metadata.
- **Review/state** high write rate under LoadSim → prefer narrow updates (one review row) over rewrite-whole-document (current JSON RMW pattern).
- Keep **`EnableReadCaches`-style** kill switch for A/B after migration.

---

## How to re-validate later

```powershell
# From host/ — Release, fakes, caches on (default)
pwsh scripts/run-loadsim-release.ps1 -Users 100 -Duration 600
# Or VS multi-start: Api (fakes) + Load Sim profile (mixed 100×600)
```

Compare new results to `loadsim-async-mixed-100x10m.json` (browse p95, error rate, 5xx, jobs).

---

## Decision log

| Date | Decision |
|------|----------|
| 2026-07-17 | Async multipass + caches + caps validated via mixed 100×10m PASS |
| 2026-07-17 | ThreadPool min threads: optional config, **default off** (no soak win) |
| 2026-07-17 | HttpRequestMetrics hot path fixed (ring buckets) |
| 2026-07-17 | **Stop further perf work**; document findings; config/state cache deferred (may be moot after DB) |

---

*Last updated: 2026-07-17*
