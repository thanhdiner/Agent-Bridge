@echo off
title AgentBridge Desktop UI
cd /d "%~dp0"
dotnet run --project src/AgentBridge.Desktop -c Release
