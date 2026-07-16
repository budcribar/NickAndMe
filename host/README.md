# Film Studio host (Blazor + Python engine API)

Thin **Blazor Server** UI that talks to a small **Python HTTP API**, which calls the existing film engine. The C# app does **not** reimplement Grok, Stage 1/2, or ffmpeg.

```text
Blazor (host/FilmStudio.Web)  →  python_engine_api.py :8765  →  pipeline_api / renderer
```

## Prerequisites

- .NET 10 SDK (`dotnet --version`)
- Python venv with `requirements-review.txt` (same as Streamlit)
- Repo root as working directory for the Python API
- `XAI_API_KEY` set if you will generate video

## Run (two terminals)

### 1) Python engine API

```bash
cd /path/to/NickAndMe
source .venv/bin/activate   # WSL
# or Windows: .\.venv-win\Scripts\Activate.ps1

python host/python_engine_api.py
# http://127.0.0.1:8765/health
```

### 2) Blazor UI

```powershell
cd C:\Users\budcr\source\repos\NickAndMe\host\FilmStudio.Web
dotnet run
```

Open the URL printed by Kestrel (e.g. `https://localhost:7xxx`).

## What works in this scaffold

| Feature | Status |
|---------|--------|
| List / activate projects | Yes |
| Start scene generation | Yes (`POST /api/jobs/gen-scene`) |
| Poll job status / cancel | Yes |
| Stage 1 Adaptation UI | Still Streamlit |
| Full clip review / approve | Still Streamlit |
| SignalR live push | Not yet (poll Refresh) |

## Why this split

- **Blazor** = multi-user-ready UI path, SignalR later, Store/MAUI later against same API idea  
- **Python API** = reuses `pipeline_api` + `gen_jobs` + `renderer/engine.py`  
- **scenes.json / blueprint** stay per project under `projects/`

## Next steps (when you productize)

1. Auth + tenant-scoped projects  
2. SignalR hub proxying job events from the Python side (or .NET worker watching state)  
3. Port Scenes / Characters pages gradually  
4. Object storage for mp4s in multi-user cloud  

Streamlit can remain the power-user review console during the transition.
