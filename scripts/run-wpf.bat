@echo off
setlocal
cd /d "%~dp0.."
set "NOVEL_GM_ROOT=%CD%"
cd apps\desktop-wpf
dotnet run -c Release
endlocal
