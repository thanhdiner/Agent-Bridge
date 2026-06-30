[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$SelfContained,

    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $outputRoot = Join-Path $artifactsRoot 'publish\AgentBridge'
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $outputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    $outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}

$outputRoot = [System.IO.Path]::GetFullPath($outputRoot)
$artifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot)
$artifactsRootWithSeparator = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (!$outputRoot.StartsWith($artifactsRootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay under the repository artifacts directory. Refusing to delete or publish outside: $outputRoot"
}

$gatewayOutput = Join-Path $outputRoot 'services\gateway'
$agentOutput = Join-Path $outputRoot 'services\agent'

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $gatewayOutput -Force | Out-Null
New-Item -ItemType Directory -Path $agentOutput -Force | Out-Null

$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
$commonArguments = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', $selfContainedValue,
    '--nologo'
)

function Publish-Project {
    param(
        [Parameter(Mandatory)]
        [string]$Project,

        [Parameter(Mandatory)]
        [string]$Output
    )

    Write-Host "Publishing $Project -> $Output"
    & dotnet publish $Project @commonArguments -o $Output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    Publish-Project 'src\LocalMcp.Gateway\LocalMcp.Gateway.csproj' $gatewayOutput
    Publish-Project 'src\LocalMcp.Agent.Windows\LocalMcp.Agent.Windows.csproj' $agentOutput
    Publish-Project 'src\AgentBridge.Desktop\AgentBridge.Desktop.csproj' $outputRoot
}
finally {
    Pop-Location
}

$requiredFiles = @(
    (Join-Path $outputRoot 'AgentBridge.Desktop.exe'),
    (Join-Path $gatewayOutput 'LocalMcp.Gateway.exe'),
    (Join-Path $agentOutput 'LocalMcp.Agent.Windows.exe')
)

$missingFiles = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
if ($missingFiles.Count -gt 0) {
    throw "Publish completed without required runtime files: $($missingFiles -join ', ')"
}

Write-Host ''
Write-Host 'Managed runtime bundle is ready:'
Write-Host "  $outputRoot"
Write-Host ''
Write-Host 'Launch only:'
Write-Host "  $(Join-Path $outputRoot 'AgentBridge.Desktop.exe')"
