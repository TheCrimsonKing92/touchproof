<# Prepare an isolated, read-only Activity input. Does not launch a conductor or open live stores. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppDll,
    [string]$ReplayRoot = (Join-Path ([IO.Path]::GetTempPath()) ('activity-detail-' + [guid]::NewGuid().ToString('N')))
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runtime = (Resolve-Path -LiteralPath $AppDll).Path
$root = [IO.Path]::GetFullPath($ReplayRoot)
if (Test-Path -LiteralPath $root) { throw "Replay root must be new: $root" }
$fixturePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TestData/Fixtures/activity-detail-dispatch-rejected.json'
$fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
$state = Join-Path $root '.orchestrator'
$logs = Join-Path $state 'logs'
$null = New-Item -ItemType Directory -Path $logs
$rows = foreach ($record in @($fixture.task_failure, $fixture.latest_matching_escalation)) {
    # Synthetic conduct framing makes the exact persisted message visible without
    # seeding workflow state. Original kind/time/goal remain explicit provenance.
    [ordered]@{
        timestamp = $record.OccurredAt
        eventKind = 'goal-escalation'
        goalId = $record.GoalId
        detail = $record.Message
        replaySourceKind = $record.Kind
        replayContext = 'Synthetic conduct framing; persisted payload and timestamp unchanged'
    } | ConvertTo-Json -Compress
}
[IO.File]::WriteAllText((Join-Path $logs 'conduct-events.log'), ($rows -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
$receipt = [ordered]@{
    RepositoryRoot = $root
    ExpectedStateDirectory = $state
    AppDll = $runtime
    RuntimeSha256 = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
    FixtureSha256 = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
    Context = 'Synthetic conduct framing, no workflow state; original timestamps are not screenshot observation times'
    GoalId = $fixture.task_failure.GoalId
    TaskId = $fixture.task_failure.TaskId
}
[IO.File]::WriteAllText((Join-Path $root 'replay-receipt.json'), ($receipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$receipt | ConvertTo-Json -Compress
