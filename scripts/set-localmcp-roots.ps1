<#
.SYNOPSIS
Adds or replaces LocalMcp file-access roots and restarts the Windows Agent.

.EXAMPLE
.\scripts\set-localmcp-roots.ps1 `
  -AllowedRoots "C:\","D:\","F:\" `
  -WritableRoots "D:\mcp-scratch","F:\All Project\_Đang build"

.EXAMPLE
.\scripts\set-localmcp-roots.ps1 `
  -AllowedRoots "F:\" `
  -WritableRoots "F:\scratch" `
  -Replace `
  -NoRestart
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $AllowedRoots,

    [string[]] $WritableRoots = @(),

    [switch] $Replace,

    [switch] $NoRestart,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $repoRoot "src\LocalMcp.Agent.Windows\appsettings.Development.json"
$projectPath = Join-Path $repoRoot "src\LocalMcp.Agent.Windows\LocalMcp.Agent.Windows.csproj"

function ConvertTo-NormalizedRoots {
    param(
        [AllowEmptyCollection()]
        [string[]] $Roots
    )

    $seen = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    $result = [System.Collections.Generic.List[string]]::new()

    foreach ($root in $Roots) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath($root.Trim()).Replace(
            [System.IO.Path]::AltDirectorySeparatorChar,
            [System.IO.Path]::DirectorySeparatorChar
        )

        $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
        if (-not [string]::Equals(
            $fullPath,
            $pathRoot,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            $fullPath = $fullPath.TrimEnd(
                [char[]]@(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar
                )
            )
        }

        if ($seen.Add($fullPath)) {
            $result.Add($fullPath)
        }
    }

    return ,$result.ToArray()
}

function Get-JsonArrayProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return ,@()
    }

    return ,@($property.Value)
}

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [object] $Value
    )

    if ($null -eq $Object.PSObject.Properties[$Name]) {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
    else {
        $Object.$Name = $Value
    }
}

function Test-PathWithinRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $normalizedPath = [System.IO.Path]::GetFullPath($Path).Replace(
        [System.IO.Path]::AltDirectorySeparatorChar,
        [System.IO.Path]::DirectorySeparatorChar
    )
    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).Replace(
        [System.IO.Path]::AltDirectorySeparatorChar,
        [System.IO.Path]::DirectorySeparatorChar
    )

    if ([string]::Equals(
        $normalizedPath,
        $normalizedRoot,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        return $true
    }

    $rootWithSeparator = if ([System.IO.Path]::EndsInDirectorySeparator($normalizedRoot)) {
        $normalizedRoot
    }
    else {
        $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    }

    return $normalizedPath.StartsWith(
        $rootWithSeparator,
        [System.StringComparison]::OrdinalIgnoreCase
    )
}

if (Test-Path -LiteralPath $configPath) {
    $rawConfig = Get-Content -LiteralPath $configPath -Raw
    $config = $rawConfig | ConvertFrom-Json
}
else {
    $config = [pscustomobject]@{}
}

if ($null -eq $config.PSObject.Properties["FileAccess"]) {
    $config | Add-Member -MemberType NoteProperty -Name FileAccess -Value ([pscustomobject]@{})
}

$existingAllowed = ConvertTo-NormalizedRoots -Roots (
    Get-JsonArrayProperty -Object $config.FileAccess -Name "AllowedRoots"
)
$existingWritable = ConvertTo-NormalizedRoots -Roots (
    Get-JsonArrayProperty -Object $config.FileAccess -Name "WritableRoots"
)
$requestedAllowed = ConvertTo-NormalizedRoots -Roots $AllowedRoots
$requestedWritable = ConvertTo-NormalizedRoots -Roots $WritableRoots

$effectiveAllowed = if ($Replace) {
    $requestedAllowed
}
else {
    ConvertTo-NormalizedRoots -Roots @($existingAllowed + $requestedAllowed)
}

$effectiveWritable = if ($Replace) {
    $requestedWritable
}
else {
    ConvertTo-NormalizedRoots -Roots @($existingWritable + $requestedWritable)
}

if ($effectiveAllowed.Count -eq 0) {
    throw "AllowedRoots must contain at least one valid path."
}

foreach ($writableRoot in $effectiveWritable) {
    $isAllowed = $false

    foreach ($allowedRoot in $effectiveAllowed) {
        if (Test-PathWithinRoot -Path $writableRoot -Root $allowedRoot) {
            $isAllowed = $true
            break
        }
    }

    if (-not $isAllowed) {
        throw "Writable root '$writableRoot' is outside every configured AllowedRoot."
    }
}

Set-JsonProperty -Object $config.FileAccess -Name "AllowedRoots" -Value $effectiveAllowed
Set-JsonProperty -Object $config.FileAccess -Name "WritableRoots" -Value $effectiveWritable

$json = $config | ConvertTo-Json -Depth 20
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText(
    $configPath,
    $json + [Environment]::NewLine,
    $utf8NoBom
)

$writableDisplay = if ($effectiveWritable.Count -eq 0) {
    "(none)"
}
else {
    $effectiveWritable -join "; "
}

Write-Host "Updated: $configPath"
Write-Host "AllowedRoots:  $($effectiveAllowed -join '; ')"
Write-Host "WritableRoots: $writableDisplay"

if ($NoRestart) {
    Write-Host "Agent restart skipped."
    return
}

$agentProcesses = @(
    Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -ieq "LocalMcp.Agent.Windows.exe" -or
            (
                $_.Name -ieq "dotnet.exe" -and
                -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
                $_.CommandLine -match "\brun\b" -and
                $_.CommandLine -match "LocalMcp\.Agent\.Windows"
            )
        }
)

foreach ($process in ($agentProcesses | Sort-Object @{
    Expression = {
        if ($_.Name -ieq "LocalMcp.Agent.Windows.exe") { 0 } else { 1 }
    }
})) {
    Write-Host "Stopping Agent process $($process.ProcessId) ($($process.Name))..."
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}

if ($agentProcesses.Count -gt 0) {
    Start-Sleep -Milliseconds 750
}

$shell = Get-Command pwsh -ErrorAction SilentlyContinue
if ($null -eq $shell) {
    $shell = Get-Command powershell -ErrorAction Stop
}

$escapedRepoRoot = $repoRoot.Replace("'", "''")
$escapedProjectPath = $projectPath.Replace("'", "''")
$startCommand = "`$Host.UI.RawUI.WindowTitle = 'LocalMcp Windows Agent'; Set-Location -LiteralPath '$escapedRepoRoot'; dotnet run --project '$escapedProjectPath' -c '$Configuration'"
$encodedCommand = [Convert]::ToBase64String(
    [System.Text.Encoding]::Unicode.GetBytes($startCommand)
)

$startedProcess = Start-Process `
    -FilePath $shell.Source `
    -ArgumentList @("-NoExit", "-EncodedCommand", $encodedCommand) `
    -WorkingDirectory $repoRoot `
    -PassThru

Write-Host "Agent restart launched in a new terminal (PID $($startedProcess.Id))."
