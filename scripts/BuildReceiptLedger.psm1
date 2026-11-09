Set-StrictMode -Version Latest
$script:buildReceiptWarned = $false

function Start-BuildReceiptMeter {
    $cpu = -1L
    try {
        if (-not ('BuildReceiptCpuMeter' -as [type])) {
            $source = Join-Path $PSScriptRoot 'BuildReceiptCpuMeter.cs.txt'
            $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            $cache = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'mcg-build-receipt-meter'
            $key = "$hash-$($PSVersionTable.PSEdition)-$([Environment]::Version)"
            $assembly = Join-Path $cache "$key.dll"
            if (-not [IO.File]::Exists($assembly)) {
                [IO.Directory]::CreateDirectory($cache) | Out-Null
                $temporary = Join-Path $cache "$key-$PID-$([Guid]::NewGuid().ToString('N')).dll"
                try {
                    Add-Type -TypeDefinition ([IO.File]::ReadAllText($source)) -OutputAssembly $temporary -ErrorAction Stop
                    if (-not [IO.File]::Exists($assembly)) { [IO.File]::Move($temporary, $assembly) }
                }
                finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
            }
            if (-not ('BuildReceiptCpuMeter' -as [type])) { Add-Type -Path $assembly -ErrorAction Stop }
        }
        $cpu = [BuildReceiptCpuMeter]::ReadMilliseconds()
    }
    catch { $cpu = -1L }
    return [pscustomobject]@{ Wall = [Diagnostics.Stopwatch]::StartNew(); StartCpu = $cpu; Cpu = $null }
}

function Stop-BuildReceiptMeter {
    param($Meter)
    if ($null -eq $Meter) { return }
    $Meter.Wall.Stop()
    try {
        $cpu = [BuildReceiptCpuMeter]::ReadMilliseconds()
        if ($Meter.StartCpu -ge 0 -and $cpu -ge $Meter.StartCpu) { $Meter.Cpu = $cpu - $Meter.StartCpu }
    }
    catch { }
}

function Write-BuildReceipt {
    param([string]$Root, [string]$GoalPrefix, [string]$Tree, [string[]]$Projects, $Meter)
    if ($null -eq $Meter) { return }
    try {
        $hostRoot = $Root
        $directory = [IO.DirectoryInfo]::new($Root)
        while ($null -ne $directory.Parent) {
            if ($directory.Parent.Name -eq '.orchestrator-worktrees') { $hostRoot = $directory.Parent.Parent.FullName; break }
            $directory = $directory.Parent
        }
        $goal = $GoalPrefix
        $context = Join-Path $Root '.orchestrator-context'
        if ([IO.Directory]::Exists($context)) {
            $matches = @([IO.Directory]::GetDirectories($context, "$GoalPrefix*"))
            if ($matches.Count -eq 1) { $goal = [IO.Path]::GetFileName($matches[0]) }
        }
        $digest = if ([string]::IsNullOrWhiteSpace($Tree)) { 'unresolved' }
            elseif ($Tree.StartsWith('wbc1:git-tree:', [StringComparison]::Ordinal)) { $Tree }
            else { "wbc1:git-tree:$Tree" }
        $row = [ordered]@{
            version = 1; recorded_at = [DateTimeOffset]::UtcNow.ToString('o'); goal_id = $goal
            origin = 'worker-check'; tree_digest = $digest; projects = @($Projects)
            wall_ms = $Meter.Wall.Elapsed.TotalMilliseconds; cpu_ms = $Meter.Cpu
            cpu_ms_unavailable_reason = if ($null -eq $Meter.Cpu) { 'accounting-unavailable' } else { $null }
            cache = 'none'
        }
        $state = Join-Path $hostRoot '.orchestrator'
        [IO.Directory]::CreateDirectory($state) | Out-Null
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($row | ConvertTo-Json -Depth 4 -Compress) + [Environment]::NewLine)
        # A single attempt: never wait for another writer or change the build verdict.
        $stream = [IO.FileStream]::new((Join-Path $state 'build-receipts.jsonl'), [IO.FileMode]::Append,
            [IO.FileAccess]::Write, ([IO.FileShare]::Read -bor [IO.FileShare]::Delete), 1)
        try { $stream.Write($bytes, 0, $bytes.Length) }
        finally { $stream.Dispose() }
    }
    catch {
        if (-not $script:buildReceiptWarned) {
            $script:buildReceiptWarned = $true
            try { [Console]::Error.WriteLine('warning: build receipt ledger write skipped: ' + $_.Exception.GetType().Name) }
            catch { }
        }
    }
}

Export-ModuleMember -Function Start-BuildReceiptMeter, Stop-BuildReceiptMeter, Write-BuildReceipt
