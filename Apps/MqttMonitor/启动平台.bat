@echo off
setlocal
cd /d "%~dp0"
title MQTT Monitor

rem ==== locate node.exe (do NOT rely on PATH alone) ====
set "NODE_EXE="

rem 1) whatever is on PATH
for %%P in (node.exe) do if not defined NODE_EXE set "NODE_EXE=%%~$PATH:P"

rem 2) common install locations
if not defined NODE_EXE if exist "%ProgramFiles%\nodejs\node.exe" set "NODE_EXE=%ProgramFiles%\nodejs\node.exe"
if not defined NODE_EXE if exist "%ProgramFiles(x86)%\nodejs\node.exe" set "NODE_EXE=%ProgramFiles(x86)%\nodejs\node.exe"
if not defined NODE_EXE if exist "%LOCALAPPDATA%\Programs\nodejs\node.exe" set "NODE_EXE=%LOCALAPPDATA%\Programs\nodejs\node.exe"
if not defined NODE_EXE if exist "%APPDATA%\npm\node.exe" set "NODE_EXE=%APPDATA%\npm\node.exe"

rem 3) runtime bundled with the development environment
if not defined NODE_EXE if exist "%USERPROFILE%\.workbuddy\binaries\node\versions" (
  for /d %%D in ("%USERPROFILE%\.workbuddy\binaries\node\versions\*") do (
    if exist "%%~fD\node.exe" set "NODE_EXE=%%~fD\node.exe"
  )
)

if not defined NODE_EXE goto nonode
if not exist "node_modules" goto nomodules

echo.
echo   ==============================================================
echo     MQTT Monitor  -  starting
echo     Node: %NODE_EXE%
echo   ==============================================================
echo.

"%NODE_EXE%" server.js

echo.
echo   [stopped] the server has exited.
echo   If an error message appeared above, copy that text when asking
echo   for help - it tells us exactly what went wrong.
echo.
pause
exit /b 0

:nomodules
echo.
echo   [ERROR] node_modules is missing.
echo   Open a terminal in this folder and run:  npm install
echo.
pause
exit /b 1

:nonode
echo.
echo   [ERROR] Node.js was not found on this computer.
echo.
echo   This app needs Node.js 18 or newer.
echo   1. Download it from  https://nodejs.org  and install
echo   2. Then double-click this file again
echo.
pause
exit /b 1
