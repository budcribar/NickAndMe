# Film Studio (.NET solution)

Visual Studio / `dotnet` solution: **Blazor UI + C# API/engine**, with live **SignalR** job progress.
**No Python runtime is required** for the product path under `host/`.

```text
host/
  FilmStudio.slnx          # open this in Visual Studio
  FilmStudio.Core/         # shared models + options
  FilmStudio.Engine/       # project store + Grok jobs + remux
  FilmStudio.Api/          # REST + SignalR hub (:5088)
  FilmStudio.Web/          # Blazor Server UI
```

## Architecture

| Project | Role |
|---------|------|
| **FilmStudio.Web** | Blazor UI (projects, adaptation, scenes, characters, review, cost) |
| **FilmStudio.Api** | Backend: REST + `/hubs/jobs` SignalR |
| **FilmStudio.Engine** | Native C# job runner (Stage 1/2, video, remux, book prepare) |
| **FilmStudio.Core** | DTOs / options |

## Run (two terminals)

### 1) API / engine first (set API key for real gen)

```powershell
cd C:\Users\budcr\source\repos\NickAndMe\host\FilmStudio.Api
$env:XAI_API_KEY = "your-key"   # required for Stage 1 / images / video / vision
dotnet run
# Must listen on http://127.0.0.1:5088
# GET http://127.0.0.1:5088/health
# SignalR: /hubs/jobs
```

You need **two processes**: Api **and** Web. If only Web is running, health checks fail with connection refused.

ffmpeg for remux/WIP is **bundled** on Windows via NuGet (`Soenneker.Libraries.FFmpeg` → `Resources/ffmpeg.exe`). Override with `FilmStudio:FfmpegPath` if needed.

### 2) Blazor UI

```powershell
cd C:\Users\budcr\source\repos\NickAndMe\host\FilmStudio.Web
dotnet run
# e.g. https://localhost:7206  or  http://localhost:5079
```

Web calls API at `EngineApi:BaseUrl` = `http://127.0.0.1:5088` (see `appsettings.json` + `appsettings.Development.json`).

### Visual Studio

Open `host/FilmStudio.slnx`, set **multiple startup projects**: Api + Web.

## REST (Api)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | Liveness + workspace |
| GET | `/api/projects` | List / active |
| POST | `/api/projects/{id}/activate` | Switch project |
| GET | `/api/jobs` | Job snapshot |
| POST | `/api/jobs/book-prepare` | PDF extract / vision OCR |
| POST | `/api/jobs/stage1` | Stage 1 scene bible |
| POST | `/api/jobs/stage2` | Stage 2 clip plan |
| POST | `/api/jobs/gen-scene` | Generate scene clips |
| POST | `/api/jobs/remux` | Scene remux / WIP (ffmpeg progress over SignalR) |
| POST | `/api/jobs/cancel` | Cancel |
| GET | `/api/stage2-status` | Blueprint present? |

## SignalR

Hub: `/hubs/jobs`  
Events: `JobUpdated` (JobSnapshot), `JobLog` (string)

## Config

`FilmStudio.Api/appsettings.json` → `FilmStudio:WorkspaceRoot` (empty = auto-detect repo root).

## Capability matrix (native C#)

| Feature | Status |
|---------|--------|
| PDF extract + vision OCR + page render | Yes |
| Stage 1 scene bible (Grok chat) | Yes |
| Stage 2 clip planner | Yes |
| Multi-ref video + audio prompt build | Yes |
| Character portrait gen / lock | Yes |
| FFmpeg scene remux + WIP (progress via SignalR) | Yes |
| Review / edit log / approve | Yes |
| SignalR live UI | Yes |

Repo-root `gui/`, `scripts/`, and `renderer/` may still contain historical Python tooling; they are **not** invoked by Film Studio under `host/`.
