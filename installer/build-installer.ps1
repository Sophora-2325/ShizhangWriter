#Requires -Version 5.1
<#
.SYNOPSIS
  Build dist\installer\ShizhangWriter-Setup-<version>.exe (WPF client + backend + embedded Python).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$PythonVersion = "3.13.9",
    [switch]$SkipSmokeTest,
    [switch]$ReuseStage
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Root = Split-Path -Parent $PSScriptRoot
$Dist = Join-Path $Root "dist"
$Stage = Join-Path $Dist "stage"
$Cache = Join-Path $Dist "cache"
$OutDir = Join-Path $Dist "installer"
$Csproj = Join-Path $Root "apps\desktop-wpf\NovelGm.Desktop.csproj"
$IslUrl = "https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl"
# Allocate a temporary port so another copy of the app cannot satisfy smoke checks.
$PortProbe = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
$PortProbe.Start()
$SmokePort = $PortProbe.LocalEndpoint.Port
$PortProbe.Stop()

function Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# Verify every recursive deletion remains in its explicitly intended build/temp root.
function Remove-BuildTree([string]$Path, [string]$AllowedRoot) {
    $ResolvedRoot = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $ResolvedTarget = [IO.Path]::GetFullPath($Path)
    if (-not $ResolvedTarget.StartsWith($ResolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside $ResolvedRoot : $ResolvedTarget"
    }
    if (Test-Path -LiteralPath $ResolvedTarget) {
        if ((Get-Item -LiteralPath $ResolvedTarget -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing recursive cleanup of a link: $ResolvedTarget"
        }
        Remove-Item -LiteralPath $ResolvedTarget -Recurse -Force
    }
}

[xml]$proj = Get-Content -LiteralPath $Csproj -Raw
$Version = @($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $Version) { throw "<Version> is missing in $Csproj" }

$Iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $Iscc) { throw "Inno Setup 6 not found. Install it with: winget install --id JRSoftware.InnoSetup -e" }

$PyMinor = ($PythonVersion.Split(".")[0..1]) -join "."
$PyTag = ($PythonVersion.Split(".")[0..1]) -join ""
if (-not $ReuseStage) {
    & py "-$PyMinor" -c "import sys" 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Python $PyMinor (64-bit) is required on the build machine to resolve wheels." }
}

Write-Host "novel-gm $Version  |  Python $PythonVersion  |  ISCC $Iscc"

if (-not $ReuseStage) {
if (Test-Path -LiteralPath $Stage) { Remove-BuildTree $Stage $Dist }
New-Item -ItemType Directory -Force -Path $Stage, $Cache, $OutDir | Out-Null

Step "Publishing WPF client (self-contained, single file)"
dotnet publish $Csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $Stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
$OldLaunch = [IO.Path]::GetFullPath((Join-Path $Stage 'novel-gm.exe'))
$NewLaunch = [IO.Path]::GetFullPath((Join-Path $Stage 'ShizhangWriter.exe'))
if ([IO.Path]::GetDirectoryName($OldLaunch) -ne [IO.Path]::GetFullPath($Stage) -or
    [IO.Path]::GetDirectoryName($NewLaunch) -ne [IO.Path]::GetFullPath($Stage)) { throw 'Unsafe staged launch path' }
Move-Item -LiteralPath $OldLaunch -Destination $NewLaunch
Get-ChildItem -LiteralPath $Stage -Filter *.pdb | Remove-Item -Force

Step "Preparing embedded Python $PythonVersion"
$ZipName = "python-$PythonVersion-embed-amd64.zip"
$Zip = Join-Path $Cache $ZipName
if (-not (Test-Path -LiteralPath $Zip)) {
    Invoke-WebRequest -Uri "https://www.python.org/ftp/python/$PythonVersion/$ZipName" -OutFile $Zip -UseBasicParsing
}
$PyDir = Join-Path $Stage "runtime\python"
Expand-Archive -LiteralPath $Zip -DestinationPath $PyDir
# ..\.. is the install root, so `python -m services.core` resolves the backend package.
Set-Content -LiteralPath (Join-Path $PyDir "python$PyTag._pth") -Encoding ascii -Value @(
    "python$PyTag.zip", ".", "Lib\site-packages", "..\..", "import site"
)

Step "Installing backend dependencies"
$ReqFile = Join-Path $Cache "requirements-runtime.txt"
Get-Content -LiteralPath (Join-Path $Root "services\core\requirements.txt") |
    Where-Object { $_.Trim() -and $_ -notmatch '^\s*(#|pytest|ruff)' } |
    Set-Content -LiteralPath $ReqFile -Encoding ascii
$SitePackages = Join-Path $PyDir "Lib\site-packages"
$PipArgs = @("--disable-pip-version-check", "--no-warn-script-location", "--no-compile",
    "--target", $SitePackages, "-r", $ReqFile)
# Pin to the versions in the dev .venv (the ones the test suite ran against) when it exists.
$DevPython = Join-Path $Root ".venv\Scripts\python.exe"
if (Test-Path -LiteralPath $DevPython) {
    $Constraints = Join-Path $Cache "constraints.txt"
    & $DevPython -m pip list --format=freeze --exclude-editable --disable-pip-version-check |
        Set-Content -LiteralPath $Constraints -Encoding ascii
    if ($LASTEXITCODE -ne 0) { throw "pip freeze of .venv failed" }
    $PipArgs += @("-c", $Constraints)
    Write-Host "    pinned to versions from .venv"
}
& py "-$PyMinor" -m pip install @PipArgs
if ($LASTEXITCODE -ne 0) { throw "pip install failed" }
# Console-script launchers hard-code the build machine's Python path; the app never uses them.
$BinDir = Join-Path $SitePackages "bin"
if (Test-Path -LiteralPath $BinDir) { Remove-BuildTree $BinDir $Stage }

Step "Copying backend sources"
$SvcDst = Join-Path $Stage "services"
New-Item -ItemType Directory -Force -Path $SvcDst | Out-Null
Copy-Item -LiteralPath (Join-Path $Root "services\__init__.py") -Destination $SvcDst
robocopy (Join-Path $Root "services\core") (Join-Path $SvcDst "core") /E `
    /XD tests __pycache__ storage .pytest_cache .ruff_cache /XF *.pyc requirements.txt `
    /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
$global:LASTEXITCODE = 0
} else {
    Step "Reusing staged files after verifying version and backend sources"
    $StagedExe = Join-Path $Stage "ShizhangWriter.exe"
    if (-not (Test-Path -LiteralPath $StagedExe)) { throw "Staged client is missing" }
    if ((Get-Item -LiteralPath $StagedExe).VersionInfo.FileVersion -ne "$Version.0") {
        throw "Staged client version does not match $Version"
    }
    $SourceRoot = Join-Path $Root "services"
    Get-ChildItem -LiteralPath $SourceRoot -Recurse -File | Where-Object {
        $_.FullName -notmatch '\\(tests|__pycache__|storage|\.pytest_cache|\.ruff_cache)\\' -and
        $_.Extension -ne '.pyc' -and $_.Name -ne 'requirements.txt'
    } | ForEach-Object {
        $RelativeSource = $_.FullName.Substring($SourceRoot.Length).TrimStart('\')
        $StagedSource = Join-Path (Join-Path $Stage "services") $RelativeSource
        if (-not (Test-Path -LiteralPath $StagedSource) -or
            (Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $StagedSource).Hash) {
            throw "Staged source mismatch: $RelativeSource"
        }
    }
    $PyDir = Join-Path $Stage "runtime\python"
}

if (-not $SkipSmokeTest) {
    Step "Smoke test: importing provider SDKs with the bundled Python"
    # Provider adapters import their SDKs lazily, so /health alone would not catch a missing package.
    & (Join-Path $PyDir "python.exe") -c "import anthropic, google.genai, openai, lxml.html, httpx, keyring"
    if ($LASTEXITCODE -ne 0) { throw "Bundled Python is missing backend dependencies" }

    Step "Smoke test: starting bundled backend on port $SmokePort"
    $TmpData = Join-Path ([IO.Path]::GetTempPath()) ("novel-gm-smoke-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $TmpData | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $PyDir "python.exe"
    $psi.Arguments = "-m services.core"
    $psi.WorkingDirectory = $Stage
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables["NOVEL_GM_ROOT"] = $Stage
    $psi.EnvironmentVariables["NOVEL_GM_DATA"] = $TmpData
    $psi.EnvironmentVariables["NOVEL_GM_PORT"] = "$SmokePort"
    $proc = [System.Diagnostics.Process]::Start($psi)
    $ok = $false
    try {
        for ($i = 0; $i -lt 60 -and -not $ok; $i++) {
            Start-Sleep -Milliseconds 500
            if ($proc.HasExited) { throw "Bundled backend exited early (code $($proc.ExitCode))" }
            try {
                $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$SmokePort/health" -UseBasicParsing -TimeoutSec 2
                # Windows PowerShell can otherwise decode Chinese paths as Latin-1.
                $Health = [Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray()) | ConvertFrom-Json
                $ok = $resp.StatusCode -eq 200 -and $Health.paths.root -eq $Stage
            } catch { }
        }
        if (-not $ok) { throw "Bundled backend did not answer /health within 30 s" }
        Write-Host "    /health OK"
        foreach ($path in "/providers", "/providers/presets", "/skills", "/skills/sources") {
            $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$SmokePort$path" -UseBasicParsing -TimeoutSec 10
            if ($resp.StatusCode -ne 200) { throw "$path returned $($resp.StatusCode)" }
        }
        Write-Host "    /providers /providers/presets /skills /skills/sources OK"
        $ApiBase = "http://127.0.0.1:$SmokePort"
        $OpenApi = Invoke-RestMethod -Uri "$ApiBase/openapi.json" -TimeoutSec 10
        if ($OpenApi.info.version -ne $Version) { throw "Backend version mismatch: $($OpenApi.info.version)" }
        $ProjectBody = @{ title = "Installer smoke test"; parent_dir = $TmpData; entries = @(); characters = @(); style = @{}; threads = @() } | ConvertTo-Json -Depth 5
        $Project = Invoke-RestMethod -Uri "$ApiBase/project" -Method Post -ContentType "application/json; charset=utf-8" -Body $ProjectBody -TimeoutSec 10
        $Folders = Invoke-RestMethod -Uri "$ApiBase/project/$($Project.id)/notes/folders" -TimeoutSec 10
        if (@($Folders.folders).Count -lt 3) { throw "Bundled notes categories are missing" }
        $Note = Invoke-RestMethod -Uri "$ApiBase/project/$($Project.id)/notes" -Method Post -ContentType "application/json; charset=utf-8" -Body '{"folder":"memo","body":"standalone note"}' -TimeoutSec 10
        if ($Note.folder -ne "memo" -or $Note.chapter_no -ne 0) { throw "Bundled standalone notes are not working" }
        $Reference = Invoke-RestMethod -Uri "$ApiBase/project/$($Project.id)/notes/reference?chapter_no=1" -TimeoutSec 10
        if ($Reference.reference -or @($Reference.notes).Count -ne 0) { throw "Notes unexpectedly participate in AI context" }
        $PlanChapter = Invoke-RestMethod -Uri "$ApiBase/project/$($Project.id)/chapters" -Method Post -ContentType "application/json; charset=utf-8" -Body '{"plan":{"misc":"author plan notes"}}' -TimeoutSec 10
        if ($PlanChapter.plan.misc -ne "author plan notes") { throw "Bundled plan misc is missing" }
        $Role = Invoke-RestMethod -Uri "$ApiBase/project/$($Project.id)/characters" -Method Post -ContentType "application/json; charset=utf-8" -Body '{"id":"installer-role","name":"Smoke role","chapter_no":1}' -TimeoutSec 10
        $PlanPayload = '{"misc":"saved plan notes","present_character_ids":["installer-role"]}'
        $SavedPlan = Invoke-RestMethod -Uri "$ApiBase/project/$($Project.id)/chapter/1/plan" -Method Put -ContentType "application/json; charset=utf-8" -Body $PlanPayload -TimeoutSec 10
        if ($SavedPlan.plan.misc -ne "saved plan notes" -or @($SavedPlan.plan.present_character_ids).Count -ne 1) { throw "Bundled plan save failed" }
        $RoleRetry = Invoke-RestMethod -Uri "$ApiBase/project/$($Project.id)/characters" -Method Post -ContentType "application/json; charset=utf-8" -Body '{"id":"installer-role","name":"Smoke role","chapter_no":1}' -TimeoutSec 10
        $UpdatedProject = Invoke-RestMethod -Uri "$ApiBase/project/$($Project.id)" -TimeoutSec 10
        if (@($UpdatedProject.characters | Where-Object { $_.id -eq "installer-role" }).Count -ne 1) { throw "Bundled role retry created duplicates" }
        Write-Host "    plan misc / chapter association / stable role identity OK"
        Write-Host "    backend version $Version / standalone notes / categories / empty AI reference OK"
    } finally {
        if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null }
        if (-not $ok) {
            Write-Host ($proc.StandardOutput.ReadToEnd())
            Write-Host ($proc.StandardError.ReadToEnd())
        }
        Remove-BuildTree $TmpData ([IO.Path]::GetTempPath())
    }
    Get-ChildItem -LiteralPath $Stage -Recurse -Directory -Filter __pycache__ |
        ForEach-Object { Remove-BuildTree $_.FullName $Stage }
}

Step "Compiling installer"
$Isl = Join-Path $PSScriptRoot "ChineseSimplified.isl"
if (-not (Test-Path -LiteralPath $Isl)) {
    Invoke-WebRequest -Uri $IslUrl -OutFile $Isl -UseBasicParsing
}
& $Iscc /Q "/DAppVersion=$Version" "/DStageDir=$Stage" "/DOutputDir=$OutDir" (Join-Path $PSScriptRoot "novel-gm.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$Setup = Join-Path $OutDir "ShizhangWriter-Setup-$Version.exe"
$StageMb = (Get-ChildItem -LiteralPath $Stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ""
Write-Host ("Done: {0}" -f $Setup) -ForegroundColor Green
Write-Host ("Installer {0:N1} MB  (installed size {1:N0} MB)" -f ((Get-Item -LiteralPath $Setup).Length / 1MB), $StageMb)
