# Single-terminal fallback without npm concurrently
$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $Root
$env:NOVEL_GM_ROOT = "$Root"
if (-not $env:NOVEL_GM_DATA) { $env:NOVEL_GM_DATA = Join-Path $Root "storage" }

$py = Join-Path $Root ".venv\Scripts\python.exe"
if (-not (Test-Path $py)) {
  py -3.13 -m venv (Join-Path $Root ".venv")
  & (Join-Path $Root ".venv\Scripts\pip.exe") install -q -r (Join-Path $Root "services\core\requirements.txt")
}

$api = Start-Process -FilePath $py -ArgumentList "-m","services.core" -WorkingDirectory $Root -NoNewWindow -PassThru
$web = Start-Process -FilePath "npm" -ArgumentList "run","dev" -WorkingDirectory (Join-Path $Root "apps\web") -NoNewWindow -PassThru

Write-Host "API http://127.0.0.1:18765  WEB http://127.0.0.1:5173  (Ctrl+C to stop)"
try {
  Wait-Process -Id $api.Id, $web.Id
} finally {
  if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue }
  if (-not $web.HasExited) { Stop-Process -Id $web.Id -Force -ErrorAction SilentlyContinue }
}
