# Infrastructure test-host retained memory

Source backlog: `55a676fbafb74804a90155aa5ea8a443`; goal: `1834ee27`.
Report status (2026-11-18): **measurement pending**. The driver has been asked for
paired captures and the retained October lane receipts. This is the procedure and
source assessment, not a published measurement result. Developer maintains this
report; driver supplies execution receipts; Acceptance and Reviewer verify it.

## Comparison identity

The historical 2026-10-26 observation was **723 MB live managed heap and 2537 MB
working set**, as reported. The MB convention, exact selection, runner parallelism,
runtime and heap checkpoint are not established in the supplied evidence. These
numbers are descriptive; neither is an established workload peak.

First recover that attempt's lane manifest, TRX, runtime and command receipts.
Otherwise use the matched adjacent revision experiment in the
[operator procedure](infrastructure-host-retained-memory-procedure.md). Do not
substitute today's lane of the same name for the historical selection.

| Item | Before | After |
| --- | --- | --- |
| Fallback revision | `9439c8b9a06e48778a49183db6936d495ec6ab81` | `bf4100d153041e7a07ce8f310e654a06e2c73ca9` |
| Lane, exact executed test identities, concurrency | pending driver | pending driver |
| Machine, SDK/runtime, architecture, effective GC configuration | pending driver | pending driver |
| Attempt, PID, start UTC, image, loaded assembly hash/revision | pending driver | pending driver |
| Checkpoint identity and capture interval UTC | pending driver | pending driver |
| Peak observed working set, bytes; observed interval/coverage | pending driver | pending driver |
| Checkpoint live heap, bytes; dump-induced GC evidence | pending driver | pending driver |
| Repeated checkpoint values and min/max spread | pending driver | pending driver |
| Retained analysis union, bytes, with root evidence | pending driver | pending driver |
| Contemporaneous working-set accounting and unexplained bytes | pending driver | pending driver |
| Commands, exits, TRX, counters, dumps, timeline references/hashes | pending driver | pending driver |

These revisions are the cache-fix commit and its parent, verified with
`git rev-list --parents -n 1 bf4100d153041e7a07ce8f310e654a06e2c73ca9`.
Current inspected source revision is `8d6408ac1f6f84afab72822fa190cd3d9e4a270e`;
it is not a measured arm. Record any additional current-revision run separately.
The matched comparison is not an October-to-present measurement.

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

Measured deltas, avoidable retention, GC correlation, and the unexplained residual
are **unknown**, not zero. Historical subtraction is not a present attribution.
Type totals alone cannot establish retained ownership or resident physical bytes.

After the driver supplies the procedure's evidence bundle, replace pending cells
with byte values and references, calculate after-minus-before deltas and repeat
spread, and record a comparable/non-comparable disposition. Evaluate cache release
only if attributable avoidable retention repeats beyond that spread. Quantify
bytes per host and the sum for actually measured concurrent hosts. A proposal
must identify an owner and a lifetime with no later or parallel consumer; otherwise
retain shared lifetime. No fixed materiality threshold or lifecycle implementation
is authorized. Reviewer independently checks any resulting diagnosis before action.

Driver records the finished comparison and this report's revision on the canonical
backlog item. Raw diagnostics live in operator-owned evidence storage, not here or
in `.scratch`. No new live captures or passing candidate tests are claimed.
