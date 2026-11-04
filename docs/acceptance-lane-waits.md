# Acceptance lane wait diagnostics

`gate-stats waits [--last <n>] [--top <k>] [--json]` reads existing attempt receipts and TRX discovery.
Defaults are ten attempts and twenty occurrence rows. Known lane wait totals rank first, descending;
ties use attempt, lane, route, retry and occurrence identity. Stage coverage and gate wall values
cover all selected occurrences, including rows outside `--top`.

Each observation identifies its attempt, lane, route, retry and occurrence. Local invocation binding
uses the execution owner's invocation ordinal. Uninvoked pending lanes retain a separate occurrence
identity; remote claim attempts and a later local fallback remain distinct. Optional observations
travel in verification results and summaries and in the existing `.attempt.json` receipt on owner
disposal, including fault and cancellation paths. No timing store is created.

| Stage | Owning observer | Endpoint |
| --- | --- | --- |
| scheduler-slot | `AcceptanceDotnetTestBatchScheduler.RunAsync`, `RunInfrastructureShardBatchAsync.RunShardAsync` | Shared semaphore grant |
| scheduler-resource-keys | `RunInfrastructureShardBatchAsync` pending set and resource reservation | Scheduler capacity and all lane keys reserved |
| shard-permit | `RunLaneTestHostWithShardPermitAsync` around `AcquireAsync` | Permit acquired or cancellation |
| remote-claim | `RemoteLaneEarlyOffer.TryOfferAsync`, after existing eligibility/offer predicates | Executor and applicable serialized key claims return |
| ready-to-admitted | Ready at the above admission owner; completed in the permit owner or after remote claims | All required route grants complete |
| admitted-to-execution | Permit owner / `RemoteLaneCoordinator.RunRemoteAsync` | `executor-invoked` / `submission-start` |

Local prerequisites, shared prebuild and completion of the initial remote offer pass precede local readiness. The first batch group can own prebuild;
it does not receive a scheduler wait sample before becoming eligible. Shared-prebuilt shard candidates
become locally ready after that offer pass, while remote offers can become ready independently before prebuild.
Routes without a successful shared prebuild retain unknown scheduler coverage; their first direct
readiness observer is entry to the test-host permit boundary after their own build prerequisites.
Unselected local candidates are removed when a remote offer is adopted. A local fallback begins
a new local occurrence when it is requeued. Reuse creates no executor event.

New elapsed measurements use the run collector's injected `TimeProvider` timestamps, with nullable
seconds and a unique local clock domain. UTC changes do not enter those durations. Component waits
can end at cancellation; cancellation never fabricates admission or execution. A stage without its
required endpoint is unknown. Scheduler/key attribution identifies the observed combined admission
boundary, not an inferred resource-pressure cause.

Remote submit, runner, worktree, build-wait, build and test values reuse `RemoteLanePhaseTimings`.
Their source labels identify submission-step UTC, runner durations or remote TRX clocks. Existing
queue and collection differences cross local and remote clocks: raw values remain in the health
ledger, while this elapsed report leaves them unknown. Remote values never enter the local interval
union. Missing, malformed, negative, nonfinite, incomplete, duplicate or mismatched observations
are unknown; measured zero is a valid sample. Route-inapplicable stages are counted separately.
Legacy TRX occurrences have unknown route/applicability and conservatively show missing coverage.

Stage totals describe their valid sample coverage. Combined lane wait requires complete comparable
local intervals, takes their union and then sums known occurrence totals. It leaves incomplete or
incomparable occurrences unknown. Gate wall is read independently from the existing
`gate-lane-time.jsonl` total milliseconds, converted to seconds. Neither a stage sum nor summed lane
wait represents gate wall or a promised saving. Diagnostics do not drive admission or verdicts.

Acceptance executes `AcceptanceLaneWaitObservationTests`, `GateStatsWaitsTests` and
`AcceptanceLaneWaitArtifactIntegrationTests`, plus the scheduler, remote phase and reporting
regression classes named in the task package. The integration fixture drives actual scheduler and
permit boundaries with private resources and injected execution, disposes the owner into its real
metadata sink, and reads the report from disk. A future real-gate comparison is a nonblocking follow-up.
