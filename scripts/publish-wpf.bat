@echo off
setlocal
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-release.ps1"
if errorlevel 1 exit /b 1
echo.
echo Published complete Release to ..\AppVersions\novel-gm\ShizhangWriter.exe
echo Tip: run scripts\install-desktop-shortcut.bat once for a desktop icon.
endlocal
