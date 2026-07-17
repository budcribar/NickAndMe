# Multi-user architecture plan (≈100 concurrent users)

**Goal:** Evolve FilmStudio from single-operator / single-job to support ~**100 concurrent UI sessions**, with **per-user API keys**, **scene-level isolation**, **fair local workers**, and a **load simulator** that does not burn real xAI credits.

**Non-goals (v1 of this plan):** CRDT co-editing, multi-region, full SaaS billing portal.

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

---

## 2. Architecture (end state)

```
┌─────────────────────────────────────────────────────────────────┐
│ Clients (Blazor Web)  │  FilmStudio.LoadSim (100 VUs)           │
└───────────┬───────────────────────────┬─────────────────────────┘
            │ HTTP + SignalR            │
            ▼                           ▼
┌─────────────────────────────────────────────────────────────────┐
│ FilmStudio.Api                                                      │
│  Auth / UserContext (userId, apiKey ref)                            │
│  JobRouter → JobQueue (multi-job)                                   │
│  ApiWorkerPool (global maxInFlight)                                 │
│  LocalWorkerPool (ffmpeg)                                           │
│  LockService (scene / project / character)                          │
│  ProjectStore (files)                                               │
│  SignalR groups: user:{id}, project:{id}, job:{id}                  │
└───────────┬───────────────────┬─────────────────────────────────────┘
            │                   │
            ▼                   ▼
   IGrokVideoClient      IFfmpegRemux
   IGrokImageClient      (real or fake)
   IGrokChatClient
   (real or fake per user key)
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

Soft locks: owner, reason, expiresAt, heartbeat; steal with force flag (admin).

### 2.3 Jobs

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

### 2.4 Per-user API keys

```text
IUserApiKeyProvider.GetKeyAsync(userId) → string?
```

- Real: vault / user secrets / DB.
- LoadSim: synthetic keys `sim-user-{n}` (fakes ignore value).
- **Never** log full keys.

Video client construction: factory `IGrokVideoClientFactory.Create(apiKey)` or pass key per call.

### 2.5 Fairness (local workers, multi-key world)

With **per-user keys**, Grok fairness is mostly per-key. Still apply:

- Global `MaxVideoInFlight` (protect the machine).
- Per-user `MaxVideoInFlight`.
- Optional **round-robin dequeue among users with pending work** when assigning the next free global slot (CPU fairness).

If later you run a **shared** key mode, same RR is mandatory for Grok fairness.

### 2.6 WIP

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

### 4.3 Fake video client contract

```text
SubmitGenerationAsync(prompt, duration, ...) 
  → requestId = guid
  record call for assertions

PollForVideoUrlAsync(requestId)
  → after delay, "file://fixtures/clip_10s.mp4" or http://localhost/fixtures/...

DownloadToFileAsync(url, path)
  → File.Copy(fixture, path)  // real mp4 header so UI play works
```

**Fixture pack:** `FilmStudio.Fakes/Fixtures/clip_short.mp4` (~1–2s), `clip_10s.mp4` (optional).

### 4.4 Chaos knobs (for simulator + tests)

| Knob | Purpose |
|------|---------|
| `FailRate` | Random gen failures |
| `RateLimitEveryN` | Synthetic 429 |
| `SlowUserIds` | Extra delay for some users |
| `LockConflictRate` | (test only) |

---

## 5. Implementation phases (PR-sized)

### Phase A — Foundations (no multi-user UX yet)

**A1. Abstractions + DI**

- Extract `IGrokVideoClient`, `IGrokImageClient`, `IGrokChatClient`, `IGrokVisionClient` from concrete classes (or wrap them).
- `IFfmpegRemux` over remux methods used by jobs.
- Register real implementations as today when `UseFakes=false`.

**A2. FilmStudio.Fakes**

- Implement fakes + fixtures.
- Unit smoke: fake video produces file on disk.

**A3. Job model multi-instance**

- `JobRecord` + `IJobStore` (in-memory concurrent dictionary first).
- Replace global single `_snapshot` with:
  - `TryEnqueue`, `GetJob`, `ListJobs(userId|projectId)`, `Cancel(jobId)`.
- Keep **backward-compatible** `GET /api/jobs` → “primary” or “latest for caller”.
- Add `GET /api/jobs/{id}`, `GET /api/jobs?mine=1`.

**A4. Capacity options**

- `CapacityOptions` as above; enforce in enqueue.

**Exit A:** app runs with fakes; single-user behavior preserved; tests use fakes.

---

### Phase B — Identity + keys (stub auth OK)

**B1. User context**

- Middleware: `X-User-Id` header (dev/sim) and/or JWT later.
- `IUserContext.UserId` required for gen endpoints.

**B2. API key provider**

- `ConfigUserApiKeyProvider`: map `userId → key` from config / env `USERKEY_{id}`.
- Default fallback: process `XAI_API_KEY` for local single-user.

**B3. Pass key into Grok clients**

- Prefer `Submit...(apiKey:)` or factory per request so fakes can ignore and reals use user key.

**Exit B:** two headers `X-User-Id: alice|bob` use different keys (or fakes).

---

### Phase C — Locks + multi-job concurrency

**C1. `ILockService`**

- Persist under `pipeline_state.json` → `locks` **or** in-memory for fakes/tests.
- `TryAcquire(resource, userId, ttl)`, `Renew`, `Release`, `Get`.

**C2. Enforce locks**

- Scene gen / scene remux: require `scene` lock.
- WIP: require `wip` lock; single-flight coalesce.
- Stage1/2: `stage` lock.

**C3. Worker pools**

- **ApiWorkerPool:** up to `MaxVideoInFlight` concurrent tasks; dequeue with per-user RR among non-empty user queues; quantum = **one clip** (preferred).
- **LocalWorkerPool:** ffmpeg semaphore.

**C4. SignalR groups**

- On connect: join `user:{userId}` (from claim/header).
- Job progress → `job:{id}` and `user:{userId}`.
- Optional project broadcast for “someone remuxed”.

**Exit C:** two users gen different scenes concurrently (fakes); same scene → 409.

---

### Phase D — Web UX (minimum)

- Show **my jobs** + queue position.
- Scene lock badge on Scenes list.
- Disable Gen when locked by other user.
- Config page or About: capacity stats (`/api/capacity`).

**Exit D:** human-usable multi-user on one machine with fakes or real keys.

---

### Phase E — LoadSim + soak

- Ship `FilmStudio.LoadSim` (below).
- CI job (optional, nightly): 50 VUs × 2 min with fakes.
- Manual soak: 100 VUs × 10 min; capture metrics.

**Exit E:** documented numbers + pass/fail thresholds.

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

| Endpoint | Purpose |
|----------|---------|
| `GET /api/capacity` | inFlight, queue depths, caps |
| `GET /api/jobs` | filter mine/project |
| `GET /api/jobs/{id}` | detail |
| `POST /api/jobs/gen-scene` | + require user; acquire scene lock |
| `POST /api/jobs/remux` | locks |
| `POST /api/locks/...` | optional debug |
| Headers | `X-User-Id`, optional `X-Api-Key` (dev) |

---

## 8. Testing strategy

| Layer | What |
|-------|------|
| Unit | RR scheduler, lock TTL, coalesce WIP flag, queue caps |
| Integration | `WebApplicationFactory` + fakes; 2 users concurrent gen different scenes |
| Load | LoadSim 100 VUs fakes |
| Manual | 2 browsers, real or fake keys |

---

## 9. Risks & mitigations

| Risk | Mitigation |
|------|------------|
| Blazor Server memory at 100 circuits | Prefer LoadSim → **API only**; measure Web separately; later WASM |
| File corruption | Scene locks + atomic file writes |
| Fake mp4 won’t play | Ship real tiny fixture files |
| Global job rewrite breaks UI | Compatibility shim on `/api/jobs` |
| Real key leak in sim | Forbid gen scenario without `UseFakes` unless `--i-know-what-im-doing` |

---

## 10. Suggested calendar (indicative)

| Phase | Effort (order of magnitude) |
|-------|-----------------------------|
| A Foundations + fakes | 3–5 days |
| B Identity + keys | 1–2 days |
| C Locks + workers | 4–6 days |
| D Web UX | 2–3 days |
| E LoadSim + soak | 2–3 days |

**First vertical slice (1 week goal):** A1–A3 + B1 stub + C3 minimal multi-job with fakes + LoadSim **browse+fake gen** at 50 VUs.

---

## 11. Immediate next PR (start here)

1. Create `FilmStudio.Fakes` + `IGrokVideoClient` extraction + `UseFakes` switch.  
2. Create `FilmStudio.LoadSim` with **browse-only** 100 VUs (no gen).  
3. Add `GET /api/capacity` stub (static caps + process uptime).  

Then iterate multi-job + locks + gen actions in sim.

---

## 12. Decision log

| Decision | Choice |
|----------|--------|
| Primary bottleneck with per-user keys | Server workers + Blazor, not shared Grok |
| Fairness | Global max in-flight + per-user cap + optional RR among users |
| WIP | Single-flight coalesce, local pool |
| Auth v1 | `X-User-Id` header (sim + dev); JWT later |
| Test money | Fakes always for CI/load |
| Work-stealing deque for WIP | Rejected; use single-flight + optional parallel stale remux |

---

*Document version: 2026-07-17 — aligns with multi-user discussion (project lanes, scene locks, per-user keys, ~100 concurrent UI sessions).*
