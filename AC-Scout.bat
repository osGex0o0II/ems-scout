@echo off
setlocal
set "PROJECT_DIR="
set "SCRIPT_DIR=%~dp0"

if exist "%SCRIPT_DIR%src\collect.js" set "PROJECT_DIR=%SCRIPT_DIR%"

if not defined PROJECT_DIR (
  for %%p in (
    "%SCRIPT_DIR%ems-tool"
    "%SCRIPT_DIR%..\ems-tool"
    "%SCRIPT_DIR%..\..\ems-tool"
    "%SCRIPT_DIR%..\..\..\ems-tool"
    "D:\Code\ems-tool"
    "D:\Code\Git\ems-tool"
    "D:\Code\EMS"
    "D:\Code\EMS\ems-tool"
    "%USERPROFILE%\ems-tool"
    "%USERPROFILE%\Desktop\ems-tool"
  ) do (
    if not defined PROJECT_DIR if exist "%%~p\src\collect.js" set "PROJECT_DIR=%%~fp"
  )
)

if not defined PROJECT_DIR (
  for /f "delims=" %%i in ('dir /s /b "%SCRIPT_DIR%collect.js" "D:\Code\ems-tool\src\collect.js" 2^>nul') do (
    if not defined PROJECT_DIR if exist "%%~dpi..\src\collect.js" set "PROJECT_DIR=%%~dpi.."
  )
)

if defined PROJECT_DIR (
  set "ORIG_DIR=%CD%"
  cd /d "%PROJECT_DIR%"
  goto run
)

echo [ERROR] AC-Scout: Cannot find src/collect.js
echo         Put this batch file in the ems-tool folder, or keep ems-tool at:
echo         D:\Code\ems-tool
pause
exit /b 1
:run
echo AC-Scout v1.0 - EMS collection TUI
echo [INFO] This entry only starts collection/import/audit flows.
echo [INFO] Current export path: native app Data Management - Export current filtered Excel.
node src/collect.js
