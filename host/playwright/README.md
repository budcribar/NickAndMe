# Film Studio Playwright pilot

Drive the Blazor UI end-to-end for a cinematic short (**Poe — The Tell-Tale Heart**).

## Prerequisites

- API on `http://127.0.0.1:5088`
- Web on `http://localhost:5079`
- Node 18+

```bash
cd host/playwright
npm install
npx playwright install chromium
```

## Phase A — fakes (default)

Start API with fakes:

```powershell
cd host
$env:PageToMovie__UseFakes = "true"
$env:PageToMovie_USE_FAKES = "true"
$env:PageToMovie__WorkspaceRoot = (Resolve-Path ..).Path
dotnet run --project PageToMovie.Api --launch-profile "http (fakes)"
```

Web (other terminal):

```powershell
cd host
$env:EngineApi__BaseUrl = "http://127.0.0.1:5088"
dotnet run --project PageToMovie.Web --launch-profile http
```

Run pilot:

```powershell
cd host/playwright
$env:HEADED = "1"   # optional
npm run pilot
```

Artifacts: `host/playwright/artifacts/<timestamp>/`  
(screenshots, keyframes, job JSON, pilot.log, summary.json)

## Phase B — real Scene 1 (480p)

1. Restart API **without** fakes, with `XAI_API_KEY`.
2. Open Scenes for the same project and Gen scene 1 at **480p**, or extend the pilot later with `REAL_SCENE1=1`.

## What the pilot does

1. Create project (UI)
2. Import book text
3. Create Fountain from book + sign off
4. Extract cast
5. Build shot plan
6. Generate **scene 1** at 480p
7. Auto Review each clip + human Pass/Fail (~25% fail)
8. Admin Learning: suggest + propose + auto-approve pending rules
9. Keyframes from clip MP4s + WIP when present

UI hooks: `data-testid` on Home, Import, Screenplay, Characters, Shots, Scenes, Review, Learning, Nav.
