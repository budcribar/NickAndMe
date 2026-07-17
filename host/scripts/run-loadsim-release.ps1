# Run API (fakes) + LoadSim in Release — fairer latency than Debug.
# Usage (from repo root or host/):
#   pwsh host/scripts/run-loadsim-release.ps1
#   pwsh host/scripts/run-loadsim-release.ps1 -Users 100 -Duration 90

param(
    [int]$Users = 100,
    [int]$Duration = 90,
    [string]$BaseUrl = "http://127.0.0.1:5088"
)

$ErrorActionPreference = "Stop"
$HostDir = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $HostDir "FilmStudio.Api"))) {
    $HostDir = Join-Path (Split-Path $PSScriptRoot -Parent) "host"
}

Write-Host "Building Release…" -ForegroundColor Cyan
Push-Location $HostDir
try {
    dotnet build FilmStudio.Api/FilmStudio.Api.csproj -c Release -v q
    dotnet build FilmStudio.LoadSim/FilmStudio.LoadSim.csproj -c Release -v q

    $env:FILMSTUDIO_USE_FAKES = "true"
    $env:FilmStudio__UseFakes = "true"
    $env:FilmStudio__Capacity__MaxVideoInFlight = "8"
    $env:FilmStudio__Fakes__VideoDelayMs = "50"
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:ASPNETCORE_URLS = $BaseUrl

    Write-Host "Starting Api (Release, fakes)…" -ForegroundColor Cyan
    $api = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", "FilmStudio.Api", "-c", "Release", "--no-build", "--no-launch-profile") `
        -PassThru -WindowStyle Normal

    Write-Host "Waiting for $BaseUrl/health …"
    $ok = $false
    for ($i = 1; $i -le 90; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "$BaseUrl/health" -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { $ok = $true; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ok) {
        Write-Error "API did not become healthy. Stopped."
        if ($api -and !$api.HasExited) { Stop-Process -Id $api.Id -Force }
        exit 2
    }

    Write-Host "Starting LoadSim (Release)…" -ForegroundColor Cyan
    $args = @(
        "run", "--project", "FilmStudio.LoadSim", "-c", "Release", "--no-build", "--",
        "--baseUrl", $BaseUrl,
        "--users", "$Users",
        "--duration", "$Duration",
        "--scenario", "mixed",
        "--project", "LoadSimBuster",
        "--thinkTimeMs", "200",
        "--genWeight", "0.08",
        "--browseWeight", "0.50",
        "--playWeight", "0.35",
        "--reviewWeight", "0.05",
        "--remuxWeight", "0.02",
        "--maxGenPerUser", "2",
        "--maxErrorRate", "0.02",
        "--maxBrowseP95Ms", "2000",
        "--waitForApiSec", "30",
        "--out", "loadsim-results.json"
    )
    & dotnet @args
    $code = $LASTEXITCODE

    if ($api -and !$api.HasExited) {
        Write-Host "Stopping Api…"
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
    }
    exit $code
}
finally {
    Pop-Location
}
