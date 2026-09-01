@echo off
setlocal
if "%~1"=="" (
  echo Usage: run-android-agent.bat ^<adb-serial^> [gateway-url]
  echo Example: run-android-agent.bat 192.168.1.50:41277 http://127.0.0.1:5227
  exit /b 2
)

set "AndroidAdb__Serial=%~1"
if not "%~2"=="" set "AndroidAdb__GatewayUrl=%~2"

dotnet run --project "%~dp0src\LocalMcp.Agent.AndroidAdb\LocalMcp.Agent.AndroidAdb.csproj"
exit /b %errorlevel%
