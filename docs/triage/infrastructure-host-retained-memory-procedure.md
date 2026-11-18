# Operator procedure: Infrastructure host memory

Use with the [report](infrastructure-host-retained-memory.md) for backlog
`55a676fbafb74804a90155aa5ea8a443`. Driver runs the workload and diagnostic tools;
Developer incorporates receipts into the report. This procedure does not change
cache policy, GC settings, build admission, or the isolated runner.

## Pin the experiment

1. Attempt recovery of the October 26 `infrastructure-tests-remainder` manifest,
   command, TRX and runtime receipts from retained operator records. Record searched
   locations and unavailable identities. If selection or parallelism is unrecoverable,
   label October descriptive and use the adjacent revisions in the report.
2. Use separate detached worktrees for those full SHAs on this Windows host. Execute
   sequentially on the same SDK/runtime, architecture and concurrency. Record CPU,
   RAM, OS build, dotnet path, `dotnet --info`, runtimeconfig, dependency versions,
   effective GC configuration, and inherited `DOTNET_GC*`/`COMPlus_GC*` values without
   changing them. Preserve the same runner configuration and tool versions across arms.
3. For the fallback, use the filter below, selecting only classes present in both
   revisions. Record the resulting discovered and executed test names, including
   theory rows, from runner receipts/TRX; a filter string alone is insufficient.
   Do not include newly added cache tests in only one arm. Record runner parallelism
   from the actual xUnit configuration/defaults and loaded runner version; build
   parallelism is not test parallelism. Name this experiment `matched-cache-consumers`,
   not the recovered historical lane.
4. Install the identical temporary observation fixture below into each private
   measurement worktree, then build through each revision's existing isolated entry point.
   Retain its artifact-owner receipt, assembly hash, assembly revision, and the full
   command. For repeated runs reuse only that revision's validated artifacts. Never
   copy binaries from the other arm. These are instrumented builds; record the fixture
   hash and worktree diff alongside the base revision. Record the source corpus seen by guards:
   snapshots enumerate `src` and `tests` recursively, including any generated `.cs`
   present there. Retain relative-path/hash manifests and disclose corpus changes.

### Establish the assembly teardown boundary

The driver used `MatchedHostObservation.cs` from the report's durable bundle,
SHA-256 `beb66eb7be0e597cc8c49b662c2407967753785840b00f1ee5d146e749ce0578`.
Copy that file into `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/` in each
disposable measurement worktree. Verify identical bytes before building. Keep it
out of the landing diff. If using another fixture, record and justify the new
instrumentation and use it identically in both arms; do not call it the same method.

This xUnit assembly fixture implements `IAsyncDisposable`. Its constructor writes
`start.json` with PID/start/image/loaded DLL/arguments/runtime/server-GC/processors
and starts a nominal 250 ms process-memory timer. On assembly teardown, after all
selected tests complete, it samples once, then atomically publishes `checkpoint.json`
with UTC/PID/start/WS/OS peak/gen2 count. It waits for a per-run `release` file,
with a three-minute safety limit, then writes `released.json` with UTC/gen2 count
and disposes the timer. The release file is the event gate; elapsed time never
establishes workload completion. The timer interval is sampling cadence only.

The exact fixture source and `board9-memory.py` are retained with the report's
receipts. The Python file documents the original command sequence; its `repo`/tool
paths depend on its original location, so do not run a relocated copy unchanged.
The PowerShell commands here use explicit evidence/worktree locations. The fixture
is test-only diagnostic instrumentation, not a production environment setting or
a proposed persistent test hook. Do not reuse a run directory or stale release file.

Run the following from each worktree in its own PowerShell session. Set `$run` to a
fresh absolute directory in durable operator evidence storage (outside the repo).
Use three fresh hosts per arm, alternating before/after, with identical selection
and configuration. If discovery differs, resolve to a common selection and rerun
both arms; otherwise mark non-comparable. No GC tuning is part of this experiment.

```powershell
$run = Read-Host 'Fresh absolute evidence directory for this arm/repeat'
if (-not [IO.Path]::IsPathFullyQualified($run) -or (Test-Path -LiteralPath $run)) {
    throw 'Use a fresh absolute evidence directory'
}
New-Item -ItemType Directory -Path $run -ErrorAction Stop | Out-Null
$revision = git rev-parse HEAD
$revision | Set-Content (Join-Path $run 'revision.txt')
dotnet --info > (Join-Path $run 'dotnet-info.txt')
$filter = 'FullyQualifiedName~ApplicationLayerArchitectureTests|FullyQualifiedName~CanaryEngineSurfaceCoverageTests|FullyQualifiedName~CliReadRouteCapabilityArchitectureTests|FullyQualifiedName~ConductorCliExecutorArchitectureTests|FullyQualifiedName~ConsoleCliExecutorArchitectureTests|FullyQualifiedName~ExecutionWorkspacesReferenceGuardTests|FullyQualifiedName~OrchestrationCliCommandViewArchitectureTests|FullyQualifiedName~RuntimeAssemblyArchitectureTests|FullyQualifiedName~SourceSnapshotStructureTests'
$filter | Set-Content (Join-Path $run 'filter.txt')
$project = 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj'
# Build only: do this before the observation interval.
.\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix 1834ee27 build $project --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Build failed; retain output and do not measure' }
# Fresh host, validated reuse of this arm's build. Keep TRX even on success.
$oldObservationDirectory = $env:HOST_MEMORY_OBSERVATION_DIRECTORY
try {
    $env:HOST_MEMORY_OBSERVATION_DIRECTORY = $run
    .\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix 1834ee27 -ReuseArtifacts test $project --no-build --filter $filter --results-directory $run --report-trx --report-trx-filename lane.trx --output Detailed 2>&1 | Tee-Object (Join-Path $run 'lane.log')
    $LASTEXITCODE | Set-Content (Join-Path $run 'lane.exit.txt')
} finally {
    $env:HOST_MEMORY_OBSERVATION_DIRECTORY = $oldObservationDirectory
}
```

Use a distinct goal prefix per measurement worktree (the recorded runs used
`1834eea1` before and `1834eea2` after); keep it the same for that arm's build/reuse.
The command uses managed MTP execution through the existing isolated entry point,
not raw `dotnet test` or an apphost. Before collecting, check the pinned revision's
runner help for `--output Detailed` and TRX options. Retain that help and the exact
accepted command. An unsupported option is an apparatus failure, not memory evidence.
If a pinned runner lacks detailed completion events, use its diagnostic event log;
record the exact enabled option and event mapping in both arms.

## Bind the host and sample working set

Have observer sessions ready before starting the test command. From the isolated
runner's managed assembly path and process tree identify the **test host**, not the
build process, wrapper or a test-spawned child. Retain its command line, parent chain,
image path, loaded test DLL path/hash and revision/owner receipt. The dotnet image
alone does not identify the loaded test assembly. In each observer session set
`$run` to the same directory and `$hostId` to that PID, then run this identity block:

```powershell
$run = Read-Host 'Existing absolute evidence directory for this arm/repeat'
$hostId = [int](Read-Host 'Confirmed managed Infrastructure test-host PID')
$bound = Get-Process -Id $hostId -ErrorAction Stop
$started = $bound.StartTime.ToUniversalTime()
$image = $bound.Path
function Get-BoundHost {
    $p = Get-Process -Id $hostId -ErrorAction Stop
    if ($p.StartTime.ToUniversalTime() -ne $started -or $p.Path -ne $image) {
        throw 'Test-host identity changed'
    }
    return $p
}
[ordered]@{ pid = $hostId; startUtc = $started.ToString('o'); image = $image } |
    ConvertTo-Json | Set-Content (Join-Path $run 'host-identity.json')
Get-CimInstance Win32_Process -Filter "ProcessId = $hostId" |
    Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine |
    ConvertTo-Json | Set-Content (Join-Path $run 'host-command.json')
```

The fixture supplies `samples.csv` without an external attachment delay after its
constructor runs. Its columns, without a header, are UTC, working-set bytes,
OS peak-working-set bytes, private bytes. Retain its actual coverage and pauses;
it stops on fixture release rather than host exit. The observed first sample is
about 1.6 seconds after process start. Do not combine this file with the optional
external sampler's different schema below. The latter can extend coverage to exit.

In an optional working-set observer, append timestamped **individual-host** byte samples
until exit. The interval is measurement cadence, not a test-completion condition.
Record any late attachment: a run missing startup coverage yields only a partial
peak. Do not relabel a sampled maximum as an exact continuous-time maximum.

```powershell
$csv = Join-Path $run 'working-set.csv'
while ($true) {
    try { $p = Get-BoundHost } catch {
        $_.Exception.Message | Set-Content (Join-Path $run 'sampling-end.txt')
        break
    }
    $p.Refresh()
    [pscustomobject]@{
        utc = [DateTime]::UtcNow.ToString('o'); pid = $hostId
        startUtc = $started.ToString('o'); workingSetBytes = $p.WorkingSet64
        peakWorkingSetBytes = $p.PeakWorkingSet64; privateBytes = $p.PrivateMemorySize64
    } | Export-Csv -LiteralPath $csv -Append -NoTypeInformation
    Start-Sleep -Milliseconds 250
}
```

Keep OS-reported peak working set separate from the maximum sampled value. A
per-process peak counter has no timestamp and may already include a diagnostic
capture. Private committed bytes are not resident working-set bytes.

## Counters and workload checkpoint

In a second bound observer record installed tool versions/help and collect
System.Runtime at one-second cadence before the cache-consumer interval. If the
tool is absent, driver provisions a pinned version in operator tool storage and
uses that same version in both arms; record the exact unavailable metric if any.
The original driver used an explicitly located counters executable and the user's
gcdump tool; the bundle lacks their version receipts. Future runs must capture them
rather than infer their versions from a later installation.

```powershell
Get-BoundHost | Out-Null
dotnet-counters --version > (Join-Path $run 'counters-version.txt')
dotnet-counters collect --help > (Join-Path $run 'counters-help.txt')
dotnet-counters collect --process-id $hostId --refresh-interval 1 --counters System.Runtime --format csv --output (Join-Path $run 'runtime.csv')
```

Keep working-set and counters observers running. In a third bound observer wait
for **assembly-fixture-teardown**: the fixture's atomically published
`checkpoint.json`. Verify its PID/start match the bound host; retain the TRX and
completion events afterward to verify the exact completed test set in both arms.
This is the actual driver checkpoint, replacing the earlier proposal to race host
exit using runner output alone. No fixed delay or early heap substitutes for it.

Establish the gate in a pilot before repeats. If `released.json` appears before
capture finishes, the three-minute safety limit may have expired: reject the
checkpoint capture unless the receipt proves it completed before release. Preserve
failed runs and rerun matched arms with the same method. Do not hold a host forever.

```powershell
Get-BoundHost | Out-Null
while (-not (Test-Path (Join-Path $run 'checkpoint.json'))) {
    Get-BoundHost | Out-Null
    Start-Sleep -Milliseconds 100
}
$checkpoint = Get-Content (Join-Path $run 'checkpoint.json') -Raw | ConvertFrom-Json
if ($checkpoint.pid -ne $hostId -or [DateTime]::Parse($checkpoint.start).ToUniversalTime() -ne $started) {
    throw 'Checkpoint belongs to another host'
}
if (Test-Path (Join-Path $run 'released.json')) { throw 'Checkpoint already released' }
dotnet-gcdump --version > (Join-Path $run 'gcdump-version.txt')
$captureStart = [DateTime]::UtcNow
try {
    Get-BoundHost | Out-Null
    dotnet-gcdump collect -v --process-id $hostId --timeout 120 --output (Join-Path $run 'heap.gcdump') > (Join-Path $run 'capture.log') 2>&1
    $captureExit = $LASTEXITCODE
    $captureEnd = [DateTime]::UtcNow
    $releasedEarly = Test-Path (Join-Path $run 'released.json')
} finally {
    # Release this host even when diagnostics fail. Keep its failure receipts.
    New-Item -ItemType File -Path (Join-Path $run 'release') -Force | Out-Null
}
[ordered]@{ startUtc = $captureStart.ToString('o'); endUtc = $captureEnd.ToString('o'); exit = $captureExit } |
    ConvertTo-Json | Set-Content (Join-Path $run 'capture.json')
if ($releasedEarly) { throw 'Host released before capture completion' }
if ($captureExit -ne 0) { throw 'Capture incomplete; retain logs' }
dotnet-gcdump report (Join-Path $run 'heap.gcdump') > (Join-Path $run 'heapstat.txt')
if ($LASTEXITCODE -ne 0) { throw 'Heap report failed; retain dump' }
```

The identity check narrows PID reuse risk but PID attachment is not atomic with
validation. Preserve process start/exit evidence through attachment and reject any
capture with ambiguous identity. If the host exits, do not attach to its successor.
Let tools finish normally on host exit; if stopping counters manually, use its
interactive stop and verify the CSV was finalized. Retain each tool's exit status.

Gcdump triggers a full generation-2 GC. Mark its start/end on the working-set
timeline and show GC count/time changes from the same host's counters; overlapping
activity alone cannot identify which GC a tool induced. Missing per-GC events limit
that association. Compare capture-bearing runs to capture-bearing runs; use separate
matched runs without dumps to characterize undisturbed peaks if needed. Do not mix
an undisturbed peak with a post-capture heap and call the subtraction explained.
These tool contracts follow [Microsoft's gcdump documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump)
and [counters documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters).

## Analyze and return the evidence

Retain the consumer list used for this revision (source inventory, not runtime proof):

```powershell
rg -n 'SharedArchitectureCache\.Get\(|RepositorySourceSnapshotCache\.Get\(' tests/Mcg.AgentOrchestrator.Infrastructure.Tests -g '*.cs' -g '!**/bin/**' -g '!**/obj/**' > (Join-Path $run 'consumers.txt')
Get-FileHash (Join-Path $run 'heap.gcdump'),(Join-Path $run 'lane.trx'),(Join-Path $run 'runtime.csv') |
    Export-Csv (Join-Path $run 'artifact-hashes.csv') -NoTypeInformation
```

For each of the three runs per arm return the command and exit, workload identities,
runtime/host/configuration, process identity, timeline, checkpoint events, counter
units, capture interval and hashes. Include captured GC mode/heap count and any
unavailable counters. Preserve raw units: convert decimal MB with 1,000,000 and MiB
with 1,048,576; do not assume the historical MB convention.

Compute sampled peak `max(workingSetBytes)` with its UTC time; report observation
coverage and dump intervals alongside it. Separate the OS peak recorded at teardown
from subsequent capture-bearing sampled maxima. Get live object bytes only from a
validated **complete** gcdump, not a generic heap-size counter. Check verbose logs
for node/edge truncation even when collect/report exit zero. In this experiment all
graphs hit the 10,000,000-node cap; graph totals cannot support complete live bytes
or retained-owner analysis. Retain that limit rather than silently substituting them.
For the available counter comparison, sum last-collection heap-size bytes over
gen0/gen1/gen2/LOH/POH and subtract the corresponding fragmentation sum. Name this
`last-GC occupied`, with counter timestamps and freshness limits; it is not a live
object census. `board9-summary.py` records the original arithmetic and local-to-UTC
conversion. Pre-checkpoint buckets precede the checkpoint's whole second; end
buckets are the last available observations. Report GC committed bytes separately.
Show checkpoint values and min/max spread for each arm, plus after-minus-before
byte and percentage deltas only for comparable metrics/checkpoints.

For a complete dump, open it in PerfView/Visual Studio and retain root-path/retained-union exports
for the report's owners: architecture registry, separate runtime model, snapshot
dictionary, text/line view/Roslyn trees, and caller-owned strings. Distinguish shallow
type totals from retained graphs; deduplicate overlapping reachable objects. If
the tool cannot attribute the ownership union, report that limit rather than adding
type totals. A live rooted graph alone does not prove its bytes are avoidable.

Keep two accounts: (a) live managed object bytes partitioned into attributed and
unattributed objects, and (b) resident working-set regions. For (b), return a
timestamped same-process resident-region breakdown from an appropriate Windows
memory diagnostic if available, including its collection method/disturbance and
whether GC segments, mappings, JIT/code, stacks and native allocations can actually
be distinguished. Never subtract GC committed bytes or object sizes from working
set as if they were disjoint resident categories. Quantify unexplained resident
bytes as working set minus **disjoint supported resident categories**; if none can
be established, the entire sampled working set is unattributed in this account.
Report `working set minus live object bytes` only as a descriptive arithmetic gap,
with both timestamps and the explicit non-additive limitation. Unsupported causes
remain unknown; counters alone do not establish a native-memory explanation.

For concurrent impact, sample each identified simultaneous host and sum its values
at common timestamps. Record measured host count; do not multiply one host's peak
by a configured capacity and call it observed. Assess avoidable retention only
after repeat variation and lifetime evidence are known. Return the bundle to the
Developer to populate the report, then link the completed report/revision and
compact findings from the canonical backlog item. Acceptance runs the isolated
environment regression selector from the worker result; no unrelated regression
or cache implementation is required for this documentation slice.
