# LoadSim & soak guide (Phase E)

## Prerequisites

- API with **fakes** for any gen-heavy run (avoids xAI spend).
- Prefer **Release** for latency numbers (Debug is much slower under 100 VUs).
- Built solution: `dotnet build host/PageToMovie.slnx -c Release`
- **Sandbox project:** uses checked-in `projects/LoadSimBuster` (isolated copy of Buster).
  Gen/remux/review only touch that folder. Real `Buster` / `NickAndMe` refused unless `--allowRealProject`.
  Optional rebuild from Buster: `--prepareSandbox --refreshSandbox`.

### What is `light=1`?

Scene list/detail normally run **ffprobe** on every clip to fill actual durations (for the Scenes UI).  
Under LoadSim that becomes a huge bottleneck (browse p95 can hit 10–15s).

LoadSim calls:

```text
GET /api/projects/{id}/scenes?light=1
GET /api/projects/{id}/scenes/{n}?light=1
```

`light=1` means: **skip ffprobe** — still returns scenes, clip counts, locks, etc.; just no measured durations.  
The Blazor Scenes page does **not** use `light=1` (you still get full duration probes in the UI).

### Visual Studio → Release

1. Toolbar: configuration dropdown **Debug → Release** (solution-wide).
2. Multi-start profile **Load Sim** (Api + LoadSim + optional Web).
3. F5 / Ctrl+F5.

Or one-shot from a terminal (builds Release, starts Api fakes, runs sim):

```powershell
pwsh host/scripts/run-loadsim-release.ps1 -Users 100 -Duration 90
```

## Quick CI-style run (local)

Terminal 1:

```powershell
cd host
$env:PageToMovie_USE_FAKES = "true"
$env:PageToMovie__Capacity__MaxVideoInFlight = "4"
$env:PageToMovie__Capacity__MaxVideoInFlightPerUser = "1"
$env:PageToMovie__Capacity__MaxFfmpegInFlight = "2"
$env:PageToMovie__Fakes__VideoDelayMs = "50"
dotnet run --project PageToMovie.Api
```

Terminal 2:

```powershell
cd host
dotnet run --project PageToMovie.LoadSim -- `
  --baseUrl http://127.0.0.1:5088 `
  --users 25 `
  --duration 90 `
  --scenario mixed `
  --project LoadSimBuster `
  --sourceProject Buster `
  --out loadsim-results.json
```

`projects/LoadSimBuster` is checked into git — no copy step on normal runs.


Exit code **0** = gates pass. Results JSON is written to `--out`.

## Scenarios

| Scenario | Behavior |
|----------|----------|
| `browse` | health, projects, scenes, detail |
| `play` | range GET clip/composite |
| `gen` | POST gen-scene (onlyMissing) |
| `remux` | POST remux scene |
| `review` | POST clip review pass |
| `mixed` | weighted mix (CLI weights) |

## Gates (defaults)

| Gate | Default |
|------|---------|
| HTTP error rate (excl. intentional 409) | &lt; 1% (`--maxErrorRate`) |
| `/health` samples | all 200 |
| Browse p95 | &lt; 500 ms (`--maxBrowseP95Ms`; CI uses 800) |
| 5xx | 0 |
| Peak API in-flight vs cap | ≤ cap + 2 (when sampled) |

## Manual soak (100 users × 10 min)

**Only with fakes** unless you accept real API cost.

```powershell
# Terminal 1
# Default multi-user caps: 4 video / 1 per user / 2 ffmpeg (raise only if browse p95 stays healthy)
$env:PageToMovie_USE_FAKES = "true"
$env:PageToMovie__Capacity__MaxVideoInFlight = "4"
$env:PageToMovie__Capacity__MaxVideoInFlightPerUser = "1"
$env:PageToMovie__Capacity__MaxFfmpegInFlight = "2"
dotnet run --project PageToMovie.Api

# Terminal 2
dotnet run --project PageToMovie.LoadSim -- `
  --users 100 `
  --duration 600 `
  --scenario mixed `
  --genWeight 0.12 `
  --thinkTimeMs 400 `
  --maxGenPerUser 3 `
  --out loadsim-soak-100x10.json
```

### Admin validation during soak

1. Open Web → `/admin/login` (admin/admin in Development).
2. Confirm **jobs / locks / apiInFlight** move.
3. Open `/admin/config`, change **MaxVideoInFlight** live; observe queue / rejects.
4. Archive `loadsim-soak-100x10.json` + note capacity settings.

## Safety

- LoadSim refuses gen against non-fake API unless `--i-know-what-im-doing`.
- Prefer `--scenario browse` for pure path stress without gen.

## CI

GitHub Actions: `.github/workflows/loadsim.yml`

- Unit tests (incl. metrics + gate unit tests)
- Start API with fakes → LoadSim 25×90s mixed → upload `loadsim-results.json`
