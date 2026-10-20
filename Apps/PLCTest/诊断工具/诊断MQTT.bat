@echo off
chcp 936 >nul
setlocal
cd /d "%~dp0"
title PLCTesting MQTT 连接诊断
echo.
echo   正在收集 MQTT 连接诊断信息，大约需要 20 秒，请稍候...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0诊断MQTT.ps1"
echo.
echo   诊断结束。报告已保存到本机桌面。
echo   请把桌面上的 "MQTT诊断报告_*.txt" 完整发回。
echo.
pause
