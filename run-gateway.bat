@echo off
title LocalMcp Gateway
powershell -NoExit -Command "dotnet run --project src/LocalMcp.Gateway"
