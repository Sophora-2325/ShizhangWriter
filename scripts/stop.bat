@echo off
cd /d "%~dp0.."
if exist "node_modules\concurrently" (
  call npm run stop
) else (
  node scripts\stop-ports.js
)
