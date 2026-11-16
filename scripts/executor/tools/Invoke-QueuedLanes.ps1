# Runs ON an executor as the action of the scheduled task "mcg-executor-lane" (principal mcgrunner, S4U).
# It drains C:\mcg-executor\queue\*.json oldest first, running up to $Slots lanes at once. Each job is
# {"sha","project","filter","lane",...}. A taken job moves to queue\taken\ before it runs, so a retrigger never
# runs it twice. Task Scheduler ignores a retrigger while this instance runs, so the loop keeps polling and picks up
# jobs queued after it started; it exits only when the queue is empty and no child runs.
# Task Scheduler owns these processes, so a dropped SSH connection cannot stop a lane.
param(
    [string]$RunRoot = 'C:\mcg-executor',
    # Defaults to the number in $RunRoot\slots.txt (written by the operator to match the host's "slots"), else 1.
    [int]$Slots = 0,
    [switch]$RetentionOnly,
    [ValidateRange(0, 36500)][int]$RunFolderDays = 1,
    [ValidateRange(0, 36500)][int]$TakenDays = 14,
    [ValidateRange(0, 36500)][int]$ResultDays = 14,
    [ValidateScript({ $_ -ge 0 -and -not [double]::IsNaN($_) -and -not [double]::IsInfinity($_) })]
    [double]$RunFolderFreeSpaceFloorGB = 60,
    [scriptblock]$FreeSpaceReader
)
$ErrorActionPreference = 'Stop'

function Invoke-ExecutorRetention([string]$Root, [int]$RunDays, [int]$JobDays, [int]$OutputDays,
    [double]$FloorGB, [scriptblock]$SpaceReader) {
    $now = [DateTime]::UtcNow
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $repo = Join-Path $Root 'repo\repo.git'
    $takenPath = Join-Path $Root 'queue\taken'
    $counts = @{ runs = 0; taken = 0; results = 0; failed = 0; floorRemoved = 0 }
    $measurement = @{ Failed = $false }
    $runningNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $runningPrefixes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $attemptedRuns = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    function Read-RetentionFreeBytes {
        try {
            if ($SpaceReader) { $values = @(& $SpaceReader $Root) }
            else {
                # A directory path resolves mounted volumes, and available bytes respect account quotas.
                if (-not ('ExecutorRetentionDiskSpace' -as [type])) {
                    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class ExecutorRetentionDiskSpace {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
    public static ulong Read(string path) {
        ulong available, total, free;
        if (!GetDiskFreeSpaceEx(path, out available, out total, out free))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return available;
    }
}
'@
                }
                $values = @([ExecutorRetentionDiskSpace]::Read($rootPath))
            }
            if ($values.Count -ne 1 -or $null -eq $values[0] -or
                [Type]::GetTypeCode($values[0].GetType()).ToString() -notin
                    @('Byte', 'SByte', 'Int16', 'UInt16', 'Int32', 'UInt32', 'Int64', 'UInt64', 'Single', 'Double', 'Decimal')) {
                throw 'Reader must return one numeric available-byte value'
            }
            $bytes = [double]$values[0]
            if ($bytes -lt 0 -or [double]::IsNaN($bytes) -or [double]::IsInfinity($bytes)) {
                throw 'Available bytes must be finite and nonnegative'
            }
            return $bytes
        } catch {
            if (-not $measurement.Failed) { Write-Host "retention warning: free space unavailable: $($_.Exception.Message)" }
            $measurement.Failed = $true
            return $null
        }
    }
    function Format-RetentionGB($Bytes) {
        if ($null -eq $Bytes) { return 'unavailable' }
        return ($Bytes / 1GB).ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)
    }
    $freeBefore = Read-RetentionFreeBytes
    $freeAfter = $freeBefore

    function Invoke-RetentionGit([string[]]$Arguments) {
        # Windows PowerShell turns native stderr into error records. Preserve it without terminating the pass.
        $ErrorActionPreference = 'Continue'
        $PSNativeCommandUseErrorActionPreference = $false
        try {
            $git = Get-Command git -CommandType Application -ErrorAction Stop
            $output = @(& $git.Source -C $repo @Arguments 2>&1)
            return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output -join ' ') }
        } catch { return [pscustomobject]@{ ExitCode = -1; Text = $_.Exception.Message } }
    }
    function Assert-RetentionFolder([IO.DirectoryInfo]$Folder, [string]$Kind) {
        $Folder.Refresh()
        $path = [IO.Path]::GetFullPath($Folder.FullName)
        if (-not $path.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
            ($Folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing retention outside the run root or through a reparse point: $path"
        }
        # Check parents as well: a runs/results directory must not redirect a validated child elsewhere.
        for ($parent = $Folder.Parent; $parent -and ($parent.FullName.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar) -ne $rootPath; $parent = $parent.Parent) {
            if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing retention through a reparse point: $path" }
        }
        $prefix = $Folder.Name
        if ($Kind -eq 'results') { $prefix = $Folder.Parent.Name }
        if ($Kind -in @('runs', 'results') -and
            ($prefix -notmatch '^[A-Za-z0-9-]{9}$' -or (Test-RetentionProtected $prefix))) {
            throw "Refusing retention of an invalid or protected folder: $path"
        }
    }
    function Test-RetentionProtected([string]$Prefix) {
        if ($runningPrefixes.Contains($Prefix)) { return $true }
        foreach ($name in $runningNames) {
            if ($name.IndexOf($Prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        }
        return $false
    }
    function Remove-RetentionEntry($Entry, [string]$Kind, [switch]$Folder) {
        try {
            if ($Folder) {
                Assert-RetentionFolder $Entry $Kind
                try { Remove-Item -LiteralPath $Entry.FullName -Recurse -Force -ErrorAction Stop } catch { }
                if (Test-Path -LiteralPath $Entry.FullName) {
                    Assert-RetentionFolder $Entry $Kind
                    Invoke-RetentionMirror $Entry $Kind
                }
            } else { Remove-Item -LiteralPath $Entry.FullName -Force -ErrorAction Stop }
            if (Test-Path -LiteralPath $Entry.FullName) { throw 'Entry still exists after removal' }
            $counts[$Kind]++
            $label = @{ runs = 'run'; taken = 'taken'; results = 'result' }[$Kind]
            Write-Host "retention removed $label $($Entry.FullName)"
        } catch {
            $counts.failed++
            Write-Host "retention failed $Kind $($Entry.FullName): $($_.Exception.Message)"
        }
    }
    function Invoke-RetentionMirror([IO.DirectoryInfo]$Entry, [string]$Kind) {
        $empty = Join-Path $Root ('retention-empty-' + [Guid]::NewGuid().ToString('N'))
        $created = $false
        try {
            # New-Item without Force rejects an existing source rather than mirroring its contents.
            $null = New-Item -ItemType Directory -Path $empty -ErrorAction Stop
            $created = $true
            Assert-RetentionFolder $Entry $Kind
            Write-Host "retention fallback mirror $($Entry.FullName)"
            $ErrorActionPreference = 'Continue'
            $PSNativeCommandUseErrorActionPreference = $false
            try {
                $robocopy = Get-Command robocopy -CommandType Application -ErrorAction Stop
                # Do not traverse descendant junctions or symbolic-link targets during the purge.
                $null = @(& $robocopy.Source $empty $Entry.FullName /MIR /XJ /SL /R:0 /W:0 /NFL /NDL /NJH /NJS /NP 2>&1)
            } catch { }
            # Non-recursive deletion cannot traverse anything the mirror left behind.
            try {
                Assert-RetentionFolder $Entry $Kind
                [IO.Directory]::Delete($Entry.FullName, $false)
            } catch { }
        } finally {
            if ($created) {
                try { [IO.Directory]::Delete($empty, $false) }
                catch { Write-Host "retention warning: temporary folder not removed $empty`: $($_.Exception.Message)" }
            }
        }
    }
    function Remove-RetentionRun([IO.DirectoryInfo]$Folder) {
        [void]$attemptedRuns.Add($Folder.Name)
        try {
            Assert-RetentionFolder $Folder 'runs'
            $removed = Invoke-RetentionGit @('worktree', 'remove', '--force', $Folder.FullName)
            if (Test-Path -LiteralPath $Folder.FullName) { Remove-RetentionEntry $Folder 'runs' -Folder }
            else {
                $counts.runs++
                Write-Host "retention removed run $($Folder.FullName)"
            }
        } catch {
            $counts.failed++
            Write-Host "retention failed run $($Folder.FullName): $($_.Exception.Message)"
        }
    }

    try {
        $statePath = Join-Path $Root 'runner-state.json'
        # Missing state is an idle root. Existing but unusable state cannot authorize deletion.
        try {
            if (Test-Path -LiteralPath $statePath) {
                $state = Get-Content -Raw -LiteralPath $statePath -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                if (-not $state -or -not $state.PSObject.Properties['running'] -or $state.running -isnot [Array]) {
                    throw 'Runner state must contain a running array'
                }
                foreach ($name in $state.running) {
                    if ($name -isnot [string] -or [string]::IsNullOrWhiteSpace($name) -or
                        [IO.Path]::GetFileName($name) -ne $name) { throw 'Invalid running job name' }
                    [void]$runningNames.Add($name)
                    # Real job names identify attempts/lanes, so the taken job supplies the commit.
                    $jobPath = Join-Path $takenPath ($name + '.json')
                    if (Test-Path -LiteralPath $jobPath) {
                        $job = Get-Content -Raw -LiteralPath $jobPath -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                        if ([string]$job.sha -notmatch '^[0-9a-fA-F]{9,64}$') { throw "Invalid sha in running job $jobPath" }
                        [void]$runningPrefixes.Add($job.sha.Substring(0, 9))
                    }
                }
            }
        } catch {
            $counts.failed++
            Write-Host "retention skipped: runner state unreadable: $($_.Exception.Message)"
            return $counts.failed
        }

        $runsPath = Join-Path $Root 'runs'
        if (Test-Path -LiteralPath $runsPath) {
            foreach ($folder in (Get-ChildItem -LiteralPath $runsPath -Directory | Sort-Object Name)) {
                if ($folder.Name -notmatch '^[A-Za-z0-9-]{9}$' -or $folder.LastWriteTimeUtc -ge $now.AddDays(-$RunDays) -or
                    (Test-RetentionProtected $folder.Name)) { continue }
                Remove-RetentionRun $folder
            }
        }

        if (Test-Path -LiteralPath $takenPath) {
            foreach ($file in (Get-ChildItem -LiteralPath $takenPath -File)) {
                if ($file.Name -eq 'cancelled.log' -or $file.Extension -notin @('.json', '.cancel') -or
                    $file.LastWriteTimeUtc -ge $now.AddDays(-$JobDays) -or $runningNames.Contains($file.BaseName)) { continue }
                Remove-RetentionEntry $file 'taken'
            }
        }
        $resultsPath = Join-Path $Root 'results'
        if (Test-Path -LiteralPath $resultsPath) {
            foreach ($commit in (Get-ChildItem -LiteralPath $resultsPath -Directory)) {
                if ($commit.Name -notmatch '^[A-Za-z0-9-]{9}$' -or (Test-RetentionProtected $commit.Name)) { continue }
                try {
                    Assert-RetentionFolder $commit
                    foreach ($lane in (Get-ChildItem -LiteralPath $commit.FullName -Directory)) {
                        if ($lane.LastWriteTimeUtc -lt $now.AddDays(-$OutputDays)) {
                            Remove-RetentionEntry $lane 'results' -Folder
                        }
                    }
                    # Never recursively remove a commit container: a lane may have just created output in it.
                    if (@(Get-ChildItem -LiteralPath $commit.FullName -Force).Count -eq 0) {
                        [IO.Directory]::Delete($commit.FullName, $false)
                    }
                } catch {
                    $counts.failed++
                    Write-Host "retention failed result $($commit.FullName): $($_.Exception.Message)"
                }
            }
        }
        $freeAfter = Read-RetentionFreeBytes
        if (-not $measurement.Failed -and $freeAfter / 1GB -lt $FloorGB) {
            $candidates = [System.Collections.Generic.List[IO.DirectoryInfo]]::new()
            if (Test-Path -LiteralPath $runsPath) {
                foreach ($folder in (Get-ChildItem -LiteralPath $runsPath -Directory)) {
                    if ($folder.Name -match '^[A-Za-z0-9-]{9}$' -and -not $attemptedRuns.Contains($folder.Name) -and
                        -not (Test-RetentionProtected $folder.Name) -and
                        -not ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $candidates.Add($folder) }
                }
            }
            $candidates.Sort([Comparison[IO.DirectoryInfo]]{
                param($left, $right)
                $order = $left.LastWriteTimeUtc.CompareTo($right.LastWriteTimeUtc)
                if ($order -eq 0) { return [StringComparer]::Ordinal.Compare($left.Name, $right.Name) }
                return $order
            })
            foreach ($folder in $candidates) {
                if ($measurement.Failed -or $freeAfter / 1GB -ge $FloorGB) { break }
                $previous = $counts.runs
                Remove-RetentionRun $folder
                $counts.floorRemoved += $counts.runs - $previous
                $freeAfter = Read-RetentionFreeBytes
            }
            if (-not $measurement.Failed -and $freeAfter / 1GB -lt $FloorGB) {
                Write-Host "retention below floor: freeGB=$(Format-RetentionGB $freeAfter) floorGB=$($FloorGB.ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)); no removable run folders remain"
            }
        }
        $prune = Invoke-RetentionGit @('worktree', 'prune')
        if ($prune.ExitCode -ne 0) {
            $counts.failed++
            Write-Host "retention failed prune: $($prune.Text)"
        }
    } catch {
        $counts.failed++
        Write-Host "retention failed pass: $($_.Exception.Message)"
    } finally {
        Write-Host "retention summary: runs removed=$($counts.runs) window=$($RunDays)d; taken removed=$($counts.taken) window=$($JobDays)d; results removed=$($counts.results) window=$($OutputDays)d; failed=$($counts.failed); floorGB=$($FloorGB.ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)) freeBeforeGB=$(Format-RetentionGB $freeBefore) freeAfterGB=$(Format-RetentionGB $freeAfter) floorRemoved=$($counts.floorRemoved)"
    }
    return $counts.failed
}

if ($RetentionOnly) {
    $failures = Invoke-ExecutorRetention $RunRoot $RunFolderDays $TakenDays $ResultDays $RunFolderFreeSpaceFloorGB $FreeSpaceReader
    if ($failures -gt 0) { exit 1 }
    exit 0
}
$queue = Join-Path $RunRoot 'queue'
$taken = Join-Path $queue 'taken'
New-Item -ItemType Directory -Force -Path $taken | Out-Null
if ($Slots -lt 1) {
    $slotsFile = Join-Path $RunRoot 'slots.txt'
    $Slots = 1
    if (Test-Path -LiteralPath $slotsFile) {
        $parsed = 0
        if ([int]::TryParse((Get-Content -Raw -LiteralPath $slotsFile).Trim(), [ref]$parsed) -and $parsed -ge 1 -and $parsed -le 16) { $Slots = $parsed }
    }
}
$pwsh = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
if (-not (Test-Path -LiteralPath $pwsh)) { $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source }
$jobScript = Join-Path $RunRoot 'tools\Invoke-ExecutorJob.ps1'
$running = [System.Collections.Generic.List[object]]::new()
# The host reads $RunRoot\runner-state.json before offering a lane (occupancyProbe) and treats a state older than
# 120 s as not busy, so it is rewritten every loop pass and emptied on exit. Written atomically: temp then rename.
$statePath = Join-Path $RunRoot 'runner-state.json'
function Write-RunnerState([string[]]$RunningNames, [string[]]$QueuedNames, [switch]$RequireSuccess) {
    $state = [ordered]@{
        utc = [DateTime]::UtcNow.ToString('o'); slots = $Slots
        running = @($RunningNames | Where-Object { $_ }); queued = @($QueuedNames | Where-Object { $_ })
    }
    $temp = "$statePath.tmp"
    try {
        Set-Content -LiteralPath $temp -Value ($state | ConvertTo-Json -Compress) -Encoding utf8
        Move-Item -LiteralPath $temp -Destination $statePath -Force
    } catch { if ($RequireSuccess) { throw } }
}
$lastRetentionAttempt = [DateTime]::MinValue
while ($true) {
    # The host also writes <job base name>.cancel for a job that already started (it abandoned the lane).
    # Stop that job's whole process tree so it frees its slot instead of running to completion.
    for ($i = $running.Count - 1; $i -ge 0; $i--) {
        $child = $running[$i]
        $cancel = Join-Path $queue ($child.BaseName + '.cancel')
        if (-not $child.Process.HasExited -and (Test-Path -LiteralPath $cancel)) {
            & taskkill.exe /PID $child.Process.Id /T /F 2>&1 | Out-Null
            Move-Item -LiteralPath $cancel -Destination (Join-Path $taken ($child.BaseName + '.cancel')) -Force -ErrorAction SilentlyContinue
            Add-Content -Path (Join-Path $taken 'cancelled.log') -Value "$([DateTime]::UtcNow.ToString('o')) $($child.Name) started"
            if (-not (Test-Path -LiteralPath $child.ErrorFile)) {
                Set-Content -Path $child.ErrorFile -Value "$([DateTime]::UtcNow.ToString('o')) $($child.Name): cancelled by host after start"
            }
        }
    }
    # Reap exited children; a nonzero exit without its own error file gets one.
    for ($i = $running.Count - 1; $i -ge 0; $i--) {
        $child = $running[$i]
        if ($child.Process.HasExited) {
            if ($child.Process.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $child.ErrorFile)) {
                Set-Content -Path $child.ErrorFile -Value "$([DateTime]::UtcNow.ToString('o')) $($child.Name): exit $($child.Process.ExitCode)"
            }
            $running.RemoveAt($i)
        }
    }
    $queued = @(Get-ChildItem -Path $queue -Filter *.json -File | Sort-Object LastWriteTimeUtc)
    Write-RunnerState ($running | ForEach-Object { $_.BaseName }) ($queued | ForEach-Object { $_.BaseName })
    if ($queued.Count -eq 0 -and $running.Count -eq 0) { break }
    foreach ($next in $queued) {
        if ($running.Count -ge $Slots) { break }
        $claimed = Join-Path $taken $next.Name
        try { Move-Item -Path $next.FullName -Destination $claimed -ErrorAction Stop } catch { continue }
        # The host writes <job base name>.cancel when it abandons a lane before the job starts.
        $cancel = Join-Path $queue ($next.BaseName + '.cancel')
        if (Test-Path -LiteralPath $cancel) {
            Move-Item -LiteralPath $cancel -Destination (Join-Path $taken ($next.BaseName + '.cancel')) -ErrorAction SilentlyContinue
            Add-Content -Path (Join-Path $taken 'cancelled.log') -Value "$([DateTime]::UtcNow.ToString('o')) $($next.Name)"
            continue
        }
        $process = Start-Process -FilePath $pwsh -WindowStyle Hidden -PassThru `
            -ArgumentList '-NoProfile', '-NonInteractive', '-File', $jobScript, '-JobPath', $claimed, '-RunRoot', $RunRoot
        $running.Add([pscustomobject]@{ Process = $process; Name = $next.Name; BaseName = $next.BaseName
                ErrorFile = [IO.Path]::ChangeExtension($claimed, '.error.txt') })
    }
    if (([DateTime]::UtcNow - $lastRetentionAttempt).TotalHours -ge 1) {
        $lastRetentionAttempt = [DateTime]::UtcNow
        try {
            Write-RunnerState ($running | ForEach-Object { $_.BaseName }) ($queued | ForEach-Object { $_.BaseName }) -RequireSuccess
            $null = Invoke-ExecutorRetention $RunRoot $RunFolderDays $TakenDays $ResultDays $RunFolderFreeSpaceFloorGB $FreeSpaceReader
        } catch { Write-Host "retention failed in-loop: $($_.Exception.Message)" }
    }
    if ($running.Count -gt 0) {
        try { Wait-Process -Id ($running | ForEach-Object { $_.Process.Id }) -Timeout 2 -ErrorAction SilentlyContinue } catch { }
    }
    else { Start-Sleep -Seconds 2 }
}
Write-RunnerState @() @()
# Markers whose job had already finished are moved aside so they do not accumulate.
Get-ChildItem -Path $queue -File | Where-Object Extension -eq '.cancel' |
    Move-Item -Destination $taken -ErrorAction SilentlyContinue
try {
    $null = Invoke-ExecutorRetention $RunRoot $RunFolderDays $TakenDays $ResultDays $RunFolderFreeSpaceFloorGB $FreeSpaceReader
} catch { Write-Host "retention failed post-loop: $($_.Exception.Message)" }
