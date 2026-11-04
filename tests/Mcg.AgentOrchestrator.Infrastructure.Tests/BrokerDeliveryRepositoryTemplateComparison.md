# Broker repository template pre-landing comparison

Operator-owned evidence for goal ca9e0da1, criterion 3. This file supplies the fixed
selection and receipt contract; it is not a measurement receipt or permission to land.
Parent backlog 971c047f remains open.

Use baseline `490ca860f06dd511959a9ba775a4bc4e5e9bd5dd` and the orchestrator's committed
candidate containing this slice. Record both exact SHAs and build/staged assembly
identities. Use one otherwise idle executor, identical environment and concurrency,
and a new test process for each sample. Build/stage each revision before measuring;
do not reuse an assembly from the other revision. Retain all six samples, including
failed/invalid samples, in order **ABBAAB**, A=baseline, B=candidate.

Run this identical selection from each revision's worktree using its current managed
runner closure (operator/Acceptance only):

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -NoBuild -Filter 'FullyQualifiedName=NativeDeveloperArtifactClosureTests|FullyQualifiedName=PlanningBrokerArtifactClosureTests|FullyQualifiedName=WorkerWorkflowBrokerRoleDeliveryTests'
```

The new helper and observation regressions belong to ordinary Acceptance, outside the
performance selection. Keep every original broker row and assertion selected in both arms.

Before collecting valid samples, ensure the operator's managed launch harness activates
the existing slot timing/Git census **inside both test hosts**. `Set-MtpHermeticEnvironment`
in `scripts/MtpTestRunner.psm1` strips a parent-shell `MCG_TEST_SLOT_TIMING_FILE`;
`-TestInput` only permits `MCG_TEST_INPUT_*`, so it cannot pass that existing variable.
Use the operator's instrumented launch harness and record its identity/configuration
and identical instrumentation in both arms. The stock command above alone does not
establish launch counts. Missing census activation is an evidence gap, never zero Git
launches. Do not change test behavior to make instrumentation appear active.

Retain the runner's actual TRX paths (including remote-first paths where applicable),
execution start/end receipts and census artifacts outside temporary clean-run folders.
Execution wall excludes build/staging. Sum all test durations in the three selected
classes, keeping individual class totals too. Extract the unique `BROKER_DELIVERY`
records from retained test output; require the same observation coverage as baseline.
Sum only the top-level `repository` stage for repository totals. Its nested template
initialization, wait and copy durations are explanatory and must not be added again.
Candidate `repositoryPreparation.initialization.status=not-entered` with completed
copy denotes reuse, independently of the existing context `lifecycle` cold/warm label.
Include cold template construction in governing wall and class-duration totals.

Durable receipt shape (all numbers must come from retained execution evidence):

```json
{
  "goal": "ca9e0da1", "baselineSha": "...", "candidateSha": "...",
  "executor": "...", "runnerIdentity": "...", "instrumentationIdentity": "...",
  "selection": ["NativeDeveloperArtifactClosureTests", "PlanningBrokerArtifactClosureTests", "WorkerWorkflowBrokerRoleDeliveryTests"],
  "environmentAndConcurrency": {},
  "samples": [{
    "ordinal": 1, "arm": "A", "sha": "...", "assemblyIdentity": "...",
    "trxPaths": [], "executionReceipt": "...", "censusReceipt": "...",
    "executionWallSeconds": null, "classTestSeconds": {}, "totalTestSeconds": null,
    "repositorySeconds": null, "coldInitializationSeconds": null,
    "gitLaunches": null, "launchesByCallerAndSubcommand": {},
    "warmCopyLaunches": null, "caseIdentitiesAndCountsReceipt": "...",
    "allPassed": null, "observationsComplete": null, "valid": null
  }],
  "medians": {"baseline": {}, "candidate": {}},
  "verdicts": {"executionWallWithin5Percent": null, "testSecondsWithin5Percent": null,
    "repositoryTotalDecreased": null, "warmInitializationLaunchesRemoved": null},
  "eligibleToLand": false
}
```

Require exactly six valid samples, three per arm, in ABBAAB order, with equivalent
case identities/counts, passing outcomes and complete observations. Compute the median
of each arm's three run values. Candidate execution-wall and total-test-seconds medians
must each be <= 1.05 times baseline; median repository total must decrease. Attributed
census evidence must show the four baseline init/add/commit/HEAD-read launches occur
once per candidate variant, with none for warm repository copying. Cold initialization
cost stays included. Null/unavailable counts cannot satisfy this check.

Any invalid sample, unexplained case difference, failed assertion, missing receipt or
exceeded threshold is insufficient evidence to land. Investigate while retaining the
full sequence; do not selectively replace samples until passing. Ordinary Acceptance
and review remain required independently of this practical performance tolerance.
