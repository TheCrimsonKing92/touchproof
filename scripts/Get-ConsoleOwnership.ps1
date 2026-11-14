<#
.SYNOPSIS
  Report console ownership from a process snapshot and launch receipts.
.DESCRIPTION
  Read-only attribution. Missing evidence suppresses the entire candidate group.
  Snapshot and receipt schemas and cleanup procedure are in the operator runbook.
#>
[CmdletBinding()]
param(
    [string]$SnapshotPath,
    [Parameter(Mandatory = $true)][int]$HarnessProcessId,
    [string]$LaunchReceiptsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture

function Read-JsonRecords([string]$Path) {
    $raw = [IO.File]::ReadAllText($Path)
    if ($raw.Trim() -notmatch '^\[[\s\S]*\]$') { throw 'Expected a JSON array.' }
    # PS7 can coerce ISO strings to dates, losing the original offset evidence.
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        $document = [System.Text.Json.JsonDocument]::Parse($raw)
        try {
            $records = @(foreach ($entry in $document.RootElement.EnumerateArray()) {
                if ($entry.ValueKind.ToString() -ne 'Object') { throw 'Expected a JSON object record.' }
                $fields = @{}
                foreach ($property in $entry.EnumerateObject()) {
                    $value = $property.Value
                    $fields[$property.Name] = switch ($value.ValueKind.ToString()) {
                        'String' { $value.GetString() }
                        'Number' {
                            $number = 0
                            if ($value.TryGetInt32([ref]$number)) { $number } else { $value.GetRawText() }
                        }
                        'Null' { $null }
                        default { throw 'Record fields must be scalar strings or integers.' }
                    }
                }
                [pscustomobject]$fields
            })
            return ,$records
        } finally { $document.Dispose() }
    }
    $converted = ConvertFrom-Json -InputObject $raw
    return ,@($converted)
}

function Get-Field($Record, [string]$Name) {
    $property = $Record.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}

function Test-Id($Value, [int]$Minimum = 1) {
    return (($Value -is [int] -or $Value -is [long]) -and $Value -ge $Minimum -and $Value -le [int]::MaxValue)
}

function Get-Instant($Value) {
    if ($Value -isnot [string] -or $Value -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$') { return $null }
    $instant = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse($Value, $culture, [Globalization.DateTimeStyles]::None, [ref]$instant)) {
        return $instant.ToUniversalTime()
    }
    return $null
}

function Format-Instant($Instant) {
    if ($null -eq $Instant) { return 'unreadable' }
    return $Instant.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", $culture)
}

function Read-LiveSnapshot {
    $lines = @(& (Join-Path $PSScriptRoot 'Get-RepoProcessInfo.ps1') -Name '*' -Newest ([int]::MaxValue) 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'Live process inventory unavailable.' }
    $records = @(foreach ($line in $lines) {
        $text = [string]$line
        if ($text -match '^PROCESS_QUERY_UNAVAILABLE\b') { throw 'Live process inventory contains inaccessible evidence.' }
        if ($text -match '^PROCESS id=(\d+) parent=(\d+) name=(.*?) created=(.*?) path=.*? command=(.*)$') {
            [pscustomobject]@{
                processId = [int]$Matches[1]; parentProcessId = [int]$Matches[2]
                name = $Matches[3]; startTime = $Matches[4]; commandLine = $Matches[5]
            }
        } elseif ($text -notmatch '^MCG_PHASE=') { throw 'Live process inventory format unavailable.' }
    })
    if ($records.Count -eq 0) { throw 'Live process inventory empty.' }
    return ,$records
}

function Get-ParentChain([int]$FirstId, $Processes) {
    $seen = @{}
    $currentId = $FirstId
    while ($Processes.ContainsKey($currentId) -and -not $seen.ContainsKey($currentId)) {
        $seen[$currentId] = $true
        $current = $Processes[$currentId]
        Write-Output $currentId
        if (-not $Processes.ContainsKey($current.parentProcessId)) { break }
        $parent = $Processes[$current.parentProcessId]
        if ($null -eq $current.instant -or $null -eq $parent.instant -or $parent.instant -gt $current.instant) { break }
        $currentId = $parent.processId
    }
}

function Get-IdentityMatches($Process, $Receipts) {
    if ($null -eq $Process.instant) { return }
    foreach ($receipt in $Receipts) {
        if ($receipt.processId -eq $Process.processId -and $null -ne $receipt.instant -and
            [Math]::Abs(($receipt.instant - $Process.instant).Ticks) -le [TimeSpan]::TicksPerSecond) {
            Write-Output $receipt
        }
    }
}

function Get-LaunchedProcessMatches($HostRecord, $Processes, $Receipts) {
    # Only the immediate, temporally valid parent can be the receipted launch.
    if ($null -eq $HostRecord.instant -or -not $Processes.ContainsKey($HostRecord.parentProcessId)) { return }
    $parent = $Processes[$HostRecord.parentProcessId]
    if ($null -eq $parent.instant -or $parent.instant -gt $HostRecord.instant) { return }
    foreach ($receipt in @(Get-IdentityMatches $parent $Receipts)) {
        [pscustomobject]@{ receipt = $receipt; process = $parent; route = 'launched-process' }
    }
}

try {
    $snapshot = if ([string]::IsNullOrWhiteSpace($SnapshotPath)) { Read-LiveSnapshot } else { Read-JsonRecords $SnapshotPath }
    $processes = @{}
    foreach ($entry in $snapshot) {
        $idValue = Get-Field $entry 'processId'
        $parentValue = Get-Field $entry 'parentProcessId'
        $nameValue = Get-Field $entry 'name'
        if (-not (Test-Id $idValue) -or -not (Test-Id $parentValue 0) -or
            $nameValue -isnot [string] -or [string]::IsNullOrWhiteSpace($nameValue)) { throw 'Invalid snapshot identity fields.' }
        if ($processes.ContainsKey([int]$idValue)) { throw 'Duplicate snapshot process ids.' }
        $processes[[int]$idValue] = [pscustomobject]@{
            processId = [int]$idValue; parentProcessId = [int]$parentValue; name = $nameValue
            instant = Get-Instant (Get-Field $entry 'startTime')
        }
    }
    if (-not $processes.ContainsKey($HarnessProcessId)) { throw 'Harness process missing from snapshot.' }
} catch {
    Write-Output "Ownership ambiguous: snapshot unavailable or invalid ($($_.Exception.Message)); candidate group suppressed."
    exit 2
}

$receiptError = $null
$receipts = @()
try {
    if ([string]::IsNullOrWhiteSpace($LaunchReceiptsPath)) { throw 'Launch receipts path missing.' }
    $receipts = @(foreach ($entry in (Read-JsonRecords $LaunchReceiptsPath)) {
        $idValue = Get-Field $entry 'processId'
        $startValue = Get-Field $entry 'startTime'
        $dispatchValue = Get-Field $entry 'dispatchId'
        if (-not (Test-Id $idValue) -or $startValue -isnot [string] -or
            $dispatchValue -isnot [string] -or [string]::IsNullOrWhiteSpace($dispatchValue) -or
            $dispatchValue -match '[\r\n\x00-\x1f]') { throw 'Invalid receipt identity or dispatch fields.' }
        [pscustomobject]@{ processId = [int]$idValue; instant = Get-Instant $startValue; dispatchId = $dispatchValue }
    })
} catch {
    $receiptError = 'Launch receipts missing, unreadable or malformed.'
    $receipts = @()
}

try {
    $ancestors = @(Get-ParentChain $HarnessProcessId $processes)
    $harnessHosts = @()
    $candidates = @()
    $unattributed = @()
    $hosts = @($processes.Values | Where-Object { $_.name -match '^(OpenConsole|conhost)(\.exe)?$' } | Sort-Object processId)
    foreach ($hostRecord in $hosts) {
        $reason = $null
        if ($hostRecord.processId -eq $HarnessProcessId) { $reason = 'harness process' }
        elseif ($ancestors -contains $hostRecord.processId) { $reason = 'harness ancestor' }
        elseif (@(Get-ParentChain $hostRecord.processId $processes) -contains $HarnessProcessId) { $reason = 'harness descendant-console' }
        if ($null -ne $reason) {
            $harnessHosts += [pscustomobject]@{ hostRecord = $hostRecord; reason = $reason }
            continue
        }
        $matchesForHost = @(foreach ($receipt in @(Get-IdentityMatches $hostRecord $receipts)) {
            [pscustomobject]@{ receipt = $receipt; process = $hostRecord; route = 'direct' }
        })
        $matchesForHost += @(Get-LaunchedProcessMatches $hostRecord $processes $receipts)
        if ($matchesForHost.Count -gt 0) {
            $candidates += [pscustomobject]@{ hostRecord = $hostRecord; evidence = $matchesForHost }
        } else {
            $reason = 'no matching receipt'
            if ($null -ne $receiptError) { $reason = 'receipts unavailable' }
            elseif ($null -eq $hostRecord.instant) { $reason = 'host start time invalid or missing offset' }
            elseif (@($receipts | Where-Object { $_.processId -eq $hostRecord.processId -and $null -eq $_.instant }).Count -gt 0) { $reason = 'receipt start time invalid or missing offset' }
            elseif (@($receipts | Where-Object { $_.processId -eq $hostRecord.processId }).Count -gt 0) { $reason = 'start-time mismatch (reused id)' }
            $unattributed += [pscustomobject]@{ hostRecord = $hostRecord; reason = $reason }
        }
    }
    # Assemble the report before emitting anything: an exception cannot leak partial candidates.
    $ambiguous = $null -ne $receiptError -or $unattributed.Count -gt 0
    $result = if ($ambiguous) { 'ambiguous' } else { 'resolved' }
    $report = @("Console ownership harness=$HarnessProcessId harness-owned=$($harnessHosts.Count) orchestrator-created=$($candidates.Count) unattributed=$($unattributed.Count) result=$result", 'harness-owned:')
    foreach ($item in $harnessHosts) {
        $report += "  WITHHELD id=$($item.hostRecord.processId) name=$($item.hostRecord.name) startTime=$(Format-Instant $item.hostRecord.instant) reason=$($item.reason)"
    }
    $report += 'unattributed:'
    foreach ($item in $unattributed) {
        $report += "  UNPLACED id=$($item.hostRecord.processId) name=$($item.hostRecord.name) startTime=$(Format-Instant $item.hostRecord.instant) reason=$($item.reason)"
    }
    if ($ambiguous) {
        $unplaced = ($unattributed | ForEach-Object { $_.hostRecord.processId }) -join ','
        $report += "Ownership ambiguous: $receiptError unplaced=[$unplaced]; candidate group suppressed ($($candidates.Count) matched hosts withheld)."
    } else {
        $report += 'orchestrator-created candidates:'
        foreach ($item in $candidates) {
            $evidence = @($item.evidence | ForEach-Object {
                "dispatchId=$($_.receipt.dispatchId) route=$($_.route) matched(processId=$($_.process.processId),startTime=$(Format-Instant $_.process.instant),receiptStartTime=$(Format-Instant $_.receipt.instant),tolerance=1s)"
            } | Sort-Object -Unique)
            $report += "  CANDIDATE id=$($item.hostRecord.processId) name=$($item.hostRecord.name) startTime=$(Format-Instant $item.hostRecord.instant) evidence=$($evidence -join '; ')"
        }
    }
    Write-Output $report
    if ($ambiguous) { exit 2 }
    exit 0
} catch {
    Write-Output 'Ownership ambiguous: unexpected attribution failure; candidate group suppressed.'
    exit 1
}
