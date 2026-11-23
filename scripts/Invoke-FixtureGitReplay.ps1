<#
.SYNOPSIS
  Capture the frozen fixture Git workload for the baseline or replacement candidate.
.DESCRIPTION
  Acceptance runs this on Windows with permission to start the Kernel-Process ETW
  provider. Use a clean, already-built candidate and a new evidence directory.
  The test host writes to an isolated MTP results directory. The launcher copies
  its artifacts to the evidence directory after each capture.
  Run both arms with the same configuration, host, Git, .NET and fixture inputs.
  ETW is bounded to 64 MiB and each replay job to 900 seconds. The raw ETL is
  system-wide and should stay with the restricted acceptance artifacts.

  Each selection retains the native ETL, the rooted process tree, test runner
  output, raw TRX and slot/probe JSONL data, a case manifest and shared-probe
  caller counts. The receipt records build/run artifacts with SHA-256, ETW loss
  and attribution residual, native Git counts, and job CPU in 100 ns and seconds.
  Review the caller, parent and arguments for setup, asserted behavior, oracle
  and the shipped landing exclusion before ranking. Unattributed native Git
  starts and capped slot callers remain visible for reconciliation.
.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-FixtureGitReplay.ps1 -EvidenceDirectory C:\evidence\fixture-git-baseline
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Fixture Git replay requires Windows and PowerShell 7.' }

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj'
$runner = Join-Path $PSScriptRoot 'Invoke-TestSummary.ps1'
Import-Module (Join-Path $PSScriptRoot 'MtpTestRunner.psm1') -Force
$nativeSource = Join-Path $PSScriptRoot 'FixtureGitReplayWindows.cs.txt'
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$provider = 'Microsoft-Windows-Kernel-Process'
$traceLimitMiB = 64
$timeoutSeconds = 900
$censusRevision = '8c104aa81cc389c43d8a9d1bd74d19121048b55e'
$integrationRevision = '08f295076eae4d1c14660a846e3c917de32d7a9d'
$exclusionTip = '2464c323f6901cecac8889547631a2076817dcf1'

function Invoke-CheckedNative {
    param([string]$Executable, [string[]]$ArgumentList)
    $output = & $Executable @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable exited $LASTEXITCODE. $($output -join ' ')"
    }
    $output
}

function Write-JsonFile {
    param([string]$Path, [object]$Value)
    ConvertTo-Json -InputObject $Value -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Read-TraceTree {
    param([string]$Path, $Replay)
    $nodesByParent = @{}
    $root = $null
    Get-WinEvent -Path $Path -Oldest -FilterXPath '*[System[EventID=1]]' | ForEach-Object {
        $event = $_
        try {
            if ($event.ProviderName -ne $provider) { return }
            $fields = @{}
            foreach ($field in ([xml]$event.ToXml()).Event.EventData.Data) {
                $fields[$field.GetAttribute('Name')] = $field.InnerText
            }
            foreach ($name in 'ProcessID', 'ParentProcessID', 'ProcessSequenceNumber', 'ParentProcessSequenceNumber', 'CreateTime', 'ImageName') {
                if ([string]::IsNullOrEmpty($fields[$name])) { throw "Kernel-Process start is missing $name." }
            }
            $start = [long]0
            if (-not [long]::TryParse($fields.CreateTime, [ref]$start)) {
                $start = [DateTimeOffset]::Parse($fields.CreateTime, [Globalization.CultureInfo]::InvariantCulture).ToFileTime()
            }
            if ($start -lt $Replay.RootStartFileTime) { return }
            $node = [pscustomobject]@{
                pid = [uint32]$fields.ProcessID
                parent_pid = [uint32]$fields.ParentProcessID
                sequence = $fields.ProcessSequenceNumber
                parent_sequence = $fields.ParentProcessSequenceNumber
                start_filetime = $start
                image = $fields.ImageName
            }
            if ($node.pid -eq $Replay.RootPid -and $node.start_filetime -eq $Replay.RootStartFileTime) {
                if ($null -ne $root) { throw 'Duplicate replay root in the native process trace.' }
                $root = $node
            }
            if (-not $nodesByParent.ContainsKey($node.parent_sequence)) {
                $nodesByParent[$node.parent_sequence] = [Collections.Generic.List[object]]::new()
            }
            $nodesByParent[$node.parent_sequence].Add($node)
        }
        finally { $event.Dispose() }
    }
    if ($null -eq $root) { throw 'The native process trace is missing the replay root.' }
    $tree = [Collections.Generic.List[object]]::new()
    $sequences = [Collections.Generic.HashSet[string]]::new()
    $tree.Add($root)
    for ($index = 0; $index -lt $tree.Count; $index++) {
        $node = $tree[$index]
        if (-not $sequences.Add($node.sequence)) { throw 'Duplicate process sequence in the replay tree.' }
        foreach ($child in $nodesByParent[$node.sequence]) {
            if ($child.parent_pid -ne $node.pid) { throw 'Parent PID and sequence disagree in the replay tree.' }
            $tree.Add($child)
        }
    }
    if ($tree.Count -ne $Replay.ProcessCount) {
        throw "ETW retained $($tree.Count) starts for the tree. The contained job counted $($Replay.ProcessCount) processes."
    }
    $tree.ToArray()
}

function Read-ReplayCases {
    param([string]$Results, [string]$Class, [string[]]$Methods)
    $trx = @(Get-ChildItem -LiteralPath $Results -Filter '*.trx' -File -Recurse)
    if ($trx.Count -eq 0) { throw 'The replay produced no TRX.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $cases = @(foreach ($report in $trx) {
        [xml]$xml = Get-Content -Raw -LiteralPath $report.FullName
        $definitions = @{}
        foreach ($definition in $xml.SelectNodes("//*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")) {
            $definitions[$definition.id] = $definition.TestMethod
        }
        foreach ($result in $xml.SelectNodes("//*[local-name()='Results']/*[local-name()='UnitTestResult']")) {
            $method = $definitions[$result.testId]
            $identity = "$($method.className).$($method.name)"
            if ($method.className -cne $Class -or $method.name -cnotin $Methods -or
                $result.outcome -cne 'Passed' -or -not $seen.Add($identity)) {
                throw "Unexpected case, repetition or outcome in the replay TRX ($identity, $($result.outcome))."
            }
            [ordered]@{
                identity = $identity
                display_name = $result.testName
                parameters = @()
                repetitions = 1
                outcome = $result.outcome
            }
        }
    })
    if ($cases.Count -ne $Methods.Count) {
        throw "Expected $($Methods.Count) executed cases in $Class and found $($cases.Count)."
    }
    $cases | Sort-Object { $_.identity }
}

function Read-SlotCensus {
    param([string]$Path, [object[]]$Cases, [object[]]$Tree)
    $slotCounts = @{}
    $processIds = @($Tree.pid)
    $caseNames = @($Cases | ForEach-Object { $_.identity })
    foreach ($line in Get-Content -LiteralPath $Path) {
        $row = ConvertFrom-Json -AsHashtable $line
        if ($row.test_name -cnotin $caseNames -or $row.process_id -notin $processIds -or
            $slotCounts.ContainsKey($row.test_name)) {
            throw "Unexpected slot observation for $($row.test_name) in process $($row.process_id)."
        }
        if (-not $row.Contains('git_launches') -or -not $row.Contains('git_subcommands') -or
            -not $row.Contains('git_callers') -or -not $row.Contains('git_fixture_probe_launches')) {
            throw "Incomplete Git census for $($row.test_name)."
        }
        $commandTotal = [int]($row.git_subcommands.Values | Measure-Object -Sum).Sum
        $callerTotal = [int]($row.git_callers.Values | Measure-Object -Sum).Sum
        if ($commandTotal -ne $row.git_launches -or $callerTotal -ne $row.git_launches -or
            $row.git_fixture_probe_launches -lt 0 -or $row.git_fixture_probe_launches -gt $row.git_launches) {
            throw "Git census totals disagree for $($row.test_name)."
        }
        $slotCounts[$row.test_name] = $row
    }
    if ($slotCounts.Count -ne $Cases.Count) { throw 'One or more replay cases have no Git census window.' }
    $slotCounts
}

function Read-ProbeStarts {
    param([string]$Path, [hashtable]$Slots, [object[]]$Git)
    $native = @{}
    foreach ($node in $Git) { $native["$($node.pid)/$($node.start_filetime)"] = $node }
    $seen = [Collections.Generic.HashSet[string]]::new()
    $probeCalls = @{}
    $countsByTest = @{}
    $lines = if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path }
    $matched = @(foreach ($line in $lines) {
        $row = ConvertFrom-Json -AsHashtable $line
        $key = "$($row.child_process_id)/$($row.child_start_filetime)"
        $node = $native[$key]
        $slot = $Slots[$row.test_name]
        if ($null -eq $node -or $null -eq $slot -or
            $row.process_id -ne $node.parent_pid -or $row.process_id -ne $slot.process_id -or
            -not $seen.Add($key)) {
            throw "Fixture probe has no unique matching native start and test window ($key, $($row.test_name))."
        }
        $commandKey = "$($row.test_name)|$($row.subcommand)"
        $probeCalls[$commandKey] = [int]$probeCalls[$commandKey] + 1
        if ($probeCalls[$commandKey] -gt [int]$slot.git_subcommands[$row.subcommand]) {
            throw "Fixture probe launches exceed the slot census for $commandKey."
        }
        $countsByTest[$row.test_name] = [int]$countsByTest[$row.test_name] + 1
        $caller, $parentCaller = $row.caller -csplit '<-', 2
        [pscustomobject]@{
            sequence = $node.sequence
            test_name = $row.test_name
            caller = $caller
            parent_caller = $parentCaller
            subcommand = $row.subcommand
            arguments = $row.arguments
        }
    })
    foreach ($name in $Slots.Keys) {
        if ([int]$countsByTest[$name] -ne $Slots[$name].git_fixture_probe_launches) {
            throw "Fixture probe starts do not reconcile with the slot census for $name."
        }
    }
    $matched
}

function Invoke-ReplaySelection {
    param([string]$Family, [string]$Class, [string]$Filter, [string[]]$Methods)
    $directory = Join-Path $outputRoot $Family
    [void][IO.Directory]::CreateDirectory($directory)
    $etl = Join-Path $directory 'process.etl'
    $log = Join-Path $directory 'runner.log'
    $staging = Initialize-MtpResultsDirectory -RunLabel 'fixture-git'
    $results = Join-Path $staging 'tests'
    $slotFile = Join-Path $staging 'slot.jsonl'
    $probeFile = Join-Path $staging 'probes.jsonl'
    $command = @'
param($Runner, $Project, $Configuration, $Filter, $Results, $Slots, $Probes, $Log)
& $Runner -Target $Project -Configuration $Configuration -NoBuild -Filter $Filter -ResultsRoot $Results -SlotTimingFile $Slots -TestInput "MCG_TEST_INPUT_FIXTURE_GIT_PROBE_TRACE=$Probes" *> $Log
exit $LASTEXITCODE
'@
    $values = $runner, $project, $Configuration, $Filter, $results, $slotFile, $probeFile, $log
    $literals = ($values | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ' '
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("& {`n$command`n} $literals"))
    $session = 'McgFixtureGit-' + [Guid]::NewGuid().ToString('N')
    $started = [DateTimeOffset]::UtcNow
    try {
        Invoke-CheckedNative logman.exe @('create', 'trace', $session, '-ets', '-p', $provider, '0x10', '4',
            '-o', $etl, '-f', 'bin', '-max', [string]$traceLimitMiB) | Out-Null
        try {
            $replay = [FixtureGitReplayWindows]::Run($pwsh, $encoded, $repo, $timeoutSeconds)
        }
        finally {
            try { $loss = [FixtureGitReplayWindows]::StopTrace($session) }
            catch {
                & logman.exe stop $session -ets | Out-Null
                throw
            }
            $stopped = [DateTimeOffset]::UtcNow
        }
    }
    finally {
        foreach ($artifact in $results, $slotFile, $probeFile) {
            if (Test-Path -LiteralPath $artifact) {
                Copy-Item -LiteralPath $artifact -Destination $directory -Recurse -Force
            }
        }
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    Write-JsonFile (Join-Path $directory 'capture.json') ([ordered]@{
        job = $replay; loss = $loss; trace_start_utc = $started; trace_end_utc = $stopped
        trace_provider = $provider; trace_limit_mib = $traceLimitMiB; job_timeout_seconds = $timeoutSeconds
    })
    if ($replay.ExitCode -ne 0) { throw "$Family runner exited $($replay.ExitCode). See $log." }
    if ($loss.EventsLost -ne 0 -or $loss.FileBuffersLost -ne 0 -or $loss.RealTimeBuffersLost -ne 0 -or
        (Get-Item -LiteralPath $etl).Length -ge $traceLimitMiB * 1MB) {
        throw "ETW lost events or reached its size bound for $Family. See $directory."
    }

    $tree = @(Read-TraceTree $etl $replay)
    $git = @($tree | Where-Object { [IO.Path]::GetFileName($_.image) -match '^git(?:-.+)?\.exe$' })
    $cases = @(Read-ReplayCases (Join-Path $directory 'tests') $Class $Methods)
    $slots = Read-SlotCensus (Join-Path $directory 'slot.jsonl') $cases $tree
    $probes = @(Read-ProbeStarts (Join-Path $directory 'probes.jsonl') $slots $git)
    $probeSequences = @($probes | ForEach-Object { $_.sequence })
    $insideGitTree = [Collections.Generic.HashSet[string]]::new()
    $nativeRows = @(foreach ($node in $tree) {
        $belowGit = $insideGitTree.Contains($node.parent_sequence)
        if ($node -in $git) {
            $origin = if ($node.sequence -in $probeSequences) { 'shared-probe' }
            elseif ($belowGit) { 'git-child' }
            else { 'unattributed' }
            [pscustomobject]@{ process = $node; origin = $origin }
            [void]$insideGitTree.Add($node.sequence)
        }
        elseif ($belowGit) { [void]$insideGitTree.Add($node.sequence) }
    })
    $callers = @($probes | Group-Object test_name, caller, parent_caller, subcommand | ForEach-Object {
        $row = $_.Group[0]
        [pscustomobject]@{
            family = $Family; test_name = $row.test_name; caller = $row.caller
            parent_caller = $row.parent_caller; subcommand = $row.subcommand; count = $_.Count
        }
    })
    Write-JsonFile (Join-Path $directory 'process-tree.json') $tree
    Write-JsonFile (Join-Path $directory 'native-git.json') $nativeRows
    Write-JsonFile (Join-Path $directory 'shared-probe-callers.json') $callers
    Write-JsonFile (Join-Path $directory 'cases.json') $cases
    $unattributed = @($nativeRows | Where-Object origin -eq 'unattributed').Count
    [ordered]@{
        family = $Family
        filter = $Filter
        executed_cases = $cases.Count
        skipped_cases = 0
        native_git = $git.Count
        shared_probe = $probes.Count
        git_child = @($nativeRows | Where-Object origin -eq 'git-child').Count
        unattributed = $unattributed
        unattributed_percent = if ($git.Count -eq 0) { 0 } else { 100.0 * $unattributed / $git.Count }
        slotted_other = ($slots.Values | ForEach-Object { $_.git_callers['other'] } | Measure-Object -Sum).Sum
        cpu_100ns = $replay.UserCpu100ns + $replay.KernelCpu100ns
        cpu_seconds = ($replay.UserCpu100ns + $replay.KernelCpu100ns) / 1e7
    }
}

$source = Invoke-CheckedNative git.exe @('-C', $repo, 'rev-parse', 'HEAD')
if (Invoke-CheckedNative git.exe @('-C', $repo, 'status', '--porcelain')) { throw 'Start replay from a clean candidate checkout.' }
foreach ($ancestor in $integrationRevision, $exclusionTip, $censusRevision) {
    Invoke-CheckedNative git.exe @('-C', $repo, 'merge-base', '--is-ancestor', $ancestor, $source)
}
$origin = Invoke-CheckedNative git.exe @('-C', $repo, 'remote', 'get-url', 'origin')
$gitVersion = Invoke-CheckedNative git.exe @('--version')
$dotnetVersion = Invoke-CheckedNative dotnet.exe @('--version')
$outputRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a new fixture Git evidence directory for each arm.' }
[void][IO.Directory]::CreateDirectory($outputRoot)
if (-not ('FixtureGitReplayWindows' -as [type])) { Add-Type -TypeDefinition (Get-Content -Raw -LiteralPath $nativeSource) }

$worktree = 'GoalWorktreeTestsRebaseMergeNoHistoryRewrite'
$factory = 'WorkerDispatchTestsSeededRepositoryFactoryTests'
$factoryMethod = 'Create_MultipleCopies_ProducesIndependentCommittedRepositories'
$selections = @(
    Invoke-ReplaySelection 'goal-worktree' $worktree "FullyQualifiedName=$worktree" @(
        'MergeIntegratedHistory_PreservesEarlierGoalCommitsAndExactTree'
        'ExistingSquashPath_NegativeControlDropsReviewedHeadFromAncestry'
        'OverlappingConflict_AbortsAndLeavesBranchAndCheckoutUnchanged'
        'AlreadyContainsMain_LeavesBranchUnchanged'
        'DirtyWorktree_PreservesBranchAndUncommittedContent'
        'AdditiveConflict_MergesWithOriginalHeadAsFirstParent'
        'MissingBranch_ReturnsRecoveryWithoutCreatingBranch'
        'MissingWorktree_LeavesExistingBranchUnchanged'
    )
    Invoke-ReplaySelection 'seeded-factory' $factory "FullyQualifiedName=$factory&Name~$factoryMethod" @($factoryMethod)
)
[long]$totalCpu100ns = 0
[int]$totalNativeGit = 0
foreach ($selection in $selections) {
    $totalCpu100ns += $selection.cpu_100ns
    $totalNativeGit += $selection.native_git
}
$artifacts = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -Force -File | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($outputRoot, $_.FullName)
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash
    }
})
$receipt = Join-Path $outputRoot 'replay.json'
Write-JsonFile $receipt ([ordered]@{
    schema = 1; revision = $source; repository = $repo; origin = $origin
    candidate_parents = (Invoke-CheckedNative git.exe @('-C', $repo, 'log', '-1', '--format=%P'))
    measurement_runner_sha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    measurement_native_sha256 = (Get-FileHash -LiteralPath $nativeSource).Hash
    census_revision = $censusRevision; historical_probe_entries = 189105; historical_verifier_attempts = 33
    pinned_fixture_revision = $integrationRevision; shipped_exclusion_tip = $exclusionTip
    shipped_exclusion_goal = '69d0be49'
    configuration = $Configuration; os = [Environment]::OSVersion.VersionString
    git = $gitVersion; dotnet_sdk = $dotnetVersion; powershell = $PSVersionTable.PSVersion.ToString()
    boundary = 'suspended launcher creation through all contained processes exiting'
    cpu_100ns = $totalCpu100ns
    cpu_seconds = $totalCpu100ns / 1e7
    native_git = $totalNativeGit
    selections = $selections; artifacts = $artifacts
})
Write-Output "Fixture Git replay receipt $receipt"
