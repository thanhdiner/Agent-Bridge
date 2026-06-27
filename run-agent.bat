@echo off
title LocalMcp Windows Agent
powershell -NoExit -Command "dotnet run --project src/LocalMcp.Agent.Windows"
