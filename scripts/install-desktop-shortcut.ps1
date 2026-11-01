param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ReleaseRoot = Join-Path (Split-Path -Parent $Root) 'AppVersions\novel-gm'
$Exe = Join-Path $ReleaseRoot 'ShizhangWriter.exe'

Write-Host "novel-gm root: $Root"

if (-not $SkipPublish) {
    Write-Host "Publishing self-contained exe..."
    & (Join-Path $PSScriptRoot "publish-wpf.bat")
    if ($LASTEXITCODE -ne 0) { throw "publish-wpf.bat failed" }
}

if (-not (Test-Path $Exe)) {
    throw "Missing $Exe - run scripts\publish-wpf.bat first, or omit -SkipPublish."
}

if (-not (Test-Path -LiteralPath (Join-Path $ReleaseRoot 'runtime\python\python.exe'))) {
    throw 'The fixed Release folder is missing its bundled Python runtime.'
}

$Desktop = [Environment]::GetFolderPath("Desktop")
$LnkPath = Join-Path $Desktop 'Shizhang Writer.lnk'

$Wsh = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $LnkPath) {
    $ExistingShortcut = $Wsh.CreateShortcut($LnkPath)
    if ([IO.Path]::GetDirectoryName($ExistingShortcut.TargetPath) -ne $ReleaseRoot) { throw 'A different application already owns this desktop shortcut name.' }
}
foreach ($LegacyName in @('novel-gm.lnk', (([char]0x62fe).ToString()+[char]0x7ae0+[char]0x5199+[char]0x4f5c+'.lnk'))) {
    $LegacyPath = Join-Path $Desktop $LegacyName
    if (Test-Path -LiteralPath $LegacyPath) {
        $LegacyShortcut = $Wsh.CreateShortcut($LegacyPath)
        if ([IO.Path]::GetDirectoryName($LegacyShortcut.TargetPath) -eq $ReleaseRoot) {
            if (-not (Test-Path -LiteralPath $LnkPath)) {
                if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($LegacyPath)) -ne $Desktop -or
                    [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($LnkPath)) -ne $Desktop) { throw 'Unsafe shortcut path' }
                Move-Item -LiteralPath $LegacyPath -Destination $LnkPath
            } else {
                # Keep an existing compatible alias working without removing unrelated desktop files.
                $LegacyShortcut.TargetPath = $Exe
                $LegacyShortcut.IconLocation = ($Exe + ',0')
                $LegacyShortcut.Save()
            }
        }
    }
}
$Shortcut = $Wsh.CreateShortcut($LnkPath)
$Shortcut.TargetPath = $Exe
$Shortcut.WorkingDirectory = $ReleaseRoot
$Shortcut.Description = 'Shizhang Writer'
$Shortcut.IconLocation = ($Exe + ",0")
$Shortcut.Save()

Write-Host ""
Write-Host "Desktop shortcut created: $LnkPath"
Write-Host 'Double-click Shizhang Writer on the desktop to start. No terminal needed.'
Write-Host "Fixed Release entry: $Exe (bundled runtime; no dev .venv required)."
