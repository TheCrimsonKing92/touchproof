# Hosted verifier QoS caller inventory

Base: `ead4052a508a3ea06aa0e26ac687bb22a51089eb`. Production paths below are under
`src/Mcg.AgentOrchestrator.App/` unless otherwise qualified. Search expressions:
`AcceptanceExecutionRunner.(RunAttempt|RunFocusedVerification)(`, `.RunOwnedAsync(`,
`AcceptanceCheckProcessInvoker.RunAsync(`, and `AcceptanceProcessRunner.RunProcessAsync(`.

`AcceptanceChildQos.Enable()` occurs only in
`Orchestration/ConductorParallelAcceptanceAttempts.OwnedProcessPriority.cs:23,35`.
Those calls execute inside solo and grouped attempt hosts, respectively. This change
leaves both calls, the static flag and its existing tests untouched. Hosted scopes override
eligibility only in their async flow and never call the setter for the hosting process.

| Caller | Host / prior Enable | Disposition |
| --- | --- | --- |
| `Orchestration/SelectedExecutionBackstopGateRunner.cs::Run` | conductor / no | Begin scope from workspace policy; aggregate all command sweeps and log once after cleanup. |
| `Orchestration/SelectedExecutionBackstopFailureRerun.cs::Create` | conductor / no | Inherits the originating runner's scope, policy snapshot and totals; no separate policy read. |
| `Orchestration/ConductorDriver.MainSuspectReleaseProbe.cs::Run` | conductor / no | Begin hosted scope before checkout and focused verification. |
| `Orchestration/MultiRepositoryProjectVerifier.cs::Run` | conductor or operator CLI / no | Covered by `MultiRepositoryAcceptanceGate.Run`; one snapshot covers home and secondary repositories. |
| `Cli/CliMultiRepositoryAcceptance.cs::RunHome` | operator CLI / no | Same gate scope. |
| `Orchestration/ConductorDriver.cs::_runAcceptanceVerification` | conductor multi-repository home / no | Same gate scope, via `ConductorDriver.AcceptanceLanding.cs::ExecuteRepositoryAcceptance`. |
| Same delegate, solo landing | solo attempt / yes | Unchanged; `ConductorParallelAcceptanceAttempts.cs` enables before invoking the driver. Production constructor sets `_parallelAcceptanceEnabled=true` (line 234); the inline solo fallback is for injected drivers. |
| `Cli/CliCommandHandlers.Goals.Acceptance.cs::RunAcceptanceWorkspaceMergeCore` | operator CLI / no | Begin scope around the direct owned verifier invocation. |
| `Orchestration/ConductorDriver.AcceptanceCohortExecution.cs::ExecuteAcceptanceCohortGate` | grouped attempt / yes | Unchanged; entered by `ConductorDriver.GroupedGateAttempts.cs:298`. |
| `Orchestration/ConductorDriver.CohortAttributionStableSlot.cs` | grouped attempt / yes | Unchanged; cohort attribution runs inside that same attempt. |
| `Orchestration/ConductorDriver.CohortFocusedAttribution.cs` | grouped attempt / yes | Unchanged; same attribution ownership. |
| `Orchestration/MergeTrainGateReceiptExecution.cs` | grouped attempt / yes | Unchanged; `ConductorDriver.GroupedGateAttempts.cs:307` enters the train. |
| `Orchestration/MergeTrainPrefixRunner.cs` | grouped attempt / yes | Unchanged; train attribution. |
| `Orchestration/MergeTrainFocusedRecheckRunner.cs` | grouped attempt / yes | Unchanged; train attribution. |
| `Orchestration/MergeTrainMemberRecheckRunner.cs` | grouped attempt / yes | Unchanged; train attribution. |
| `Orchestration/FocusedFindingEvidenceExecutor.cs::Run` | solo focused-evidence attempt / yes | Unchanged; `ConductorParallelAcceptanceAttempts.cs:1485-1493` invokes the focused runner after attempt setup. `RunPreReviewFocusedEvidence` has no production caller. |
| `Orchestration/ManifestCommandBuildCheck.cs::RunCommand` | conductor / no | Both conflict build owners (`ConductorConflictIntegration.ValidateCandidate`, `RatchetConflictDispatchIntegration.IntegrateMainBeforeDeveloperDispatch`) now begin a scope around their build check. `ConductorDriver` passes the explicit workspace state path into both owners. `OperatorIntentCoordinator` passes it into its integration owner too. |
| `Orchestration/PostLandingCanaryCommand.cs` | dedicated canary child / no | Outside the named conductor/operator CLI host scope; unchanged. Entered by `Program.cs`, launched by `PostLandingCanaryRunner`. |
| `Orchestration/ExperimentReplayProcedure.cs` | dedicated replay child / no | Outside the named host scope; unchanged. Entered by `Program.cs`, launched by `ExperimentReplayRunner`; request has no state path. |
| `Infrastructure/Workspaces/AcceptanceProcessRunner.cs::RunProcessAsync` | inherits caller | Wrapper into the actual invoker; no added scope or eligibility decision. |
| `Infrastructure/Workspaces/AcceptanceProcessTestContext.cs` | test only | Existing invoker wrapper; unchanged. |
| `Orchestration/AcceptanceExecutionRunner.cs` | inherits caller | Attempt/focused owner wrapper; unchanged. |

Production conduct entry points enable owned grouped attempts in
`CliCommandHandlers.Goals.cs:1405,1776`. Background cohort/train admission selects the
owned grouped host; the synchronous branches of those methods also serve injected tests
and the grouped child itself. No new scope is installed inside an attempt host.

The only direct test callers of the execution runner are `AttributionOccupancyHeartbeatTests`
(two focused calls), `MultiRepositoryAcceptanceGateFixture` (home attempt), and
`PartialNegativeControlResumeTests` (focused call). They run in test hosts with injected
collaborators and require no production wiring. Matches in
`JobAccountingRunIdentityEvidenceTests` are synthetic source text and source assertions,
not invocations of an executable verifier. Other invoker-name matches in tests are source
inspection or comments.

## Flow and verification

The real verifier reaches `AcceptanceProcessRunner` from `GoalAcceptanceVerifier.cs:3560`,
and the invoker starts its sweep at `AcceptanceCheckProcessInvoker.cs:171` (unchanged).
Batch groups and remote shard methods run as ordinary async methods; structural-coverage
work uses `Task.Run`, which captures execution context. Searches found no `SuppressFlow`,
unsafe queueing, or raw thread dispatch on these verifier lane paths. `ConfigureAwait(false)`
does not suppress execution context. The per-run snapshot therefore reaches each lane's
existing sweep without changing selection, concurrency, priority, or process ownership.

Added test classes:

- `SelectedExecutionBackstopGateRunnerQosTests`: enabled/disabled root and members, exact
  masks, failed setter counts, both successive policy orders, overlapping decisions,
  and one exact summary on success, exception and cancellation.
- `SelectedExecutionBackstopFailureRerunQosTests`: actual automatic focused re-run,
  on-disk policy flipped before re-run, original decision retained and totals summed.
- `HostedVerifierRunQosTests`: policy fallback, scope restoration, idempotent aggregation,
  logging failure, and additional hosted probe/CLI/multi-repository/manifest paths.

`HostedVerifierQosFixture` replaces the verifier with controlled lane launches through the
real process runner/invoker. Its trivial owned child waits on a unique event until the
recording setter has observed the reported job members. It does not launch a test suite.
Each repository, child and event is fixture-owned; the seams are async-flow scoped, so no
serial collection or manifest census entry is needed.

Tests are deferred to Acceptance. The enabled runner assertions discriminate the base
revision: without the hosted scope, a false attempt flag produces no setter calls. Removing
the scope also removes the summary entirely. Disabled cases additionally pin precedence
over a true attempt flag. No negative-control execution receipt is claimed by the Developer.
