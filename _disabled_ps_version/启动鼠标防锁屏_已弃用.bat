@echo off
setlocal
set "PS=%~dp0mouse_jiggler_gui.ps1"

powershell -STA -ExecutionPolicy Bypass -NoProfile -File "%PS%"
if errorlevel 1 (
  echo.
  echo [ERROR] Failed to start (code %errorlevel%). See the message above.
  echo Also check mouse_jiggler_error.log in this folder.
  pause
)
