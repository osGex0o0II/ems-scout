@echo off
setlocal
cd /d "%~dp0"
echo [LEGACY] This starts the old Web panel, not the current WinUI native app.
echo [LEGACY] Current export path: native app Data Management - Export current filtered Excel.
if /i not "%EMS_ENABLE_LEGACY_PANEL%"=="1" (
  echo [BLOCKED] Legacy Web panel is disabled by default.
  echo [BLOCKED] To run it intentionally: set EMS_ENABLE_LEGACY_PANEL=1
  pause
  exit /b 2
)
set EMS_PANEL_PORT=17777
start "" "http://127.0.0.1:%EMS_PANEL_PORT%"
node src\panel\server.js --port=%EMS_PANEL_PORT%
pause
