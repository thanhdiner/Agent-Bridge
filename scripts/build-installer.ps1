[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$installerRoot = Join-Path $repositoryRoot 'artifacts\installer\AgentBridge'
$payloadRoot = Join-Path $installerRoot 'payload\AgentBridge'
$templatesRoot = Join-Path $PSScriptRoot 'installer'
$publishScript = Join-Path $PSScriptRoot 'publish-managed-runtime.ps1'

if (Test-Path -LiteralPath $installerRoot) {
    Remove-Item -LiteralPath $installerRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $installerRoot -Force | Out-Null

$selfContained = -not $FrameworkDependent.IsPresent
& $publishScript `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -SelfContained:$selfContained `
    -OutputRoot $payloadRoot

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$requiredTemplates = @(
    'Install-AgentBridge.cmd',
    'install.ps1'
)

foreach ($template in $requiredTemplates) {
    $source = Join-Path $templatesRoot $template
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Installer template is missing: $source"
    }

    Copy-Item -LiteralPath $source -Destination (Join-Path $installerRoot $template) -Force
}

$commit = 'unknown'
try {
    $commit = (& git rev-parse --short HEAD).Trim()
}
catch {
}

$version = $env:AGENTBRIDGE_VERSION
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = "0.1.0+$commit"
}

$manifest = [ordered]@{
    name = 'AgentBridge'
    version = $version
    runtime = $Runtime
    configuration = $Configuration
    selfContained = $selfContained
    commit = $commit
    buildUtc = [DateTimeOffset]::UtcNow.ToString('O')
    entryPoint = 'AgentBridge.Desktop.exe'
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $installerRoot 'manifest.json') -Encoding UTF8

$zipPath = Join-Path (Split-Path -Parent $installerRoot) "AgentBridge-$Runtime-installer.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -LiteralPath `
    (Join-Path $installerRoot 'Install-AgentBridge.cmd'), `
    (Join-Path $installerRoot 'install.ps1'), `
    (Join-Path $installerRoot 'manifest.json'), `
    (Join-Path $installerRoot 'payload') `
    -DestinationPath $zipPath `
    -Force

Write-Host ''
Write-Host 'Installer package is ready:'
Write-Host "  $installerRoot"
Write-Host ''
Write-Host 'Zip package:'
Write-Host "  $zipPath"
Write-Host ''
Write-Host 'Install command after extraction:'
Write-Host '  .\Install-AgentBridge.cmd'
