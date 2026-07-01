[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent,

    [string]$ToolPath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packagingBuild = Join-Path $repositoryRoot 'packaging\build.ps1'

if (!(Test-Path -LiteralPath $packagingBuild -PathType Leaf)) {
    throw "Packaging build script is missing: $packagingBuild"
}

$arguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $packagingBuild,
    '-Configuration', $Configuration,
    '-Runtime', $Runtime
)

if ($FrameworkDependent.IsPresent) {
    $arguments += '-FrameworkDependent'
}

if (![string]::IsNullOrWhiteSpace($ToolPath)) {
    $arguments += @('-ToolPath', $ToolPath)
}

& powershell @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Packaging build failed with exit code $LASTEXITCODE."
}

$setupPath = Join-Path $repositoryRoot 'artifacts\inno\AgentBridgeSetup-win-x64.exe'
if (!(Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Expected setup executable was not created: $setupPath"
}

Write-Host ''
Write-Host 'Installer executable is ready:'
Write-Host "  $setupPath"
