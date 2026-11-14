param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("LiveOwner", "ExitedOwner")]
    [string]$Scenario,
    [Parameter(Mandatory = $true)][string]$GoalId,
    [Parameter(Mandatory = $true)][string]$SeedRepositoryRoot,
    [Parameter(Mandatory = $true)][string]$SeedAttemptName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-FixtureProcessStartInfo {
    param([string]$FileName, [string[]]$ArgumentList, [string]$WorkingDirectory, [hashtable]$Environment = @{})
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FileName
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) { $info.ArgumentList.Add($argument) }
    $info.Environment.Clear()
    foreach ($name in @("PATH", "PATHEXT", "ComSpec", "SystemRoot", "WINDIR", "TEMP", "TMP",
        "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)",
        "ProgramW6432", "COMPUTERNAME", "DOTNET_ROOT", "DOTNET_ROOT_X64")) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($null -ne $value) { $info.Environment[$name] = $value }
    }
    foreach ($name in $Environment.Keys) { $info.Environment[$name] = $Environment[$name] }
    return $info
}

function Invoke-CapturedProcess {
    param([string]$FileName, [string[]]$ArgumentList, [string]$WorkingDirectory, [hashtable]$Environment = @{})
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = New-FixtureProcessStartInfo @PSBoundParameters
    try {
        if (-not $process.Start()) { throw "Process did not start: $FileName" }
        $processId = $process.Id
        $process.StandardInput.Close()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        # Hang-only guard includes the scope tool's cold compilation, as in the free-slot fixture.
        if (-not $process.WaitForExit(270000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Child exit event did not arrive within the hang guard: $FileName"
        }
        return [pscustomobject]@{
            ProcessId = $processId
            ExitCode = $process.ExitCode
            Stdout = $stdoutTask.GetAwaiter().GetResult()
            Stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$helperPath = Join-Path $repositoryRoot "scripts\Invoke-WorkerBuildCheck.ps1"
$tempBase = [System.IO.Path]::GetTempPath()
if ($tempBase -match '[\\/]\.orchestrator-worktrees[\\/]') {
    $tempBase = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Temp"
}
$temporaryRoot = [System.IO.Path]::GetFullPath((Join-Path $tempBase ("mcg-worker-lease-owner-fixture-" + [Guid]::NewGuid().ToString("N"))))
if ($temporaryRoot -match '[\\/]\.orchestrator-worktrees[\\/]') {
    throw "Fixture work directory must resolve to the manual goal prefix."
}
$workDirectory = Join-Path $temporaryRoot "work"
$shimDirectory = Join-Path $temporaryRoot "shim"
$isolatedRoot = Join-Path $temporaryRoot "isolated"
$runRoot = Join-Path $isolatedRoot "goals\manual"
$leaseRoot = Join-Path $runRoot "lease"
$leasePath = Join-Path $leaseRoot "lease.json"
$shellPath = (Get-Process -Id $PID).Path
$owner = $null

try {
    New-Item -ItemType Directory -Force -Path $workDirectory, $shimDirectory, $leaseRoot | Out-Null
    $project = Join-Path $workDirectory "One.csproj"
    Set-Content -LiteralPath $project -Value "<Project />"
    # Only the fixture's throwaway repository is committed.
    foreach ($gitArguments in @(
        ,@("init", "--initial-branch=fixture")
        ,@("add", "One.csproj")
        ,@("-c", "user.name=Build Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "-m", "fixture")
    )) {
        $git = Invoke-CapturedProcess -FileName "git" -ArgumentList $gitArguments -WorkingDirectory $workDirectory
        if ($git.ExitCode -ne 0) { throw "Fixture git setup failed: $($git.Stdout)$($git.Stderr)" }
    }

    $shimScript = @'
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$rawArguments = $env:DOTNET_SHIM_RAW_ARGUMENTS
$fileLoggerMatch = [regex]::Match($rawArguments, '(?i)(?:^|\s)"?-flp:LogFile=(?<path>.+?);Verbosity=normal;Encoding=UTF-8;Append=false"?(?:\s|$)')
if (-not $fileLoggerMatch.Success) { throw "Shim did not receive the file logger argument: $rawArguments" }
[System.IO.File]::WriteAllText($fileLoggerMatch.Groups["path"].Value, "Build succeeded.", [System.Text.UTF8Encoding]::new($false))
exit 0
'@
    Set-Content -LiteralPath (Join-Path $shimDirectory "dotnet-shim.ps1") -Value $shimScript
    $shimCommand = @'
@echo off
set "DOTNET_SHIM_RAW_ARGUMENTS=%*"
if "%~1"=="--version" goto scope
if not "%DOTNET_SHIM_RAW_ARGUMENTS:WorkerBuildScopeTool=%"=="%DOTNET_SHIM_RAW_ARGUMENTS%" goto scope
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0dotnet-shim.ps1"
exit /b %ERRORLEVEL%
:scope
"%MCG_SCOPE_REAL_DOTNET%" %*
exit /b %ERRORLEVEL%
'@
    Set-Content -LiteralPath (Join-Path $shimDirectory "dotnet.cmd") -Value $shimCommand

    $ownerCommand = if ($Scenario -eq "LiveOwner") {
        '[Console]::WriteLine("OWNER_READY"); [Console]::Out.Flush(); $null = [Console]::ReadLine()'
    } else { 'exit 0' }
    $owner = [System.Diagnostics.Process]::new()
    $owner.StartInfo = New-FixtureProcessStartInfo -FileName $shellPath -WorkingDirectory $workDirectory -ArgumentList @(
        "-NoProfile", "-NonInteractive", "-Command", $ownerCommand
    )
    if (-not $owner.Start()) { throw "Lease owner did not start." }
    $seededOwnerId = $owner.Id
    $ownerExitedBeforeRun = $false
    if ($Scenario -eq "LiveOwner") {
        $ready = $owner.StandardOutput.ReadLineAsync()
        if (-not $ready.Wait(30000)) { throw "Lease owner ready event did not arrive within the hang guard." }
        if ($ready.GetAwaiter().GetResult() -ne "OWNER_READY" -or $owner.HasExited) {
            throw "Lease owner did not reach its standard-input gate."
        }
    } else {
        if (-not $owner.WaitForExit(30000)) { throw "Lease owner exit event did not arrive within the hang guard." }
        if ($owner.ExitCode -ne 0) { throw "Lease owner exited unsuccessfully." }
        $owner.Dispose()
        $owner = $null
        $probe = $null
        try { $probe = [System.Diagnostics.Process]::GetProcessById($seededOwnerId) }
        catch [System.ArgumentException] { $ownerExitedBeforeRun = $true }
        finally { if ($null -ne $probe) { $probe.Dispose() } }
        if (-not $ownerExitedBeforeRun) { throw "Exited fixture owner id is still in use before the build check." }
    }

    $seed = [ordered]@{
        version = 1
        goalId = $GoalId
        goalPrefix = "manual"
        leaseId = "goal-manual"
        rootPath = $runRoot
        artifactsPath = Join-Path $runRoot "artifacts"
        ownerProcessId = $seededOwnerId
        machineName = $env:COMPUTERNAME
        lastUsedAt = "2026-11-12T12:00:00.0000000+00:00"
        lastAttemptName = $SeedAttemptName
        staleLockCleared = $false
        reclaimedProcessId = $null
        repositoryRoot = $SeedRepositoryRoot
    }
    [System.IO.File]::WriteAllText($leasePath, ($seed | ConvertTo-Json -Depth 3), [System.Text.UTF8Encoding]::new($false))
    $before = [System.IO.File]::ReadAllBytes($leasePath)
    $helper = Invoke-CapturedProcess -FileName $shellPath -WorkingDirectory $workDirectory -ArgumentList @(
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $helperPath, $project
    ) -Environment @{
        PATH = $shimDirectory + [System.IO.Path]::PathSeparator + $env:PATH
        MCG_DOTNET_ISOLATED_ROOT = $isolatedRoot
        MCG_SCOPE_REAL_DOTNET = @(Get-Command dotnet -CommandType Application -ErrorAction Stop)[0].Source
    }
    Write-Output $helper.Stdout
    if (-not [string]::IsNullOrWhiteSpace($helper.Stderr)) { Write-Output $helper.Stderr }
    $observed = [ordered]@{
        helperExitCode = $helper.ExitCode
        helperProcessId = $helper.ProcessId
        seededOwnerId = $seededOwnerId
        ownerAliveAfterRun = $null -ne $owner -and -not $owner.HasExited
        ownerExitedBeforeRun = $ownerExitedBeforeRun
        beforeBase64 = [Convert]::ToBase64String($before)
        afterBase64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($leasePath))
    }
    Write-Output ("OBSERVED fixture: " + ($observed | ConvertTo-Json -Compress))
}
finally {
    if ($null -ne $owner) {
        try {
            if (-not $owner.HasExited) { $owner.Kill($true) }
            $owner.WaitForExit()
        }
        finally { $owner.Dispose() }
    }
    # Verify the resolved absolute target before recursive cleanup.
    $cleanupPath = [System.IO.Path]::GetFullPath($temporaryRoot)
    $expectedParent = [System.IO.Path]::GetFullPath($tempBase).TrimEnd('\', '/')
    if ((Split-Path -Parent $cleanupPath) -ne $expectedParent -or
        (Split-Path -Leaf $cleanupPath) -notmatch '^mcg-worker-lease-owner-fixture-[a-f0-9]{32}$') {
        throw "Fixture cleanup target is outside its temporary root: $cleanupPath"
    }
    Remove-Item -LiteralPath $cleanupPath -Recurse -Force -ErrorAction SilentlyContinue
}
