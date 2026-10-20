@echo off
chcp 936 >nul
setlocal
cd /d "%~dp0"
title PLCTesting MQTT 出站进阶诊断
echo.
echo   进阶诊断：用对照目标判断封锁发生在哪一层，约需 60 秒，请稍候...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0诊断MQTT-进阶.ps1"
echo.
echo   诊断结束。报告已保存到本机桌面（MQTT进阶诊断_*.txt）。
echo.
pause
