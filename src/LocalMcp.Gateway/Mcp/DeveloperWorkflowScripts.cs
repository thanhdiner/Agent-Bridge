namespace LocalMcp.Gateway.Mcp;

internal static class DeveloperWorkflowScripts
{
    public static string BuildExtension(string path, string packageScript)
    {
        var root = Ps(path);
        var scriptName = Ps(packageScript);
        return $$"""
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = '{{root}}'
$scriptName = '{{scriptName}}'
Set-Location -LiteralPath $root

$started = [DateTimeOffset]::UtcNow
$packageJsonPath = Join-Path $root 'package.json'
if (-not (Test-Path -LiteralPath $packageJsonPath -PathType Leaf)) {
    [pscustomobject]@{
        success = $false
        error = 'package.json was not found.'
        root = $root
        packageScript = $scriptName
    } | ConvertTo-Json -Depth 8 -Compress
    exit 0
}

$package = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
$availableScripts = @($package.scripts.PSObject.Properties.Name)
if ($availableScripts -notcontains $scriptName) {
    [pscustomobject]@{
        success = $false
        error = "Package script '$scriptName' does not exist."
        root = $root
        packageScript = $scriptName
        availableScripts = $availableScripts
    } | ConvertTo-Json -Depth 8 -Compress
    exit 0
}

$manager = if (Test-Path -LiteralPath (Join-Path $root 'pnpm-lock.yaml')) {
    'pnpm'
} elseif (Test-Path -LiteralPath (Join-Path $root 'yarn.lock')) {
    'yarn'
} elseif ((Test-Path -LiteralPath (Join-Path $root 'bun.lockb')) -or (Test-Path -LiteralPath (Join-Path $root 'bun.lock'))) {
    'bun'
} else {
    'npm'
}

$command = Get-Command $manager -ErrorAction SilentlyContinue
if ($null -eq $command) {
    [pscustomobject]@{
        success = $false
        error = "Package manager '$manager' is not available in PATH."
        root = $root
        packageManager = $manager
    } | ConvertTo-Json -Depth 8 -Compress
    exit 0
}

$outputLines = New-Object System.Collections.Generic.List[string]
& $command.Source 'run' $scriptName 2>&1 | ForEach-Object { $outputLines.Add($_.ToString()) }
$exitCode = $LASTEXITCODE

$manifestCandidates = Get-ChildItem -LiteralPath $root -Filter 'manifest.json' -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/](node_modules|\.git|artifacts|bin|obj)[\\/]' } |
    Sort-Object FullName |
    Select-Object -First 20 -ExpandProperty FullName

$finished = [DateTimeOffset]::UtcNow
[pscustomobject]@{
    success = ($exitCode -eq 0)
    root = $root
    packageManager = $manager
    packageScript = $scriptName
    displayCommand = "$manager run $scriptName"
    exitCode = $exitCode
    durationMs = [int64]($finished - $started).TotalMilliseconds
    manifestPaths = @($manifestCandidates)
    output = ($outputLines -join [Environment]::NewLine)
} | ConvertTo-Json -Depth 10 -Compress
""";
    }

    public static string InspectProcessTree(string? rootPath, string? nameContains, bool includePorts, int maxResults)
    {
        var root = Ps(rootPath ?? string.Empty);
        var name = Ps(nameContains ?? string.Empty);
        var ports = includePorts ? "$true" : "$false";
        return $$"""
$ErrorActionPreference = 'Stop'
$rootFilter = '{{root}}'
$nameFilter = '{{name}}'
$includePorts = {{ports}}
$maxResults = {{maxResults}}

$all = @(Get-CimInstance Win32_Process | ForEach-Object {
    [pscustomobject]@{
        processId = [int]$_.ProcessId
        parentProcessId = [int]$_.ParentProcessId
        processName = [string]$_.Name
        executablePath = [string]$_.ExecutablePath
        commandLine = [string]$_.CommandLine
        creationDate = if ($_.CreationDate -is [datetime]) {
            ([datetime]$_.CreationDate).ToUniversalTime().ToString('o')
        } elseif ($_.CreationDate) {
            try { ([Management.ManagementDateTimeConverter]::ToDateTime([string]$_.CreationDate)).ToUniversalTime().ToString('o') } catch { $null }
        } else {
            $null
        }
    }
})

$byId = @{}
foreach ($item in $all) { $byId[$item.processId] = $item }

$selectedIds = [Collections.Generic.HashSet[int]]::new()
$hasFilter = -not [string]::IsNullOrWhiteSpace($rootFilter) -or -not [string]::IsNullOrWhiteSpace($nameFilter)
foreach ($item in $all) {
    $matchesRoot = [string]::IsNullOrWhiteSpace($rootFilter) -or
        $item.commandLine.Contains($rootFilter, [StringComparison]::OrdinalIgnoreCase) -or
        $item.executablePath.Contains($rootFilter, [StringComparison]::OrdinalIgnoreCase)
    $matchesName = [string]::IsNullOrWhiteSpace($nameFilter) -or
        $item.processName.Contains($nameFilter, [StringComparison]::OrdinalIgnoreCase) -or
        $item.commandLine.Contains($nameFilter, [StringComparison]::OrdinalIgnoreCase)
    if ((-not $hasFilter) -or ($matchesRoot -and $matchesName)) { [void]$selectedIds.Add($item.processId) }
}

# Include ancestors of matched processes.
foreach ($id in @($selectedIds)) {
    $cursor = $id
    $guard = 0
    while ($byId.ContainsKey($cursor) -and $guard -lt 64) {
        $parent = [int]$byId[$cursor].parentProcessId
        if ($parent -le 0 -or -not $byId.ContainsKey($parent)) { break }
        [void]$selectedIds.Add($parent)
        $cursor = $parent
        $guard++
    }
}

# Include descendants of matched processes.
$changed = $true
while ($changed) {
    $changed = $false
    foreach ($item in $all) {
        if ($selectedIds.Contains([int]$item.parentProcessId) -and -not $selectedIds.Contains($item.processId)) {
            [void]$selectedIds.Add($item.processId)
            $changed = $true
        }
    }
}

$portsByPid = @{}
if ($includePorts) {
    try {
        Get-NetTCPConnection -State Listen -ErrorAction Stop | ForEach-Object {
            $pidValue = [int]$_.OwningProcess
            if (-not $portsByPid.ContainsKey($pidValue)) { $portsByPid[$pidValue] = [Collections.ArrayList]::new() }
            [void]$portsByPid[$pidValue].Add([pscustomobject]@{
                address = [string]$_.LocalAddress
                port = [int]$_.LocalPort
            })
        }
    } catch { }
}

function Get-DepthAndRoot([int]$processId) {
    $depth = 0
    $rootId = $processId
    $cursor = $processId
    $guard = 0
    while ($byId.ContainsKey($cursor) -and $guard -lt 64) {
        $parent = [int]$byId[$cursor].parentProcessId
        if ($parent -le 0 -or -not $selectedIds.Contains($parent)) { break }
        $rootId = $parent
        $cursor = $parent
        $depth++
        $guard++
    }
    return [pscustomobject]@{ depth = $depth; rootProcessId = $rootId }
}

$result = @($all | Where-Object { $selectedIds.Contains([int]$_.processId) } | ForEach-Object {
    $tree = Get-DepthAndRoot ([int]$_.processId)
    [object[]]$listeningPorts = if ($portsByPid.ContainsKey([int]$_.processId)) {
        $portsByPid[[int]$_.processId].ToArray()
    } else {
        [object[]]@()
    }
    [pscustomobject]@{
        processId = $_.processId
        parentProcessId = $_.parentProcessId
        rootProcessId = $tree.rootProcessId
        depth = $tree.depth
        processName = $_.processName
        executablePath = $_.executablePath
        commandLine = $_.commandLine
        creationDate = $_.creationDate
        listeningPorts = $listeningPorts
    }
} | Sort-Object rootProcessId, depth, processId | Select-Object -First $maxResults)

[pscustomobject]@{
    count = $result.Count
    truncated = ($selectedIds.Count -gt $result.Count)
    rootFilter = if ([string]::IsNullOrWhiteSpace($rootFilter)) { $null } else { $rootFilter }
    nameFilter = if ([string]::IsNullOrWhiteSpace($nameFilter)) { $null } else { $nameFilter }
    processes = $result
} | ConvertTo-Json -Depth 12 -Compress
""";
    }

    public static string InitializeDevSessions(string path, string configRelativePath)
    {
        var root = Ps(path);
        var config = Ps(configRelativePath);
        return $$"""
$ErrorActionPreference = 'Stop'
$root = '{{root}}'
$configRelativePath = '{{config}}'
$configPath = Join-Path $root $configRelativePath
$parent = Split-Path -Parent $configPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
if (Test-Path -LiteralPath $configPath) {
    [pscustomobject]@{ created = $false; path = $configPath; reason = 'already_exists' } | ConvertTo-Json -Compress
    exit 0
}

$template = [ordered]@{
    profiles = [ordered]@{
        default = [ordered]@{
            commands = @(
                [ordered]@{
                    name = 'app'
                    command = 'npm run dev'
                    workingDirectory = '.'
                }
            )
            healthChecks = @(
                [ordered]@{
                    name = 'app'
                    url = 'http://localhost:3000'
                }
            )
        }
    }
}
[IO.File]::WriteAllText($configPath, ($template | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ created = $true; path = $configPath } | ConvertTo-Json -Compress
""";
    }

    public static string StartDevSession(string path, string configRelativePath, string profileName)
    {
        var root = Ps(path);
        var config = Ps(configRelativePath);
        var profile = Ps(profileName);
        return $$"""
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = '{{root}}'
$configPath = Join-Path $root '{{config}}'
$profileName = '{{profile}}'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "Dev session config was not found: $configPath" }

$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$profile = $config.profiles.$profileName
if ($null -eq $profile) { throw "Profile '$profileName' was not found in $configPath" }
$commands = @($profile.commands)
if ($commands.Count -eq 0) { throw "Profile '$profileName' contains no commands." }
if ($commands.Count -gt 12) { throw 'A dev session profile may contain at most 12 commands.' }
$healthChecks = @($profile.healthChecks)
if ($healthChecks.Count -gt 20) { throw 'A dev session profile may contain at most 20 health checks.' }

$sessionKey = [Guid]::NewGuid().ToString('n')
$sessionDirectory = Join-Path $root ".agentbridge\sessions\$sessionKey"
New-Item -ItemType Directory -Path $sessionDirectory -Force | Out-Null
$children = [Collections.ArrayList]::new()

foreach ($entry in $commands) {
    $name = [string]$entry.name
    $commandText = [string]$entry.command
    if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($commandText)) { throw 'Every command requires name and command.' }
    $relativeWorkingDirectory = if ([string]::IsNullOrWhiteSpace([string]$entry.workingDirectory)) { '.' } else { [string]$entry.workingDirectory }
    $workingDirectory = [IO.Path]::GetFullPath((Join-Path $root $relativeWorkingDirectory))
    if (-not $workingDirectory.StartsWith([IO.Path]::GetFullPath($root), [StringComparison]::OrdinalIgnoreCase)) { throw "Command '$name' escapes the project root." }
    if (-not (Test-Path -LiteralPath $workingDirectory -PathType Container)) { throw "Working directory for '$name' was not found: $workingDirectory" }

    $stdoutPath = Join-Path $sessionDirectory "$name.stdout.log"
    $stderrPath = Join-Path $sessionDirectory "$name.stderr.log"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($commandText))
    $process = Start-Process -FilePath 'pwsh.exe' -WorkingDirectory $workingDirectory -ArgumentList @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded
    ) -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru

    [void]$children.Add([pscustomobject]@{
        name = $name
        processId = $process.Id
        command = $commandText
        workingDirectory = $workingDirectory
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
    })
}

$metadataPath = Join-Path $sessionDirectory 'session.json'
$metadata = [ordered]@{
    sessionKey = $sessionKey
    profile = $profileName
    root = $root
    startedAt = [DateTimeOffset]::UtcNow.ToString('o')
    sessionDirectory = $sessionDirectory
    children = $children.ToArray()
    healthChecks = $healthChecks
}
[IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
$metadata | ConvertTo-Json -Depth 12 -Compress

$httpClient = [Net.Http.HttpClient]::new()
$httpClient.Timeout = [TimeSpan]::FromSeconds(3)
function Test-SessionHealthChecks {
    foreach ($check in $healthChecks) {
        $name = if ([string]::IsNullOrWhiteSpace([string]$check.name)) { 'unnamed' } else { [string]$check.name }
        if (-not [string]::IsNullOrWhiteSpace([string]$check.url)) {
            $url = [string]$check.url
            try {
                $uri = [Uri]::new($url, [UriKind]::Absolute)
                if ($uri.Scheme -notin @('http', 'https')) { throw 'Only http and https URLs are supported.' }
                $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
                try {
                    $response = $httpClient.Send($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead)
                    try {
                        [pscustomobject]@{
                            name = $name
                            type = 'http'
                            target = $url
                            healthy = [bool]$response.IsSuccessStatusCode
                            statusCode = [int]$response.StatusCode
                            error = $null
                        }
                    } finally {
                        $response.Dispose()
                    }
                } finally {
                    $request.Dispose()
                }
            } catch {
                [pscustomobject]@{
                    name = $name
                    type = 'http'
                    target = $url
                    healthy = $false
                    statusCode = $null
                    error = $_.Exception.Message
                }
            }
            continue
        }

        $port = 0
        if ([int]::TryParse([string]$check.port, [ref]$port) -and $port -ge 1 -and $port -le 65535) {
            $hostName = if ([string]::IsNullOrWhiteSpace([string]$check.host)) { '127.0.0.1' } else { [string]$check.host }
            $client = [Net.Sockets.TcpClient]::new()
            try {
                $asyncResult = $client.BeginConnect($hostName, $port, $null, $null)
                $connected = $asyncResult.AsyncWaitHandle.WaitOne(3000)
                if ($connected) { $client.EndConnect($asyncResult) }
                [pscustomobject]@{
                    name = $name
                    type = 'tcp'
                    target = "$hostName`:$port"
                    healthy = ($connected -and $client.Connected)
                    statusCode = $null
                    error = if ($connected -and $client.Connected) { $null } else { 'Connection timed out.' }
                }
            } catch {
                [pscustomobject]@{
                    name = $name
                    type = 'tcp'
                    target = "$hostName`:$port"
                    healthy = $false
                    statusCode = $null
                    error = $_.Exception.Message
                }
            } finally {
                $client.Dispose()
            }
            continue
        }

        [pscustomobject]@{
            name = $name
            type = 'invalid'
            target = $null
            healthy = $false
            statusCode = $null
            error = 'Health check requires url or a valid port.'
        }
    }
}

try {
    while ($true) {
        Start-Sleep -Seconds 2
        $states = @($children | ForEach-Object {
            $process = Get-Process -Id $_.processId -ErrorAction SilentlyContinue
            [pscustomobject]@{
                name = $_.name
                processId = $_.processId
                running = ($null -ne $process)
                exitCode = if ($null -eq $process) { try { (Get-Process -Id $_.processId -ErrorAction Stop).ExitCode } catch { $null } } else { $null }
            }
        })
        [object[]]$healthStates = @(Test-SessionHealthChecks)
        $ready = $healthStates.Count -eq 0 -or -not ($healthStates | Where-Object { -not $_.healthy })
        [pscustomobject]@{
            type = 'heartbeat'
            timestamp = [DateTimeOffset]::UtcNow.ToString('o')
            ready = $ready
            children = $states
            healthChecks = $healthStates
        } | ConvertTo-Json -Depth 10 -Compress
        if (-not ($states | Where-Object running)) { break }
    }
} finally {
    $httpClient.Dispose()
    foreach ($child in $children) {
        Stop-Process -Id $child.processId -Force -ErrorAction SilentlyContinue
    }
}
""";
    }

    public static string PrepareVisualDirectory(string path, string name)
    {
        var root = Ps(path);
        var safeName = Ps(name);
        return $$"""
$ErrorActionPreference = 'Stop'
$root = '{{root}}'
$name = '{{safeName}}'
$directory = Join-Path $root '.agentbridge\visual-regression'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
[pscustomobject]@{
    directory = $directory
    baselinePath = (Join-Path $directory "$name.baseline.png")
    currentPath = (Join-Path $directory "$name.current.png")
    diffPath = (Join-Path $directory "$name.diff.png")
} | ConvertTo-Json -Compress
""";
    }

    public static string CompareVisuals(string baselinePath, string currentPath, string diffPath, int channelThreshold)
    {
        var baseline = Ps(baselinePath);
        var current = Ps(currentPath);
        var diff = Ps(diffPath);
        return $$"""
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$baselinePath = '{{baseline}}'
$currentPath = '{{current}}'
$diffPath = '{{diff}}'
$threshold = {{channelThreshold}}
if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) { throw "Baseline image was not found: $baselinePath" }
if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf)) { throw "Current image was not found: $currentPath" }

$sourceA = [Drawing.Bitmap]::new($baselinePath)
$sourceB = [Drawing.Bitmap]::new($currentPath)
try {
    if ($sourceA.Width -ne $sourceB.Width -or $sourceA.Height -ne $sourceB.Height) {
        [pscustomobject]@{
            success = $false
            reason = 'dimension_mismatch'
            baseline = @{ width = $sourceA.Width; height = $sourceA.Height }
            current = @{ width = $sourceB.Width; height = $sourceB.Height }
            baselinePath = $baselinePath
            currentPath = $currentPath
        } | ConvertTo-Json -Depth 8 -Compress
        exit 0
    }

    $width = $sourceA.Width
    $height = $sourceA.Height
    $rect = [Drawing.Rectangle]::new(0, 0, $width, $height)
    $format = [Drawing.Imaging.PixelFormat]::Format32bppArgb
    $bitmapA = $sourceA.Clone($rect, $format)
    $bitmapB = $sourceB.Clone($rect, $format)
    $diffBitmap = [Drawing.Bitmap]::new($width, $height, $format)
    try {
        $dataA = $bitmapA.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadOnly, $format)
        $dataB = $bitmapB.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadOnly, $format)
        $dataD = $diffBitmap.LockBits($rect, [Drawing.Imaging.ImageLockMode]::WriteOnly, $format)
        try {
            $length = [Math]::Abs($dataA.Stride) * $height
            $bytesA = New-Object byte[] $length
            $bytesB = New-Object byte[] $length
            $bytesD = New-Object byte[] $length
            [Runtime.InteropServices.Marshal]::Copy($dataA.Scan0, $bytesA, 0, $length)
            [Runtime.InteropServices.Marshal]::Copy($dataB.Scan0, $bytesB, 0, $length)
            $different = [int64]0
            $totalDelta = [int64]0
            for ($i = 0; $i -lt $length; $i += 4) {
                $db = [Math]::Abs([int]$bytesA[$i] - [int]$bytesB[$i])
                $dg = [Math]::Abs([int]$bytesA[$i + 1] - [int]$bytesB[$i + 1])
                $dr = [Math]::Abs([int]$bytesA[$i + 2] - [int]$bytesB[$i + 2])
                $delta = [Math]::Max($dr, [Math]::Max($dg, $db))
                $totalDelta += $delta
                if ($delta -gt $threshold) {
                    $different++
                    $bytesD[$i] = 255
                    $bytesD[$i + 1] = 0
                    $bytesD[$i + 2] = 255
                    $bytesD[$i + 3] = 255
                } else {
                    $gray = [byte](([int]$bytesB[$i] + [int]$bytesB[$i + 1] + [int]$bytesB[$i + 2]) / 9)
                    $bytesD[$i] = $gray
                    $bytesD[$i + 1] = $gray
                    $bytesD[$i + 2] = $gray
                    $bytesD[$i + 3] = 110
                }
            }
            [Runtime.InteropServices.Marshal]::Copy($bytesD, 0, $dataD.Scan0, $length)
        } finally {
            $bitmapA.UnlockBits($dataA)
            $bitmapB.UnlockBits($dataB)
            $diffBitmap.UnlockBits($dataD)
        }

        $diffBitmap.Save($diffPath, [Drawing.Imaging.ImageFormat]::Png)
        $pixelCount = [int64]$width * [int64]$height
        [pscustomobject]@{
            success = $true
            width = $width
            height = $height
            channelThreshold = $threshold
            differentPixels = $different
            totalPixels = $pixelCount
            differenceRatio = if ($pixelCount -eq 0) { 0 } else { [double]$different / [double]$pixelCount }
            meanChannelDelta = if ($pixelCount -eq 0) { 0 } else { [double]$totalDelta / [double]$pixelCount }
            baselinePath = $baselinePath
            currentPath = $currentPath
            diffPath = $diffPath
        } | ConvertTo-Json -Depth 8 -Compress
    } finally {
        $bitmapA.Dispose()
        $bitmapB.Dispose()
        $diffBitmap.Dispose()
    }
} finally {
    $sourceA.Dispose()
    $sourceB.Dispose()
}
""";
    }

    public static string RepoCheckpoint(
        string path,
        string action,
        string? note,
        string? testSummary,
        int debounceSeconds,
        int maxEntries,
        int listCount)
    {
        var root = Ps(path);
        var actionValue = Ps(action);
        var noteValue = Ps(note ?? string.Empty);
        var tests = Ps(testSummary ?? string.Empty);
        return $$"""
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = '{{root}}'
$action = '{{actionValue}}'
$note = '{{noteValue}}'
$testSummary = '{{tests}}'
$debounceSeconds = {{debounceSeconds}}
$maxEntries = {{maxEntries}}
$listCount = {{listCount}}
Set-Location -LiteralPath $root

$directory = Join-Path $root '.agentbridge'
$file = Join-Path $directory 'checkpoints.jsonl'

function Read-Entries {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return @() }
    $entries = [Collections.ArrayList]::new()
    foreach ($line in [IO.File]::ReadAllLines($file)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { [void]$entries.Add(($line | ConvertFrom-Json)) } catch { }
    }
    return $entries.ToArray()
}

if ($action -eq 'clear') {
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    [pscustomobject]@{ cleared = $true; path = $file } | ConvertTo-Json -Compress
    exit 0
}

$entries = @(Read-Entries)
if ($action -eq 'latest') {
    [pscustomobject]@{
        path = $file
        checkpoint = if ($entries.Count -gt 0) { $entries[-1] } else { $null }
    } | ConvertTo-Json -Depth 12 -Compress
    exit 0
}
if ($action -eq 'list') {
    [pscustomobject]@{
        path = $file
        count = [Math]::Min($entries.Count, $listCount)
        checkpoints = @($entries | Select-Object -Last $listCount)
    } | ConvertTo-Json -Depth 12 -Compress
    exit 0
}
if ($action -ne 'save') { throw "Unsupported checkpoint action: $action" }

New-Item -ItemType Directory -Path $directory -Force | Out-Null
$insideRepo = (& git rev-parse --is-inside-work-tree 2>$null) -eq 'true'
$branch = $null
$head = $null
$files = @()
$filesTruncated = $false
if ($insideRepo) {
    $branch = (& git branch --show-current 2>$null | Select-Object -First 1)
    $head = (& git rev-parse HEAD 2>$null | Select-Object -First 1)
    $statusLines = @(& git status --porcelain=v1 --untracked-files=all 2>$null |
        Where-Object { $_ -notmatch '^.. \.agentbridge/(checkpoints\.jsonl|sessions/|visual-regression/)' } |
        Select-Object -First 501)
    $filesTruncated = $statusLines.Count -gt 500
    $files = @($statusLines | Select-Object -First 500 | ForEach-Object {
        [pscustomobject]@{
            status = if ($_.Length -ge 2) { $_.Substring(0, 2) } else { $_ }
            path = if ($_.Length -gt 3) { $_.Substring(3) } else { '' }
        }
    })
}

$canonical = [ordered]@{
    branch = $branch
    head = $head
    note = if ([string]::IsNullOrWhiteSpace($note)) { $null } else { $note }
    testSummary = if ([string]::IsNullOrWhiteSpace($testSummary)) { $null } else { $testSummary }
    filesTruncated = $filesTruncated
    files = $files
}
$canonicalJson = $canonical | ConvertTo-Json -Depth 12 -Compress
$hashBytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonicalJson))
$contentHash = [Convert]::ToHexString($hashBytes).ToLowerInvariant()
$now = [DateTimeOffset]::UtcNow
$latest = if ($entries.Count -gt 0) { $entries[-1] } else { $null }
if ($null -ne $latest -and $latest.contentHash -eq $contentHash) {
    $latestTime = [DateTimeOffset]::Parse([string]$latest.createdAt)
    if (($now - $latestTime).TotalSeconds -lt $debounceSeconds) {
        [pscustomobject]@{
            saved = $false
            reason = 'debounced_duplicate'
            path = $file
            checkpoint = $latest
        } | ConvertTo-Json -Depth 12 -Compress
        exit 0
    }
}

$checkpoint = [ordered]@{
    id = [Guid]::NewGuid().ToString('n')
    createdAt = $now.ToString('o')
    root = $root
    branch = $branch
    head = $head
    note = if ([string]::IsNullOrWhiteSpace($note)) { $null } else { $note }
    testSummary = if ([string]::IsNullOrWhiteSpace($testSummary)) { $null } else { $testSummary }
    changedFileCount = $files.Count
    filesTruncated = $filesTruncated
    files = $files
    contentHash = $contentHash
}

$kept = @($entries | Select-Object -Last ([Math]::Max(0, $maxEntries - 1)))
$lines = New-Object System.Collections.Generic.List[string]
foreach ($entry in $kept) { $lines.Add(($entry | ConvertTo-Json -Depth 12 -Compress)) }
$lines.Add(($checkpoint | ConvertTo-Json -Depth 12 -Compress))
[IO.File]::WriteAllLines($file, $lines, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    saved = $true
    path = $file
    retained = $lines.Count
    checkpoint = $checkpoint
} | ConvertTo-Json -Depth 12 -Compress
""";
    }

    private static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
