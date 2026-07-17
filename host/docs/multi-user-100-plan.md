# Multi-user architecture plan (≈100 concurrent users)

**Goal:** Evolve FilmStudio from single-operator / single-job to support ~**100 concurrent UI sessions**, with **per-user API keys**, **scene-level isolation**, **fair local workers**, a **load simulator** that does not burn real xAI credits, and an **admin console** (login, live server state, server configuration).

**Non-goals (v1 of this plan):** CRDT co-editing, multi-region, full SaaS billing portal, multi-admin RBAC beyond `admin` vs `user`.

---

## 1. Target capacity (definition of done)

| Metric | Target (single host, well-specced) |
|--------|-------------------------------------|
| Concurrent UI sessions (API + optional Blazor) | **100** |
| Concurrent **video gens** (global) | **8–16** (configurable) |
| Concurrent video gens **per user** | **1–2** |
| Concurrent ffmpeg (remux/WIP) | **2** |
| WIP rebuilds per project | **1** (single-flight + coalesce) |
| Scene gens same scene, two users | **Rejected** (scene lock) |
| Scene gens different scenes, same project | **Allowed** |
| API keys | **1 per user** (resolved at job start) |

**Success criteria for load tests:**

1. Simulator runs **100 virtual users** for 10+ minutes without process crash / unbounded memory growth.
2. Under gen mix (e.g. 20% genning), p95 queue wait and error rates stay within configured SLOs.
3. No cross-user file clobber (scene locks + atomic writes).
4. Fakes inject without code changes beyond DI / config.
5. Admin can log in, view **live** server state, and change capacity/fake settings without redeploy (persisted config).

---

## 2. Architecture (end state)

```
┌──────────────────────────────────────────────────────────────────────┐
│ Blazor Web (users)  │  Blazor Admin (/admin)  │  LoadSim (100 VUs)    │
└──────────┬──────────────────┬─────────────────────────┬──────────────┘
           │                  │                         │
           │  user JWT /      │  admin JWT +            │  X-User-Id
           │  X-User-Id       │  role=admin             │
           ▼                  ▼                         ▼
┌──────────────────────────────────────────────────────────────────────┐
│ FilmStudio.Api                                                         │
│  Auth (user + admin roles)                                             │
│  JobRouter → JobQueue (multi-job)                                      │
│  ApiWorkerPool / LocalWorkerPool                                       │
│  LockService · ProjectStore                                            │
│  ServerMetricsService (snapshots for admin)                            │
│  RuntimeConfigStore (capacity + fakes; hot-reloadable)                 │
│  SignalR: user:{id} · project:{id} · job:{id} · admin:ops              │
└───────────┬───────────────────┬────────────────────────────────────────┘
            │                   │
            ▼                   ▼
   IGrok* clients          IFfmpegRemux
   (real or fake)          (real or fake)
```

### 2.1 Resource split

| Work | Scheduler | Cap |
|------|-----------|-----|
| Video / image / vision / Stage1–2 LLM | **ApiWorkerPool** | Global + per-user |
| Scene remux, WIP | **LocalWorkerPool** | Global ffmpeg semaphore |
| Browse, review, play | Request threads | No job slot |

### 2.2 Locks

| Resource key | Mode |
|--------------|------|
| `project:{id}:scene:{n}` | Exclusive for gen/remux that scene |
| `project:{id}:wip` | Exclusive WIP rebuild |
| `project:{id}:stage` | Exclusive Stage1/2 |
| `project:{id}:char:{key}` | Exclusive lock/regen portrait |

Soft locks: owner, reason, expiresAt, heartbeat; steal with force flag (**admin only**).

### 2.3 Admin console (login, live ops, configuration)

Admin is a **first-class role**, not “whoever knows the API port.”

#### 2.3.1 Admin login

| Item | v1 choice |
|------|-----------|
| Role | `admin` vs `user` (claim / config) |
| Auth | Cookie or JWT after `POST /api/auth/login` |
| Credentials | Config: `FilmStudio:Admin:Username` + `PasswordHash` (or env `FILMSTUDIO_ADMIN_PASSWORD` for dev) |
| Session | Sliding expiry (e.g. 8h); logout clears cookie |
| Dev shortcut | Optional `Admin:AllowDevBypass` only in Development |
| LoadSim | **Never** uses admin credentials; stays on user headers |

**Blazor routes:**

- `/admin/login` — public login form  
- `/admin` — dashboard (authorized `admin`)  
- `/admin/config` — server configuration (authorized `admin`)  
- Nav: “Admin” only when role is admin  

**API authorization:**

- User endpoints: require authenticated user (or existing `X-User-Id` in early phases).  
- Admin endpoints: require `role=admin` (policy `AdminOnly`).  
- Mutating admin config: admin only + optional CSRF on cookie auth.

#### 2.3.2 Live server state dashboard (`/admin`)

**Purpose:** Single pane of glass while LoadSim or real users run.

**Snapshot model** `ServerStateDto` (also pushed over SignalR):

##### A. Server / ops (infrastructure)

| Section | Fields |
|---------|--------|
| **Process** | uptime, GC heap, working set, thread count, env (Dev/Prod), `UseFakes` |
| **Capacity** | MaxVideoInFlight, MaxVideoInFlightPerUser, MaxFfmpegInFlight, MaxQueuePerUser (effective values) |
| **API pool** | inFlight video/image/chat, queue depth **global**, queue depth **per user** (top N), RR cursor |
| **Local pool** | ffmpeg inFlight, WIP jobs running |
| **Jobs (live)** | running list (jobId, userId, projectId, kind, scene, clip, **age / elapsed**, progress %) |
| **Queues** | waiting jobs count by kind; **wait age** of oldest queued job per kind |
| **Locks** | active locks (resource, userId, expiresAt, reason) |
| **Projects** | open project ids with recent activity (optional) |
| **SignalR** | approximate connection count (if available) |
| **Health** | error rate window, 429 count (real or fake), capacity rejects, disk free on workspace |

##### B. User-facing duration metrics (generation under load)

These answer: *“How long is movie / scene / clip work taking right now and recently?”*  
Record timings on every **completed** job (success and fail separately where useful). Keep a **rolling window** (e.g. last 15 min and last 100 completions per kind).

| Metric kind | What we measure | Admin display |
|-------------|-----------------|---------------|
| **Clip gen** | Queue wait + API/work time + total wall time per clip job | count, p50/p95/p99 **total**, p50/p95 **queue wait**, p50/p95 **run** (submit→saved) |
| **Scene gen** | Same for a full scene job (all clips in that job), or sum of clip totals if scene is multi-clip | p50/p95 total scene wall time; in-flight scene age |
| **Scene remux** | ffmpeg scene composite only | p50/p95 duration |
| **WIP / movie rebuild** | stale remux phase + final concat (break out if possible) | p50/p95 **total WIP**; optional p50 remux-all-stale vs concat |
| **Image / Stage1 / Stage2** (optional v1.1) | same pattern by `JobKind` | compact row |

**Per completed job record (for histograms):**

```text
JobTiming {
  Kind,           // video_clip | video_scene | remux_scene | remux_wip | ...
  UserId, ProjectId, Scene?, Clip?,
  QueuedAt, StartedAt, FinishedAt,
  QueueWaitMs,    // StartedAt - QueuedAt
  RunMs,          // FinishedAt - StartedAt  (Grok or ffmpeg work)
  TotalMs,        // FinishedAt - QueuedAt   (user-perceived)
  Success, ErrorCode?
}
```

**Aggregates in `ServerStateDto.Timings` (live admin):**

| Field | Meaning |
|-------|---------|
| `byKind[kind].completedInWindow` | N finishes in rolling window |
| `byKind[kind].queueWaitMs` | p50, p95, p99, max |
| `byKind[kind].runMs` | p50, p95, p99, max |
| `byKind[kind].totalMs` | p50, p95, p99, max — **primary “how long under load”** |
| `byKind[kind].failRate` | failures / completed in window |
| `byKind[kind].inFlight` | currently running |
| `byKind[kind].oldestInFlightAgeMs` | longest current gen (stuck detector) |
| `compareIdleHint` | optional: baseline p50 from low-load window or config “expected fake delay” so admin sees **slowdown vs baseline** |

**Under load narrative (what admin should see):**

- Queue wait **rises** when `inFlight` is at cap (many users) → “generation taking longer” is often **wait**, not slower Grok.  
- Run time **rises** if API throttles (429/backoff) or machine is CPU-bound (ffmpeg).  
- Split **queue vs run** so you can tell which.

**Baselines for “slower under load”:**

1. Store rolling p50 when `globalQueueDepth == 0` and `inFlight` low → **idle baseline**.  
2. Admin panel shows e.g. `scene total p95 = 12.4 min (idle p50 8.1 min, +53%)`.  
3. LoadSim results JSON should export the same percentiles for offline compare.

**Not in live admin v1 (unless cheap):** per-user multi-day history charts (that’s log/analytics). Live = windowed stats + current jobs.

**Real-time updates:**

1. Admin SignalR group **`admin:ops`**.  
2. On connect, admin joins `admin:ops` (only if role admin).  
3. `ServerMetricsService` emits:
   - **Periodic tick** (e.g. every 1–2s) full or delta snapshot (includes timing percentiles recomputed from ring buffer)  
   - **Event-driven** pushes on job start/finish (update timings), lock acquire/release, config change, capacity reject  
4. Blazor admin page: hub client; bind tables to latest snapshot; fallback poll `GET /api/admin/state` every 5s if hub drops.

**UI layout (suggested):**

```text
┌─ Server ──────────────────┬─ Capacity ─────────────────┐
│ Up 2h · 1.2 GB · Fakes ON │ Video 3/12 · FFmpeg 1/2    │
├─ Generation latency (15m window) ──────────────────────┤
│ Clip  total p50 2.1m  p95 4.0m │ queue p95 45s │ run p95 3.5m │
│ Scene total p50 8.2m  p95 14m  │ queue p95 2.1m│ vs idle +40% │
│ WIP   total p50 12s   p95 40s  │                           │
├─ Running jobs ─────────────────────────────────────────┤
│ job… u003 Buster scene gen S04 C2  elapsed 3m12s  45%  │
├─ Queues by user ───────────────────────────────────────┤
│ u001: 2 (oldest wait 1m)  u007: 1                      │
├─ Locks ────────────────────────────────────────────────┤
│ project:Buster:scene:04  alice  gen  exp 12:04         │
└─ Recent rejects / 429s ────────────────────────────────┘
```

**Admin actions (dashboard, optional v1.1):**

- Cancel any job by id  
- Force-release lock (with confirm)  
- Pause API pool (drain) for maintenance  

#### 2.3.3 Server configuration page (`/admin/config`)

**Purpose:** Tune capacity and fake/chaos settings **at runtime** without rebuild; persist for restart.

**Editable groups:**

| Group | Settings |
|-------|----------|
| **Capacity** | MaxVideoInFlight, MaxVideoInFlightPerUser, MaxFfmpegInFlight, MaxQueuePerUser, MaxUiSessions (soft warn) |
| **Fakes** | UseFakes (may require note: “new clients only” or restart), VideoDelayMs, FailRate, RateLimitEveryN |
| **Jobs** | Default clip quantum, job TTL / cancel policy |
| **WIP** | Auto-coalesce on/off |
| **Admin** | Change admin password (separate form) |
| **Read-only** | Workspace root, version, git commit, xAI configured (bool, not key) |

**Persistence:**

- Write to `FilmStudio:RuntimeConfigPath` (e.g. `host/FilmStudio.Api/runtime-config.json`) or under workspace `.filmstudio/runtime-config.json`.  
- `IRuntimeConfigStore`: load on startup → merge over appsettings → **hot apply** to worker pools (update semaphores / caps).  
- Audit: append `admin_config_audit.jsonl` (who, when, old→new).

**API:**

| Method | Path | Auth |
|--------|------|------|
| POST | `/api/auth/login` | public |
| POST | `/api/auth/logout` | auth |
| GET | `/api/auth/me` | auth (returns roles) |
| GET | `/api/admin/state` | admin |
| GET | `/api/admin/config` | admin |
| PUT | `/api/admin/config` | admin |
| POST | `/api/admin/jobs/{id}/cancel` | admin |
| POST | `/api/admin/locks/release` | admin (optional) |

**Validation:** caps must be ≥ 1 where required; FailRate in [0,1]; reject dangerous values (e.g. MaxVideoInFlight > 100 without confirm).

**SignalR:** after config PUT, broadcast `AdminConfigChanged` on `admin:ops` and optionally bump capacity gauges for all admins.

### 2.4 Jobs

Replace singleton “one snapshot” with:

```text
JobRecord {
  JobId, UserId, ProjectId, Kind, Scene?, Clip?,
  Status, QueuePosition, CreatedAt, StartedAt, FinishedAt,
  Error?, Progress Message/Index/Total
}
```

- Multiple jobs **running** up to caps.
- List: mine / project / all (admin).
- SignalR: progress only to `job:{id}` + `user:{userId}` (+ project group optional).

### 2.5 Per-user API keys

```text
IUserApiKeyProvider.GetKeyAsync(userId) → string?
```

- Real: vault / user secrets / DB.
- LoadSim: synthetic keys `sim-user-{n}` (fakes ignore value).
- **Never** log full keys.

Video client construction: factory `IGrokVideoClientFactory.Create(apiKey)` or pass key per call.

### 2.6 Fairness (local workers, multi-key world)

With **per-user keys**, Grok fairness is mostly per-key. Still apply:

- Global `MaxVideoInFlight` (protect the machine).
- Per-user `MaxVideoInFlight`.
- Optional **round-robin dequeue among users with pending work** when assigning the next free global slot (CPU fairness).

If later you run a **shared** key mode, same RR is mandatory for Grok fairness.

### 2.7 WIP

- Per-project single-flight + coalesce (`needsAnotherWip`).
- Remux only **stale** scenes, then concat.
- Not on API pool.

---

## 3. Solution layout (new projects)

```text
host/
  FilmStudio.Core/          # models, options, interfaces (expand)
  FilmStudio.Engine/        # domain + real clients
  FilmStudio.Api/           # HTTP + SignalR host
  FilmStudio.Web/           # Blazor UI
  FilmStudio.Fakes/         # NEW: fake Grok, fake ffmpeg, in-mem locks optional
  FilmStudio.LoadSim/       # NEW: concurrent user simulator (console)
  FilmStudio.Tests/         # NEW: unit + integration (WebApplicationFactory)
  docs/multi-user-100-plan.md  # this file
```

---

## 4. Injectable abstractions (fakes)

Introduce interfaces **at the edges** that cost money or CPU. Keep domain services depending on interfaces.

### 4.1 Core interfaces (`FilmStudio.Core` or `FilmStudio.Engine/Abstractions`)

| Interface | Real | Fake behavior |
|-----------|------|----------------|
| `IGrokVideoClient` | `GrokVideoClient` | Delay N ms; write tiny valid/minimal mp4 or copy fixture; optional 429 injection |
| `IGrokImageClient` | real | Return 1×1 PNG bytes / fixture |
| `IGrokChatClient` | real | Return canned JSON for Stage1/2 shapes |
| `IGrokVisionClient` | real | Return canned plate assignments |
| `IFfmpegRemux` | `FfmpegRemuxService` | Concat by file copy/list only, or invoke real ffmpeg on fixtures |
| `IJobProgressSink` | SignalR | `NullSink` / `RecordingSink` (for tests) |
| `IUserContext` | header/JWT | `SimUserContext` / `TestUserContext` |
| `IUserApiKeyProvider` | config/DB | `DictionaryKeyProvider` |
| `ILockService` | file/`pipeline_state` | `InMemoryLockService` |
| `IClock` | `SystemClock` | `FakeClock` (optional) |
| `IRandom` | system | seeded (flaky test control) |
| `IRuntimeConfigStore` | file-backed JSON | in-memory for tests |
| `IServerMetricsService` | live counters | fixed snapshot for unit tests |
| `IAdminAuthService` | password hash + JWT/cookie | `TestAdminAuth` (always admin in test) |

### 4.2 Registration

```csharp
// appsettings / env
"FilmStudio": {
  "UseFakes": true,
  "Fakes": {
    "VideoDelayMs": 200,
    "VideoFailRate": 0.0,
    "RateLimitEveryN": 0
  },
  "Capacity": {
    "MaxVideoInFlight": 12,
    "MaxVideoInFlightPerUser": 1,
    "MaxFfmpegInFlight": 2,
    "MaxQueuePerUser": 5
  }
}
```

```csharp
if (opts.UseFakes)
{
    services.AddSingleton<IGrokVideoClient, FakeGrokVideoClient>();
    // ...
}
else
{
    services.AddHttpClient<IGrokVideoClient, GrokVideoClient>(...);
}
```

### 4.3 Fake video client contract (realism for ffmpeg merges)

**Requirement:** Fakes must produce **real, demuxable MP4s** so scene remux and WIP concat exercise the **same ffmpeg paths** as production (stream-copy success/fail, re-encode fallback, duration probe, disk I/O). Empty/random byte files are not acceptable.

**Measured baseline (NickAndMe project, exact clips only):**

| Stat | Value |
|------|--------|
| Clip count | 11 |
| **Average size** | **~4.6 MB** (≈4.5–4.6 MB) |
| Median | ~3.4 MB |
| Range | ~1.9–8.2 MB |

**Buster** averages ~1.9 MB (shorter/lighter gens). Prefer **NickAndMe-scale** fixtures when testing merge realism.

#### How fake file size / duration is determined

1. **Primary: prebuilt fixture library** (checked into `FilmStudio.Fakes/Fixtures/` or generated once via script into that folder).
2. **FakeGrokVideoClient** selects a fixture and **`File.Copy`** to `scene_XX_clip_YY.mp4` (same path layout as real gens).
3. Size = **fixture file length on disk** (deterministic).

| Fixture (suggested) | Duration | Target size | Role |
|---------------------|----------|-------------|------|
| `clip_merge_10s.mp4` | ~10s | **~4–5 MB** | **Default** — matches NickAndMe avg size + typical Grok clip length |
| `clip_merge_8s.mp4` | ~8s | ~3–4 MB | Optional variety |
| `clip_merge_15s.mp4` | ~15s | ~6–8 MB | Stress heavier concat |
| `clip_tiny_1s.mp4` | ~1s | ~100–200 KB | Optional **load-only** mode (path stress, not merge realism) |

**Encoding for merge realism (important):**

- H.264 + AAC (or same family as production) in **`.mp4`**.
- Prefer **compatible timebase / similar resolution** across fixtures so **ffmpeg `-c copy` concat succeeds** often (production remux tries copy first, then re-encode).
- Generate fixtures with one script so all clips share params, e.g.:

```text
ffmpeg -f lavfi -i color=c=black:s=1280x720:r=24 -f lavfi -i anullsrc=r=44100:cl=stereo \
  -t 10 -c:v libx264 -pix_fmt yuv420p -c:a aac -b:a 128k -shortest \
  Fixtures/clip_merge_10s.mp4
```

Tune bitrate so output lands near **~4.5 MB / 10s** (NickAndMe average), e.g. video bitrate ~3–3.5 Mbps as a starting point.

**Selection policy:**

```text
FilmStudio:Fakes:VideoMode =
  MergeRealistic  → always clip_merge_10s (or map by requested duration)  // DEFAULT for remux/WIP tests
  LoadLight       → clip_tiny_1s  // 100 VU path stress only
```

```text
SubmitGenerationAsync → requestId
PollForVideoUrlAsync   → synthetic url after delay
DownloadToFileAsync    → File.Copy(selectedFixture, outPath)
Optional: write .duration.json sidecar with fixture duration for fast UI probe
```

**Remux fake policy:** Prefer **real `FfmpegRemuxService`** even when Grok is faked, so merges are production-faithful. Only fake ffmpeg if CI has no binary (then document reduced coverage).

### 4.4 Chaos knobs (for simulator + tests)

| Knob | Purpose |
|------|---------|
| `FailRate` | Random gen failures |
| `RateLimitEveryN` | Synthetic 429 |
| `SlowUserIds` | Extra delay for some users |
| `LockConflictRate` | (test only) |

---

## 5. Implementation phases (PR-sized)

### Phase A — Foundations (no multi-user UX yet) — **IMPLEMENTED (2026-07-17)**

**A1. Abstractions + DI** ✅

- `IGrokVideoClient`, `IGrokImageClient`, `IGrokChatClient`, `IGrokVisionClient`, `IFfmpegRemux`, `IJobStore`
- Real clients implement interfaces; DI switches on `FilmStudio:UseFakes` / `FILMSTUDIO_USE_FAKES`

**A2. FilmStudio.Fakes** ✅

- `FakeGrokVideoClient` (fixture copy), image/chat/vision fakes
- Fixtures: `clip_merge_10s.mp4` (NickAndMe-scale ~8 MB), `clip_tiny_1s.mp4`
- Script: `host/scripts/generate-fake-fixtures.ps1` (or copy real clips into Fixtures/)

**A3. Job model multi-instance** ✅

- `JobRecord` + `JobStore` + registration on each job start
- **Shim:** `GET /api/jobs` → primary/latest
- `GET /api/jobs?mine=1` / `?projectId=` / `?userId=` → list
- `GET /api/jobs/{jobId}` → detail
- Still **one concurrent runner** (semaphore gate); multi-worker is Phase C

**A4. Capacity options** ✅

- `CapacityOptions` + `FakesOptions` on `FilmStudioOptions`
- Enforced at start (`MaxVideoInFlight`, `MaxQueuePerUser`)
- `GET /api/capacity`

**Exit A:** app runs with fakes; single-user UI preserved; JobStore unit tests pass.
*(Do **not** remove the `/api/jobs` shim in Phase A.)*

---

### Phase B — Identity + keys + admin login — **IMPLEMENTED (2026-07-17)**

**B1. User context** ✅

- Middleware: `JwtHeaderMiddleware` (Bearer JWT) + `X-User-Id` header (dev/sim).
- `IUserContext` / `HttpUserContext`: `UserId`, roles, `RequestApiKey` (`X-Api-Key`).
- Roles: `user` | `admin` (JWT claims + config `Auth:AdminUserIds`).

**B2. API key provider** ✅

- `ConfigUserApiKeyProvider`: map `userId → key` from `Auth:UserApiKeys` / env `USERKEY_{id}`.
- Default fallback: process `XAI_API_KEY` for local single-user.

**B3. Pass key into Grok clients** ✅

- Jobs capture key at start → `ApiKeyScope.Push` for background work.
- Real `Grok*` clients prefer `ApiKeyScope.Current` then process env.

**B4. Admin authentication** ✅

- `POST /api/auth/login` → JWT with `role=admin` (+ user).
- Password: plain `Auth:AdminPassword` / hash / env `FILMSTUDIO_ADMIN_PASSWORD`; dev `AllowDevBypass`.
- `GET /api/auth/me` → `{ userId, roles[], isAdmin, hasApiKey }`.
- `GET /api/admin/state` → process + capacity + live jobs skeleton (403 if not admin).
- Blazor: `/admin/login`, `/admin` (poll 5s), nav Admin link.

**Exit B:** two users with headers; admin can log in and hit `/api/admin/state` (even if state is partial). ✅

---

### Phase C — Locks + multi-job concurrency — **IMPLEMENTED (2026-07-17)**

**C1. `ILockService`** ✅

- `InMemoryLockService`: `TryAcquire` / `Renew` / `Release` / `Get` / `ListActive` / `ReleaseAllForJob`.
- Keys: `project:{id}:scene:{nn}`, `:wip`, `:stage`, `:char:{key}`.

**C2. Enforce locks** ✅

- Scene gen / batch / scene remux: scene lock(s).
- WIP / refresh-stale: wip lock (single-flight).
- Stage1/2 / book prepare / plate sort: stage lock.
- Character variants: char lock.
- Conflicts → `LockConflictException` → HTTP **409** (`code: lock_conflict`).

**C3. Worker pools** ✅

- **ApiWorkerPool:** `MaxVideoInFlight` global + `MaxVideoInFlightPerUser` semaphores (wait/queue).
- **LocalWorkerPool:** `MaxFfmpegInFlight` for remux/WIP.
- Job state isolated via `AsyncLocal` (multi-job safe).

**C4. SignalR groups** ✅

- On connect: `user:{userId}`; admin → `admin:ops`.
- Job progress → All (compat) + `job:{id}` + `user:{userId}`.
- `JoinJob` / `LeaveJob` hub methods.

**C5. ServerMetricsService (feed for admin)** ✅

- Counters: api/ffmpeg inFlight, capacity rejects, lock conflicts, timings ring (p50/p95).
- `GET /api/admin/state` full snapshot; `AdminMetricsPushService` → `AdminState` every 2s on `admin:ops`.
- Admin Blazor shows locks + in-flight counters.

**Exit C:** two users gen different scenes concurrently (fakes); same scene → 409; admin dashboard shows live jobs (read-only). ✅

---

### Phase D — Web UX (users) + admin console — **IMPLEMENTED (2026-07-17)**

**D1. User UX (minimum)** ✅

- Home + Scenes: **my jobs** list (`GET /api/jobs?mine=1`) + cancel by id.
- Scene lock badge on Scenes list (from enriched scenes API).
- Disable Gen / batch when locked by other user.

**D2. Admin live dashboard (`/admin`)** ✅

- Login gate → process, capacity, jobs, locks, counters.
- SignalR `AdminState` + 5s poll fallback.
- Actions: cancel job (`POST /api/admin/jobs/{id}/cancel`); force-release lock.

**D3. Admin server configuration (`/admin/config`)** ✅

- `IRuntimeConfigStore` → `.filmstudio/runtime-config.json` + audit jsonl.
- `GET/PUT /api/admin/config` hot-applies capacity/fakes; UseFakes marked restart-required.

**D4. Nav + security** ✅

- Nav shows Admin only when `Session.IsAdmin`; otherwise Admin login.
- Admin APIs check `IUserContext.IsAdmin` (403).
- Login rate-limit (`LoginRateLimiter`, 10 / 5 min → 429).

**Exit D:** human multi-user + admin can watch LoadSim live and tune caps without rebuild. ✅

---

### Phase E — LoadSim + soak (+ admin validation) — **IMPLEMENTED (2026-07-17)**

- Ship `FilmStudio.LoadSim` ✅ — console client, CLI, gates, `loadsim-results.json`.
- Manual soak: see `host/docs/loadsim-soak.md` (100×10 min procedure).
- **Admin check:** documented in soak guide (watch `/admin` during run; hot-tune capacity).

**E1. Automated LoadSim pass/fail gates (CI)** ✅

- Workflow: `.github/workflows/loadsim.yml` (PR on `host/**` + Monday schedule).
- CI profile: **25 VUs × 90s**, `mixed`, fakes, exit non-zero on gate fail.
- Artifact: `loadsim-results.json`.

**E2. Unit tests for metrics counters + duration aggregates** ✅

- `ServerMetricsTests` (timings queue vs run, locks, queue by user, counters).
- `WorkerPoolTests` (global + per-user caps).
- `LoadSimGateTests` (gate pass/fail + metrics collector).
- `RuntimeConfigStoreTests` (hot capacity apply).

**Exit E:** LoadSim shipped + CI + unit tests + soak docs. Manual 100×10 still required before calling 100-user support “done”. ✅

---

### Phase F — Remove backward-compatible `GET /api/jobs` — **IMPLEMENTED (2026-07-17)**

**Purpose:** Drop the single-job shim once every consumer speaks multi-job.

**Gates (checked):**

| # | Gate | Status |
|---|------|--------|
| 1 | Web uses list/detail; `GetJobAsync` → `?mine=1` + pick primary | ✅ |
| 2 | SignalR still broadcasts by job/user groups; hub snapshot is per-caller | ✅ |
| 3 | LoadSim does not use bare GET /api/jobs | ✅ |
| 4 | Admin uses `/api/admin/state` job lists | ✅ |
| 5 | No callers of singleton `job` wrapper on bare GET | ✅ |

**F1. Remove shim** ✅

- Bare `GET /api/jobs` → **400** with examples (`mine=1`, `projectId`, `{jobId}`).
- List only with `mine` / `projectId` / `userId`.
- `GetSnapshot()` prefers **current user** jobs (not global singleton).

**F2. Cleanup** ✅

- `FilmStudio.Api.http` + `host/README.md` updated.
- Breaking change note below.

**Changelog (breaking):**  
`GET /api/jobs` without query filters no longer returns `{ job: <primary> }`.  
Clients must call `GET /api/jobs?mine=1` (list) or `GET /api/jobs/{id}` (detail).

**Exit F:** only multi-job list/detail remain. ✅

---

## 6. FilmStudio.LoadSim (client simulator)

### 6.1 Project type

- `host/FilmStudio.LoadSim/FilmStudio.LoadSim.csproj` — **console** `net10.0`
- References: `FilmStudio.Core` (DTOs only) or raw HttpClient + SignalR.Client
- **No** dependency on Engine (client-only)

### 6.2 CLI

```text
dotnet run --project host/FilmStudio.LoadSim -- \
  --baseUrl http://127.0.0.1:5088 \
  --users 100 \
  --duration 600 \
  --scenario mixed \
  --projectPrefix sim \
  --thinkTimeMs 500 \
  --genWeight 0.15 \
  --playWeight 0.4 \
  --browseWeight 0.35 \
  --reviewWeight 0.1 \
  --maxGenPerUser 1
```

### 6.3 Virtual user (VU) model

Each VU:

```text
UserId = $"u{index:D3}"
Header X-User-Id: u001
Optional X-Api-Key: sim-u001   // if API accepts override for fakes
ProjectId = per-user project OR shared "Buster" (flag --sharedProject)
```

**Lifecycle loop** until duration elapsed:

1. Think delay (`thinkTimeMs` ± jitter)
2. Weighted random action from scenario
3. Record metrics (latency, status code, errors)

### 6.4 Scenarios

| Scenario | Actions |
|----------|---------|
| `browse` | GET health, projects, scenes, scene detail |
| `play` | GET clip/composite/wip video (first bytes / HEAD or short range) |
| `review` | POST clip pass/fail (if project allows) |
| `gen` | POST gen-scene (onlyMissing) for assigned scene set |
| `remux` | POST remux scene / WIP (low weight) |
| `mixed` | weights from CLI |

**Scene assignment:** `scene = (userIndex % sceneCount) + 1` or fixed range per user to reduce lock conflicts; optional `--forceLockCollisions` for stress.

### 6.5 SignalR (optional mode)

- `--signalr true`: connect hub, join implied user group, count progress messages.
- Measure reconnects under load.

### 6.6 Metrics output

- Console summary + `loadsim-results.json`:

```json
{
  "users": 100,
  "durationSec": 600,
  "actions": { "browse": 12000, "gen": 80, "play": 4000 },
  "http": { "p50Ms": 12, "p95Ms": 80, "p99Ms": 200, "errors": 3 },
  "jobs": { "submitted": 80, "completed": 76, "failed": 2, "rejected": 2 },
  "server": { "notes": "optional /api/capacity snapshots" }
}
```

### 6.7 Pass/fail gates (defaults)

| Gate | Default |
|------|---------|
| Error rate | &lt; 1% (excluding intentional 409 lock) |
| Process under test still healthy | GET /health 200 |
| p95 browse | &lt; 500 ms (fakes, local) |
| No runaway memory | manual / dotnet-counters in soak doc |

### 6.8 Running against fakes

```text
# Terminal 1
set FilmStudio__UseFakes=true
set FilmStudio__Capacity__MaxVideoInFlight=12
dotnet run --project host/FilmStudio.Api

# Terminal 2
dotnet run --project host/FilmStudio.LoadSim -- --users 100 --duration 300 --scenario mixed
```

**Do not** point LoadSim at production with real keys and high gen weight.

---

## 7. API additions (summary)

| Endpoint | Purpose | Auth |
|----------|---------|------|
| `GET /api/capacity` | public/limited caps (optional) | user or anon |
| `GET /api/jobs` | filter mine/project | user |
| `GET /api/jobs/{id}` | detail | user (own) or admin |
| `POST /api/jobs/gen-scene` | + require user; acquire scene lock | user |
| `POST /api/jobs/remux` | locks | user |
| `POST /api/auth/login` | admin/user login | public |
| `POST /api/auth/logout` | clear session | auth |
| `GET /api/auth/me` | userId + roles | auth |
| `GET /api/admin/state` | full live server snapshot | **admin** |
| `GET /api/admin/config` | runtime config | **admin** |
| `PUT /api/admin/config` | update capacity/fakes | **admin** |
| `POST /api/admin/jobs/{id}/cancel` | force cancel | **admin** |
| `POST /api/admin/locks/release` | force unlock | **admin** |
| Headers (sim/dev) | `X-User-Id`, optional `X-Api-Key` | — |
| SignalR group | `admin:ops` | **admin** only |

---

## 8. Testing strategy

| Layer | What |
|-------|------|
| Unit | RR scheduler, lock TTL, coalesce WIP flag, queue caps, config validation; **metrics counters** (E2: inFlight, queues, rejects, caps) |
| Integration | `WebApplicationFactory` + fakes; 2 users concurrent gen different scenes; admin login → state/config |
| Load (CI) | **LoadSim automated gates** (E1: 20–50 VUs × ~2 min, fakes, non-zero exit on fail) |
| Load (manual) | 100 VUs × 10 min soak; admin dashboard open; capture artifacts |
| Manual | 2 browsers + admin browser; real or fake keys |
| Security | Non-admin 401/403 on `/api/admin/*`; login brute-force limited |

---

## 9. Risks & mitigations

| Risk | Mitigation |
|------|------------|
| Blazor Server memory at 100 circuits | Prefer LoadSim → **API only**; measure Web separately; later WASM |
| File corruption | Scene locks + atomic file writes |
| Fake mp4 won’t play / remux fails | Ship **real H.264 MP4 fixtures** (~10s, ~4–5 MB NickAndMe-scale); use real ffmpeg for remux under fakes |
| Load test disk blowup | `LoadLight` tiny fixture only for pure concurrency; default `MergeRealistic` for merge tests |
| Global job rewrite breaks UI | Compatibility shim on `/api/jobs` |
| Real key leak in sim | Forbid gen scenario without `UseFakes` unless `--i-know-what-im-doing` |
| Admin password in repo | Env/secret only; hashed at rest; no default prod password |
| Admin SignalR spam at 1Hz × heavy snapshot | Delta payloads; only push when admins connected; cap 1–2 Hz |
| Hot config breaks running jobs | Document restart-required flags; apply caps on *next* dequeue |

---

## 10. Suggested calendar (indicative)

| Phase | Effort (order of magnitude) |
|-------|-----------------------------|
| A Foundations + fakes | 3–5 days |
| B Identity + keys + **admin login** | 2–3 days |
| C Locks + workers + **metrics feed** | 4–6 days |
| D User UX + **admin dashboard + config** | 4–5 days |
| E LoadSim + soak + admin under load | 2–3 days |
| F Remove `GET /api/jobs` single-job shim | 0.5 day (when gates pass) |

**First vertical slice (1 week goal):** A1–A3 + B1/B4 stub admin login + `GET /api/admin/state` skeleton + LoadSim browse 50 VUs.

---

## 11. Immediate next PR (start here)

1. Create `FilmStudio.Fakes` + `IGrokVideoClient` extraction + `UseFakes` switch.  
2. Create `FilmStudio.LoadSim` with **browse-only** 100 VUs (no gen).  
3. Add `GET /api/capacity` stub (static caps + process uptime).  
4. Stub **admin login** + `GET /api/admin/state` (process uptime only) + empty `/admin` page.

Then iterate multi-job + locks + live metrics + config page + gen actions in sim.

---

## 12. Decision log

| Decision | Choice |
|----------|--------|
| Primary bottleneck with per-user keys | Server workers + Blazor, not shared Grok |
| Fairness | Global max in-flight + per-user cap + optional RR among users |
| WIP | Single-flight coalesce, local pool |
| Auth v1 users | `X-User-Id` header (sim + dev); JWT/cookie later |
| Auth admin | Dedicated login; `role=admin`; cookie or JWT |
| Admin live updates | SignalR group `admin:ops` + 1–2s snapshot ticks |
| Server config | Runtime file + hot-apply caps; audit log |
| Test money | Fakes always for CI/load |
| Fake video size | NickAndMe-scale real MP4 fixtures (~4–5 MB / ~10s) for ffmpeg merge realism; tiny fixture only for light load mode |
| Work-stealing deque for WIP | Rejected; use single-flight + optional parallel stale remux |
| Legacy `GET /api/jobs` shim | Keep through multi-job migration; **Phase F** removes when Web/sim/admin fully multi-job |

---

*Document version: 2026-07-17f — Admin metrics: generation latency (clip/scene/WIP queue vs run p50/p95 under load).*
