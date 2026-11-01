#Requires -Version 5.1
[CmdletBinding()]
param([switch]$VerifyOnce, [switch]$StartupOnly)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Workspace = Split-Path -Parent $Root
$Target = Join-Path $Workspace 'AppVersions\novel-gm'
$Dist = Join-Path $Root 'dist'
$Stage = Join-Path $Dist 'release-stage'
$Backup = Join-Path $Dist 'release-backup'
$SmokeRoot = Join-Path $Dist 'release-smoke'
$Csproj = Join-Path $Root 'apps\desktop-wpf\NovelGm.Desktop.csproj'
$AppExe = 'ShizhangWriter.exe'
[xml]$Project = Get-Content -LiteralPath $Csproj -Raw
$Version = @($Project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]

function Assert-Child([string]$Path, [string]$Parent) {
    $Full = [IO.Path]::GetFullPath($Path)
    $Base = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $Full.StartsWith($Base, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe target: $Full" }
    if ((Test-Path -LiteralPath $Full) -and ((Get-Item -LiteralPath $Full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to change a linked directory: $Full"
    }
}
function Remove-BuildDirectory([string]$Path) {
    Assert-Child $Path $Dist
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}
function Copy-Tree([string]$Source, [string]$Destination) {
    & robocopy $Source $Destination /E /XD __pycache__ tests storage .pytest_cache .ruff_cache /XF *.pyc /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $Source" }
    $global:LASTEXITCODE = 0
}
function Verify-Bundle([string]$Path) {
    $Exe = Join-Path $Path $AppExe
    if ((Get-Item -LiteralPath $Exe).VersionInfo.FileVersion -ne "$Version.0") { throw 'Client version mismatch' }
    if ($StartupOnly) {
        if ((Get-Item -LiteralPath $Exe).VersionInfo.ProductName -ne 'Shizhang Writer' -or
            (Get-Item -LiteralPath $Exe).VersionInfo.FileDescription -ne 'Shizhang Writer') { throw 'Client branding mismatch' }
        & (Join-Path $Path 'runtime\python\python.exe') (Join-Path $PSScriptRoot 'verify-release.py') $Path $Version $SmokeRoot --startup-only
    } else {
        & (Join-Path $Path 'runtime\python\python.exe') (Join-Path $PSScriptRoot 'verify-release.py') $Path $Version $SmokeRoot
    }
    if ($LASTEXITCODE -ne 0) { throw "Bundle verification failed: $Path" }
}
function Verify-InstalledCopy {
    # The staged bundle already passed smoke checks; compare every installed byte instead of rerunning them.
    foreach ($SourceFile in (Get-ChildItem -LiteralPath $Stage -Recurse -File)) {
        $Relative = $SourceFile.FullName.Substring($Stage.Length + 1)
        $InstalledFile = Join-Path $Target $Relative
        Assert-Child $InstalledFile $Target
        if (-not (Test-Path -LiteralPath $InstalledFile) -or
            (Get-FileHash -LiteralPath $SourceFile.FullName).Hash -ne (Get-FileHash -LiteralPath $InstalledFile).Hash) {
            throw "Installed bundle differs from verified stage: $Relative"
        }
    }
    Write-Host 'Installed files match the verified bundle; smoke checks were not repeated.'
}
Assert-Child $Target (Join-Path $Workspace 'AppVersions')
Assert-Child $Stage $Dist
$RuntimeSource = @((Join-Path $Dist 'stage\runtime'), (Join-Path $Target 'runtime')) |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'python\python.exe') } | Select-Object -First 1
if (-not $RuntimeSource) { throw 'No bundled Python runtime. Build installer/build-installer.ps1 once first.' }
Remove-BuildDirectory $Stage
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
Write-Host "Building Release $Version; fixed application path: $Target"
& dotnet publish $Csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:NuGetAudit=false -o $Stage
if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
# Preserve the managed assembly identity used by WPF resources; only rename the native launch file.
$BuiltExe = Join-Path $Stage 'novel-gm.exe'
$NamedExe = Join-Path $Stage $AppExe
Assert-Child $BuiltExe $Stage
Assert-Child $NamedExe $Stage
Move-Item -LiteralPath $BuiltExe -Destination $NamedExe
Get-ChildItem -LiteralPath $Stage -Filter *.pdb | Remove-Item -Force
Copy-Tree $RuntimeSource (Join-Path $Stage 'runtime')
New-Item -ItemType Directory -Force -Path (Join-Path $Stage 'services') | Out-Null
Copy-Item -LiteralPath (Join-Path $Root 'services\__init__.py') -Destination (Join-Path $Stage 'services')
Copy-Tree (Join-Path $Root 'services\core') (Join-Path $Stage 'services\core')
Verify-Bundle $Stage

# Do not interrupt a running writer or replace code used by a live backend.
$Running = @(Get-Process -Name novel-gm,ShizhangWriter,python,pythonw -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($Target + '\', [StringComparison]::OrdinalIgnoreCase)
})
if ($Running.Count) { throw 'Save and close the fixed application before publishing; no process was terminated.' }
Remove-BuildDirectory $Backup
New-Item -ItemType Directory -Force -Path $Backup, $Target | Out-Null
$Parts = @($AppExe, 'novel-gm.exe', 'services', 'runtime', 'release.json')
$Installed = @()
$Moved = @()
try {
    foreach ($Part in $Parts) {
        $From = Join-Path $Target $Part; $To = Join-Path $Backup $Part
        Assert-Child $From $Target; Assert-Child $To $Backup
        if (Test-Path -LiteralPath $From) { Move-Item -LiteralPath $From -Destination $To; $Moved += $Part }
    }
    foreach ($Part in @($AppExe, 'services', 'runtime')) {
        $Installed += $Part
        Copy-Item -LiteralPath (Join-Path $Stage $Part) -Destination $Target -Recurse -Force
    }
    if ($VerifyOnce) { Verify-InstalledCopy } else { Verify-Bundle $Target }
    $Manifest = [ordered]@{ version=$Version; published_utc=[DateTime]::UtcNow.ToString('o');
        executable=(Join-Path $Target $AppExe); client_sha256=(Get-FileHash -LiteralPath (Join-Path $Target $AppExe)).Hash;
        source_root=$Root; verification='bundled runtime SDKs + isolated startup and API smoke' }
    $Installed += 'release.json'
    $Manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Target 'release.json') -Encoding UTF8
} catch {
    $Failure = $_
    foreach ($Part in $Installed) {
        $Path = Join-Path $Target $Part; Assert-Child $Path $Target
        if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    }
    foreach ($Part in $Moved) {
        $From = Join-Path $Backup $Part; $To = Join-Path $Target $Part
        Assert-Child $From $Backup; Assert-Child $To $Target
        Move-Item -LiteralPath $From -Destination $To
    }
    throw $Failure
}
Write-Host "RELEASE READY: $(Join-Path $Target $AppExe)"
Write-Host 'Desktop link: run scripts/install-desktop-shortcut.ps1 -SkipPublish'
