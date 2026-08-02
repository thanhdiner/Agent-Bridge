[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'AgentBridge\App'),
    [switch]$NoDesktopShortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$payloadDirectory = Join-Path $PSScriptRoot 'payload\AgentBridge'
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'

if (!(Test-Path -LiteralPath $payloadDirectory -PathType Container)) {
    throw "Installer payload is missing: $payloadDirectory"
}

$manifest = $null
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}

function Test-PathStartsWith {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Root
    )

    $normalizedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    return $normalizedPath.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $normalizedPath.StartsWith($normalizedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-AgentBridgeProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $names = @(
        'AgentBridge.Desktop.exe',
        'LocalMcp.Gateway.exe',
        'LocalMcp.Agent.Windows.exe'
    )

    Get-CimInstance Win32_Process |
        Where-Object {
            $names -contains $_.Name -and
            $_.ExecutablePath -and
            (Test-PathStartsWith -Path $_.ExecutablePath -Root $Root)
        }
}

function Stop-AgentBridgeProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $processes = @(Get-AgentBridgeProcess -Root $Root | Sort-Object ProcessId -Descending)
    foreach ($process in $processes) {
        try {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        }
        catch {
        }
    }

    if ($processes.Count -gt 0) {
        Start-Sleep -Milliseconds 800
    }
}

function New-Shortcut {
    param(
        [Parameter(Mandatory)]
        [string]$ShortcutPath,

        [Parameter(Mandatory)]
        [string]$TargetPath,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $parent = Split-Path -Parent $ShortcutPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Description = 'AgentBridge control center'
    $shortcut.Save()
}

$installDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
$appDataDirectory = Split-Path -Parent $installDirectory
$stagingDirectory = Join-Path $appDataDirectory 'App.__staging'
$previousDirectory = Join-Path $appDataDirectory 'App.__previous'
$desktopExe = Join-Path $installDirectory 'AgentBridge.Desktop.exe'

New-Item -ItemType Directory -Path $appDataDirectory -Force | Out-Null

if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}

if (Test-Path -LiteralPath $previousDirectory) {
    Remove-Item -LiteralPath $previousDirectory -Recurse -Force
}

Copy-Item -LiteralPath $payloadDirectory -Destination $stagingDirectory -Recurse -Force

Stop-AgentBridgeProcess -Root $installDirectory

if (Test-Path -LiteralPath $installDirectory) {
    Move-Item -LiteralPath $installDirectory -Destination $previousDirectory -Force
}

Move-Item -LiteralPath $stagingDirectory -Destination $installDirectory -Force

if (Test-Path -LiteralPath $previousDirectory) {
    Remove-Item -LiteralPath $previousDirectory -Recurse -Force
}

if (!(Test-Path -LiteralPath $desktopExe -PathType Leaf)) {
    throw "Installed executable is missing: $desktopExe"
}

$programsDirectory = [Environment]::GetFolderPath('Programs')
$startMenuShortcut = Join-Path $programsDirectory 'AgentBridge.lnk'
New-Shortcut -ShortcutPath $startMenuShortcut -TargetPath $desktopExe -WorkingDirectory $installDirectory

if (!$NoDesktopShortcut) {
    $desktopDirectory = [Environment]::GetFolderPath('DesktopDirectory')
    $desktopShortcut = Join-Path $desktopDirectory 'AgentBridge.lnk'
    New-Shortcut -ShortcutPath $desktopShortcut -TargetPath $desktopExe -WorkingDirectory $installDirectory
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
New-ItemProperty -Path $runKey `
    -Name 'AgentBridge Desktop' `
    -PropertyType String `
    -Value ('"{0}" --hidden' -f $desktopExe) `
    -Force | Out-Null

$legacyStartupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'AgentBridge Desktop.lnk'
if (Test-Path -LiteralPath $legacyStartupShortcut -PathType Leaf) {
    Remove-Item -LiteralPath $legacyStartupShortcut -Force
}

Write-Host "AgentBridge installed to:"
Write-Host "  $installDirectory"
Write-Host ''
Write-Host "Start Menu shortcut:"
Write-Host "  $startMenuShortcut"

if (!$NoLaunch) {
    Start-Process -FilePath $desktopExe -WorkingDirectory $installDirectory
}
