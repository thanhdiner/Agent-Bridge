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
$publishScript = Join-Path $repositoryRoot 'scripts\publish-managed-runtime.ps1'
$payloadRoot = Join-Path $repositoryRoot 'artifacts\inno\payload\AgentBridge'
$outputRoot = Join-Path $repositoryRoot 'artifacts\inno'
$definitionPath = Join-Path $PSScriptRoot 'inno\AgentBridge.iss'

function Resolve-Tool {
    param([string]$ConfiguredPath)

    if (![string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        if (Test-Path -LiteralPath $ConfiguredPath -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($ConfiguredPath)
        }

        throw "Tool was not found at: $ConfiguredPath"
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Inno Setup 6\ISCC.exe'),
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw 'ISCC.exe was not found. Install Inno Setup 6, then rerun this script.'
}

if (!(Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
    throw "Definition is missing: $definitionPath"
}

$selfContained = -not $FrameworkDependent.IsPresent
& $publishScript `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -SelfContained:$selfContained `
    -OutputRoot $payloadRoot

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$version = $env:AGENTBRIDGE_VERSION
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = '0.1.0'
}

$tool = Resolve-Tool -ConfiguredPath $ToolPath

$previousSource = $env:AGENTBRIDGE_INNO_SOURCE
$previousOutput = $env:AGENTBRIDGE_INNO_OUTPUT
$previousVersion = $env:AGENTBRIDGE_VERSION
try {
    $env:AGENTBRIDGE_INNO_SOURCE = [System.IO.Path]::GetFullPath($payloadRoot)
    $env:AGENTBRIDGE_INNO_OUTPUT = [System.IO.Path]::GetFullPath($outputRoot)
    $env:AGENTBRIDGE_VERSION = $version

    & $tool $definitionPath
    if ($LASTEXITCODE -ne 0) {
        throw "Tool failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:AGENTBRIDGE_INNO_SOURCE = $previousSource
    $env:AGENTBRIDGE_INNO_OUTPUT = $previousOutput
    $env:AGENTBRIDGE_VERSION = $previousVersion
}

$outputFile = Join-Path $outputRoot 'AgentBridgeSetup-win-x64.exe'
if (!(Test-Path -LiteralPath $outputFile -PathType Leaf)) {
    throw "Expected output file was not created: $outputFile"
}

$setupSha256 = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant()
$baseUrl = $env:AGENTBRIDGE_DOWNLOAD_BASE_URL
if ([string]::IsNullOrWhiteSpace($baseUrl)) {
    $baseUrl = 'https://github.com/thanhdiner/Agent-Bridge/releases/latest/download'
}

$installerUrl = $baseUrl.TrimEnd('/') + '/AgentBridgeSetup-win-x64.exe'
$manifestPath = Join-Path $outputRoot 'agentbridge-update.json'
$manifest = [ordered]@{
    version = $version
    installerUrl = $installerUrl
    installerSha256 = $setupSha256
    releaseNotesUrl = 'https://github.com/thanhdiner/Agent-Bridge/releases/latest'
    mandatory = $false
    publishedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host ''
Write-Host 'Setup file is ready:'
Write-Host "  $outputFile"
Write-Host ''
Write-Host 'Update manifest is ready:'
Write-Host "  $manifestPath"
