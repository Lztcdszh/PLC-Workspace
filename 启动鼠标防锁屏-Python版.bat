@echo off
setlocal
set "SCRIPT=%~dp0mouse_jiggler_gui.py"

REM Try pythonw first (no black console window, clean GUI).
where pythonw >nul 2>nul
if %errorlevel%==0 (
  start "" "pythonw" "%SCRIPT%"
  goto :done
)

REM Fallback to python (shows a console, errors visible).
where python >nul 2>nul
if %errorlevel%==0 (
  python "%SCRIPT%"
  if errorlevel 1 pause
  goto :done
)

REM Fallback to the py launcher.
where py >nul 2>nul
if %errorlevel%==0 (
  py -3w "%SCRIPT%"
  goto :done
)

echo Python was not found. Please install Python and check "Add Python to PATH".
echo Download: https://www.python.org/downloads/
pause

:done
endlocal
