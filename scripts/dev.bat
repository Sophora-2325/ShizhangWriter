@echo off
setlocal EnableExtensions
REM Single-terminal: API + Web via concurrently (same console, Ctrl+C stops both)
cd /d "%~dp0.."
set "ROOT=%CD%"
set "NOVEL_GM_ROOT=%ROOT%"
if not defined NOVEL_GM_DATA set "NOVEL_GM_DATA=%ROOT%\storage"

if not exist "%ROOT%\.venv\Scripts\python.exe" (
  echo Creating venv...
  py -3.13 -m venv "%ROOT%\.venv"
)
call "%ROOT%\.venv\Scripts\activate.bat"
pip install -q -r "%ROOT%\services\core\requirements.txt"

if not exist "%ROOT%\node_modules\concurrently" (
  echo Installing root npm deps...
  call npm install
)

if not exist "%ROOT%\apps\web\node_modules" (
  pushd "%ROOT%\apps\web"
  call npm install
  popd
)

echo.
echo API http://127.0.0.1:18765
echo WEB http://127.0.0.1:5173
echo Ctrl+C stops both. Or run scripts\stop.bat
echo Root: %ROOT%
echo.

call npm run dev
endlocal
