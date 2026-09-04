@echo off
title AgentBridge Desktop UI
cd /d "%~dp0"
dotnet run --project src/AgentBridge.Desktop -c Release
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ========================================
    echo Da xay ra loi khi khoi chay ung dung.
    echo ========================================
    pause
)
