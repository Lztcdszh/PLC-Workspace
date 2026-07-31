@echo off
setlocal
set "SCRIPT=%~dp0mouse_jiggler_gui.ps1"
powershell -ExecutionPolicy Bypass -STA -File "%SCRIPT%"
if errorlevel 1 pause
