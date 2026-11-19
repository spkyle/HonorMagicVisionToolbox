@echo off
cd /d "%~dp0"
if not exist "%~dp0restore_topbar.ps1" (
    echo [ERROR] Please extract the entire ZIP archive first, then run this script inside the extracted folder.
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0restore_topbar.ps1" -PauseOnExit
if errorlevel 1 (
    echo [ERROR] Uninstaller failed with exit code %errorlevel%.
    pause
    exit /b %errorlevel%
)
exit /b 0
