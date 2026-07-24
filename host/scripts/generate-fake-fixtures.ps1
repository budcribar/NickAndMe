# Generate merge-realistic and load-light MP4 fixtures for PageToMovie.Fakes
$ErrorActionPreference = "Stop"
$outDir = Join-Path $PSScriptRoot "..\PageToMovie.Fakes\Fixtures"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$ffCandidates = @(
  (Join-Path $PSScriptRoot "..\PageToMovie.Api\bin\Debug\net10.0\Resources\ffmpeg.exe"),
  (Join-Path $PSScriptRoot "..\PageToMovie.Engine\bin\Debug\net10.0\Resources\ffmpeg.exe"),
  "ffmpeg"
)
$ff = $ffCandidates | Where-Object { $_ -eq "ffmpeg" -or (Test-Path $_) } | Select-Object -First 1
if (-not $ff) { throw "ffmpeg not found" }

function New-Fixture([string]$name, [int]$seconds, [string]$vb) {
  $out = Join-Path $outDir $name
  Write-Host "Generating $out ($seconds s, vb=$vb)..."
  & $ff -y -f lavfi -i "color=c=black:s=1280x720:r=24" -f lavfi -i "anullsrc=r=44100:cl=stereo" `
    -t $seconds -c:v libx264 -pix_fmt yuv420p -b:v $vb -c:a aac -b:a 128k -shortest $out
  if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $name" }
  $kb = [math]::Round((Get-Item $out).Length / 1KB, 1)
  Write-Host "  -> $kb KB"
}

# ~4-5 MB target for NickAndMe realism: ~3.5M video bitrate * 10s ≈ 4+ MB
New-Fixture "clip_merge_10s.mp4" 10 "3500k"
New-Fixture "clip_tiny_1s.mp4" 1 "800k"
Write-Host "Done. Fixtures in $outDir"
