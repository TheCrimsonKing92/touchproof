# Infrastructure test-host retained memory

Source backlog: `55a676fbafb74804a90155aa5ea8a443`; goal: `1834ee27`.
Report status (2026-11-18): **six matched runs recorded**. Teardown working set
fell by 32.09% and last-GC occupied bytes by 33.43% on the fallback workload.
These are different metrics; the truncated gcdumps do not establish complete live
object size or attributable avoidable cache retention. Retain shared cache lifetime.
Developer incorporated driver receipts; Acceptance and Reviewer verify the report.

## Comparison identity

The historical 2026-10-26 observation was **723 MB live managed heap and 2537 MB
working set**, as reported. The MB convention, exact selection, runner parallelism,
runtime and heap checkpoint are not established in the supplied evidence. These
numbers are descriptive; neither is an established workload peak.

The supplied driver bundle does not recover that attempt's manifest, TRX, runtime
or parallelism. This report therefore uses the authorized adjacent-revision fallback
in the [operator procedure](infrastructure-host-retained-memory-procedure.md).
The historical search locations/results were not supplied; no claim of exhaustive
recovery is made. Today's lane name is not a recovered historical selection.

| Item | Before | After |
| --- | --- | --- |
| Fallback revision | `9439c8b9a06e48778a49183db6936d495ec6ab81` | `bf4100d153041e7a07ce8f310e654a06e2c73ca9` |
| Experiment | `matched-cache-consumers`, three fresh sequential hosts | same selection, three fresh sequential hosts |
| Executed selection | 46 passed, zero failed/skipped per retained TRX | same 46 runner test names, including theory rows |
| Assembly SHA-256 (includes identical observation fixture) | `b9450a53eb94dc891a7b60d148f308e48d99c34a233116101ac9fb58275a0018` | `665c9399bab03ea05b4fe70249a6871cea87b50ef2eb592664f814d43984638a` |
| Source corpus | 5,263 files | 5,267 files |
| Recorded corpus identity (`sourceManifestHash` in environment receipt) | `7f421c0b6241c154a3e98a8ba7b69df9932c9de268cb821be8e7031ce6452ae2` | `84b3c5cf387a837db96c6a925bfb9063e2cfefca0360b40cc9b58855d33a1089` |

These revisions are the cache-fix commit and its parent, verified with
`git rev-list --parents -n 1 bf4100d153041e7a07ce8f310e654a06e2c73ca9`.
The measured assemblies are diagnostic builds of these revisions with the same
temporary assembly fixture; they are not unmodified commit binaries or measurements
of this report's HEAD. The four added corpus files are `SharedArchitectureCache.cs`,
`SharedArchitectureCacheTests.cs`, `SourceSnapshot/SourceSnapshotLineView.cs` and
`SourceSnapshot/SourceSnapshotLineViewTests.cs`. Eight existing consumer/snapshot
files differ (see corpus manifests). New cache test classes were not selected.
The workload is matched; the code and source corpus intentionally differ with the
fix. This is not an October-to-present measurement or a whole-lane estimate.

Both environment receipts identify Windows 11 build 26200, AMD64, 24 logical CPUs,
SDK 10.0.103; each host reports .NET 10.0.12, `serverGc=false`, 24 processors and
`C:\Program Files\dotnet\dotnet.exe`. The machine name, CPU model, RAM, full
runtimeconfig, effective GC heap count and inherited GC variables were not recorded.
The experiment was on the driver's Windows host; identical hardware across runs is
operator-supplied context, not independently fingerprinted by these JSON files.
Both projects declare xUnit MTP v2 3.2.2, TRX extension 2.3.2 and ArchUnitNET 0.13.4.
No parallelism override appears in the retained commands or environment `config:{}`;
the runner uses its defaults. Effective worker count/overlap and loaded runner hash
were not captured, so 24 processors is not a measurement of 24 simultaneous tests.

Selection is the nine class filters in the procedure. `summary.json` in each run
contains all executed names; SHA-256 of UTF-8 sorted passed names joined by LF
(no final LF) is `cb5586cbf9e24b67c35b29dcf842f7a1a57471c12006cd258963a5d11b702209`
in all six runs. Two theory display names contain runner ellipsis; retain TRX
definitions with the summaries rather than claiming those labels recover arguments.

## Checkpoints and measurements

All following memory values are **bytes**. UTC dates are 2026-11-18. The checkpoint
is entry to the identical assembly fixture's `DisposeAsync`, after all selected
tests complete, before release permits host shutdown. The fixture writes
`checkpoint.json` atomically and samples every nominal 250 ms; it does not force GC.
The driver attaches counters after `start.json`, then gcdump after `checkpoint.json`.
`diagnosticEndUtc` is the fixture release time, not an independently recorded dump
start/end interval. The six process identities are bound by PID, start time, image
and loaded assembly path/hash in `start.json` and the per-arm assembly hashes above.

| Run directory | PID | Process start UTC | Teardown UTC | Diagnostic release UTC |
| --- | ---: | --- | --- | --- |
| before-1 | 36660 | 15:00:24.9491788 | 15:00:59.5097200 | 15:01:16.4026809 |
| after-1 | 48148 | 15:01:48.8821368 | 15:02:14.6757718 | 15:02:27.2771062 |
| before-2 | 42276 | 15:02:29.1736799 | 15:03:03.5830691 | 15:03:18.6338051 |
| after-2 | 1852 | 15:03:21.0123339 | 15:03:48.5509444 | 15:04:02.7300789 |
| before-3-retry | 41892 | 15:08:24.1775413 | 15:08:55.8615043 | 15:09:11.4353035 |
| after-3-retry | 28796 | 15:09:13.4742701 | 15:09:37.8129858 | 15:09:50.3299738 |

| Run | Teardown working set | OS peak at teardown | Last-GC occupied, before checkpoint | Last-GC occupied, end of capture |
| --- | ---: | ---: | ---: | ---: |
| before-1 | 3,778,895,872 | 3,778,895,872 | 3,553,938,352 | 3,522,851,400 |
| before-2 | 3,770,621,952 | 3,774,046,208 | 3,582,491,944 | 3,522,830,368 |
| before-3-retry | 3,772,383,232 | 3,774,296,064 | 3,559,224,416 | 3,522,869,080 |
| after-1 | 2,566,332,416 | 2,566,332,416 | 2,352,461,568 | 2,345,059,880 |
| after-2 | 2,558,627,840 | 2,558,627,840 | 2,357,729,624 | 2,344,890,528 |
| after-3-retry | 2,564,161,536 | 2,564,161,536 | 2,388,499,960 | 2,345,017,728 |

Last-GC occupied is `sum(dotnet.gc.last_collection.heap.size) -
sum(dotnet.gc.last_collection.heap.fragmentation.size)` across gen0/gen1/gen2/LOH/POH
(counter units `By`). It is an occupied-heap proxy at the last collections, not a
complete census of currently live objects. Pre-checkpoint counter UTC seconds are
15:00:58, 15:03:02, 15:08:54 before, and 15:02:13, 15:03:47, 15:09:36 after.
End counter seconds are 15:01:16, 15:03:18, 15:09:11 before, and 15:02:27,
15:04:02, 15:09:49 after. CSV timestamps are local UTC-06 converted to UTC;
one-second buckets and collection freshness limit temporal precision.

| Metric | Before min–max (spread) | After min–max (spread) | After-minus-before mean; percent |
| --- | --- | --- | --- |
| Teardown working set | 3,770,621,952–3,778,895,872 (8,273,920) | 2,558,627,840–2,566,332,416 (7,704,576) | −1,210,926,421; −32.09% |
| OS peak at teardown | 3,774,046,208–3,778,895,872 (4,849,664) | 2,558,627,840–2,566,332,416 (7,704,576) | −1,212,705,451; −32.12% |
| Pre-checkpoint last-GC occupied | 3,553,938,352–3,582,491,944 (28,553,592) | 2,352,461,568–2,388,499,960 (36,038,392) | −1,198,987,853; −33.63% |
| End last-GC occupied | 3,522,830,368–3,522,869,080 (38,712) | 2,344,890,528–2,345,059,880 (169,352) | −1,177,860,904; −33.43% |

Means/deltas are rounded to whole bytes after arithmetic; percentages use unrounded
means. Observed arm differences exceed these within-arm ranges, without establishing
statistical confidence or an avoidable-retention quantity. Only one experiment host
was measured at a time; no concurrent-host sum or fleet extrapolation is established.

### Sampling disturbance and incomplete graphs

| Run | Sample interval UTC | Maximum sampled WS | Maximum UTC | Gcdump graph bytes / objects |
| --- | --- | ---: | --- | --- |
| before-1 | 15:00:26.5227659–15:01:16.1686907 | 4,851,298,304 | 15:01:09.6654522 | 689,634,536 / 10,000,001 |
| before-2 | 15:02:30.7655260–15:03:18.5097255 | 4,861,607,936 | 15:03:11.9996320 | 712,870,058 / 10,000,000 |
| before-3-retry | 15:08:25.7468182–15:09:11.3276457 | 4,863,631,360 | 15:09:04.5617816 | 713,781,385 / 10,000,051 |
| after-1 | 15:01:50.5103189–15:02:27.2139569 | 3,637,243,904 | 15:02:20.9632338 | 758,680,403 / 10,000,000 |
| after-2 | 15:03:22.6177756–15:04:02.5297470 | 3,632,521,216 | 15:03:54.7759461 | 758,505,306 / 10,000,000 |
| after-3-retry | 15:09:15.1453272–15:09:50.2988723 | 3,636,240,384 | 15:09:43.7888618 | 759,990,867 / 10,000,004 |

These maxima include diagnostics; they are not undisturbed lane peaks. Sampling
begins roughly 1.6 seconds after process start and ends before host exit. OS peaks
at teardown cover earlier process history but have no peak timestamp. Timers can
pause during GC, so nominal cadence does not guarantee 250 ms coverage. Diagnostic
sampled-peak ranges are 4,851,298,304–4,863,631,360 before (spread 12,333,056) and
3,632,521,216–3,637,243,904 after (spread 4,722,688).

Each fixture records one additional gen2 collection between checkpoint and release.
Verbose retry capture logs identify an induced non-background gen2 GC. The capture
protocol and those events support sampling-induced GC; aggregate deltas alone do
not uniquely identify every GC. Captures in both arms use the same protocol.
The retry logs warn `Exceeded max node count 10000000` and report extra edge data.
All six graphs reach that cap (minor overshoot is visible). A successful file write
and report exit therefore do not mean a complete graph. The apparent graph-byte
increase after the fix is not a valid live-heap comparison. Complete live-object
bytes, retained unions and root attribution remain unavailable.

The original `before-3` (PID 52156, start 15:04:04.9376622, checkpoint
15:04:40.0920273, release 15:05:57.3756316) passed 46 tests but its capture log says
`Failed to collect gcdump`. The mechanism and numeric capture exit were not retained
in the supplied summary; do not diagnose a timeout or contention. It is excluded
from the six-run comparison. Both third arms were then run afresh as `*-3-retry`;
there is no original `after-3` in this bundle.

### Working-set accounting

Descriptive teardown-WS minus preceding last-GC-occupied gaps are respectively
224,957,520; 188,130,008; 213,158,816 bytes before, and
213,870,848; 200,898,216; 175,661,576 bytes after (repeat order above).
For a closer end-of-capture comparison, the nearest WS sample to each final counter
bucket gives:

| Run | WS sample UTC | WS bytes | WS minus end last-GC occupied |
| --- | --- | ---: | ---: |
| before-1 | 15:01:15.9207857 | 3,776,000,000 | 253,148,600 |
| before-2 | 15:03:18.0155007 | 3,787,784,192 | 264,953,824 |
| before-3-retry | 15:09:11.0625162 | 3,788,484,608 | 265,615,528 |
| after-1 | 15:02:26.9660032 | 2,564,591,616 | 219,531,736 |
| after-2 | 15:04:02.0429924 | 2,562,904,064 | 218,013,536 |
| after-3-retry | 15:09:49.0418617 | 2,572,673,024 | 227,655,296 |

These are arithmetic gaps between overlapping, differently timed metrics, not native
allocation estimates or an explained partition. No disjoint resident-region evidence
was supplied: **the entire teardown working set remains unattributed** in the
resident account (3,770,621,952–3,778,895,872 before;
2,558,627,840–2,566,332,416 after). Accounted resident categories: none established.
The managed account has no supported owner partition either. GC committed bytes
(end ranges 3,627,180,032–3,644,358,656 before and
2,416,918,528–2,430,365,696 after) are not resident bytes and cannot close that gap.
Roslyn/Cecil type entries demonstrate presence only; JIT, stacks, mapped files and
native allocations have not been quantified as causes of the residual.

## Consumer and lifetime inventory

This preserves the supplied research inventory, checked against current source.
Paths below are relative to `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/`.

| Owner / source | Consumers and lifetime | What capture must distinguish |
| --- | --- | --- |
| `SharedArchitectureCache.cs:14,23` | Static `Process` holds concurrent lazy models keyed by ordered, deduplicated assembly names, for process lifetime. `ApplicationLayerArchitectureTests`, `CanaryEngineSurfaceCoverageTests`, `CliReadRouteCapabilityArchitectureTests`, `ConductorCliExecutorArchitectureTests`, `ConsoleCliExecutorArchitectureTests`, `ExecutionWorkspacesReferenceGuardTests`, `OrchestrationCliCommandViewArchitectureTests` consume them. | Different assembly sets are different models. Count shared reachable objects once, including any Cecil data actually retained. |
| `RuntimeAssemblyArchitectureTests.cs:27` | Separate static lazy five-assembly architecture, shared across this class's tests. | Attribute separately from the shared registry; do not assume either duplicates the other's entire graph. |
| `ConductorStartSharedLaunchTypeTests.cs:14` | Method-local ArchLoader/model in `StartAndSelfRelaunch_CallTheSameHandleListLauncher`; no static cache there. | Local liveness during execution versus persistent roots after completion. |
| `SourceSnapshot/RepositorySourceSnapshotCache.cs:5,11` | Static dictionary retains one lazy snapshot per normalized repository root. Source guards share it across classes; enumerate actual consumers with the procedure's source query. Fixture roots can add separate entries. | Root identity, fixture snapshots and source corpus size; no evidence that one test collection owns every consumer. |
| `SourceSnapshot/RepositorySourceSnapshot.cs:56` | Each SourceFile retains lazy Text, Lines and Tree. `GetText`, `GetLines`, `GetSyntaxTree` materialize only requested forms. | Unmaterialized entries versus text, Roslyn trees and their reachable graphs. |
| `SourceSnapshot/SourceSnapshotLineView.cs:4` | Lines share the source text and an integer offset array; strings are sliced on access. | Shared text once; retained caller-owned line strings separately from the offset view. |

There is no source proof that the end of one xUnit collection ends later or
parallel access. Shared process lifetime remains appropriate absent contrary
ownership evidence. This report changes neither caching nor GC settings.

## Findings and disposition

The fallback supports a repeatable reduction in matched teardown WS and last-GC
occupied bytes, subject to the environment/runner limits above. It does not establish
the requested complete live-heap measurement or explain resident ownership. The
historical 723/2537 MB values remain descriptive, with no percentage or subtraction
against this workload. There is no demonstrated avoidable retained-analysis union,
no proof a collection ends all consumers, and no measured concurrent impact.
Consequently these results justify retaining existing cache lifetime; a lifecycle
change is neither demonstrated beneficial nor authorized. A later proposal needs
complete ownership/liveness evidence and attributable avoidable bytes beyond repeat
spread. No production cache, GC, or build-slot behavior changes accompany this report.

## Evidence locations and receipts

Durable operator bundle (relative to the repository root):
`.orchestrator/operator-evidence/1834ee27-matched-memory`.
The task's `.scratch/driver-memory-evidence` files are delivery copies, not the
canonical store. Canonical backlog identity is `55a676fbafb74804a90155aa5ea8a443`;
the driver supplied that association in retry feedback. This worker did not read or
modify the backlog store. Driver links this report's landed revision there.

Each of the six comparison directories retains `command.json`, `start.json`, `checkpoint.json`,
`released.json`, `samples.csv`, `runtime.csv`, `lane.log`, `lane.trx`, `capture.log`,
`heap.gcdump`, `heapstat.txt`, and (successful runs) `summary.json`. Raw commands
build through `Invoke-IsolatedDotnet.ps1 -GoalPrefix 1834eea1` (before) or `1834eea2`
(after), then `-ReuseArtifacts test <project> --no-build` with the procedure's
filter, `--results-directory <run> --report-trx --report-trx-filename lane.trx
--output Detailed`. `start.json` records the managed DLL command after translation
to `--filter-class`; DLLs live below `%USERPROFILE%\AppData\LocalLow\mcg-dotnet-isolated\goals\<prefix>\artifacts\bin\Mcg.AgentOrchestrator.Infrastructure.Tests\debug`.
Both build logs are retained. `board9-memory.py` checks build/test/capture/report
nonzero exits and checks test-set identity; six complete summaries and TRX corroborate
the successful path. Separate numeric exit receipts, diagnostic tool version receipts,
and runtime artifact-owner receipts are not included in the compact bundle. Do not
infer complete diagnostics or a fresh candidate regression pass from those checks.

| Bundle artifact | SHA-256 |
| --- | --- |
| `matched-summary.json` | `534626b87fe0cfb4c6c7a151700fb6c6a67ebac0e3ae5883808482ee21714395` |
| `MatchedHostObservation.cs` | `beb66eb7be0e597cc8c49b662c2407967753785840b00f1ee5d146e749ce0578` |
| `board9-memory.py` | `f125cb3c9c053b227dfa9f0e5d0d84e39656067b88210d693388ebfbad7ae7b9` |
| `board9-summary.py` | `ed7ba3f95f19bd7dd8249b0bd9d272dce5bbd24a5f158a990cf53c263e774c28` |
| `before-corpus.json` (raw file bytes) | `d649b0242edeef1566ed7f3399dfbff4a60b1933540e09c55c27774a2e7856fd` |
| `after-corpus.json` (raw file bytes) | `2cce6ba9b20f28f71203181506da3c3705d5c7f826754ade0f464ebe479f8202` |

The corpus identities reported by the environment receipts differ from raw manifest
file hashes. Their serialization contract was not supplied; do not use those two
recorded identities as raw-file integrity checks. The raw-file hashes above bind
the inspected relative-path/hash lists.

`matched-summary.json` binds each TRX, counters CSV and heap-stat SHA-256; this round
recomputed all 18 hashes against the durable files, checked six TRX result sets
(46 passed each), and recalculated tables from counters/working-set samples. This
is receipt analysis, not a new workload run. Remaining evidence requests to the
driver are precise: historical search receipt, effective runner/GC configuration
and tool identities, a complete ownership-capable heap capture, and disjoint
resident-region diagnostics if attribution is pursued. Their absence limits the
findings above; it does not license an invented cache-retention diagnosis.
