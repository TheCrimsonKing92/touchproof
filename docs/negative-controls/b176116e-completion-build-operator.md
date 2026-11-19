# Isolated completion-build operator controls

This is an operator-executed fixture for criteria 13–15. It uses the production
`ConductorBatchLoop`, `GoalDispatchOperations`, `BackgroundDispatchRunner`,
`CompletionBuildEvidenceCoordinator`, native managed child launch, sanctioned build
helper, SQLite persistence, operator-intent coordinator and conductor handoff owner.
Worker output is seeded; no model provider is started. Driver callbacks reject worker,
acceptance, rebase and landing execution. This procedure prepares evidence; a successful
build of the probe alone does not satisfy the operator criteria.

Build the candidate's existing Windows process probe with:

```powershell
.\scripts\Invoke-WorkerBuildCheck.ps1 tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/SupervisorLaunchProbe/Mcg.AgentOrchestrator.SupervisorLaunchProbe.csproj
```

Use the **exact candidate's** `Mcg.AgentOrchestrator.SupervisorLaunchProbe.dll` from
that build's output, with its runtimeconfig, dependencies and App DLL alongside it.
Managed builds publish isolated output; use the output directory in the retained
compiler log/receipt, rather than an older checkout-local `bin` directory. Do not copy
only the probe DLL. Pass absolute paths below. Run from PowerShell 7 on Windows with
the .NET 10 SDK and git available:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts/Invoke-CompletionBuildOperatorControl.ps1 -ProbeDll C:\candidate-output\Mcg.AgentOrchestrator.SupervisorLaunchProbe.dll -SourceRepository C:\candidate-checkout -OutputRoot C:\operator-evidence\completion-build-new -Control all
```

`OutputRoot` must be a new, short absolute path outside any goal worktree. Each control creates a separate disposable git repository,
two goal worktrees, state and intent databases, build-storage root, profile and package
directory. No live orchestrator state is read. All waits use readiness/release events
or actual process exit; five-minute guards detect absent signals. Compilation restores
against an empty package-source list and uses a minimal package-free project. The
project's real MSBuild `BeforeTargets=CoreCompile` target invokes the fixture's event
checkpoint, outside the source tree. Its receipt includes the checkpoint process PID
and start time. Only the independent controlling process can signal release.

Each case starts the fallback from a production tick and waits for the real compiler
checkpoint. One fresh host applies a SQLite-backed priority intent to the disjoint goal.
The conductor defers goals changed by an intent until the next tick, so a second fresh
host reconciles the disjoint documentation task. Both actions happen **before** release.
The probe checks pending state, no verification, unchanged HEAD, absent result commit
and unchanged operation/dispatch/candidate/project/artifact/child identity after each tick.

| Control | Additional action and required observation |
| --- | --- |
| `held` | Release the real build; consume its matching success; require one commit, one verification and exactly-once terminal application after another tick. |
| `stop` | Set the real conductor stop file; the production detach owner preserves the authorized child. Remove the stop file, then apply a real cancel-dispatch intent; require confirmed child death and released custody without a commit. |
| `renewal` | Exercise the production max-duration and `ConductorLoopHandoff` path, real Windows successor launch, readiness/activation protocol and conductor lease transfer. A successor must checkpoint a production tick before handoff verification succeeds. The same build child survives and completes once. |
| `restart` | Prove the original launch owner has exited; reload SQLite in another OS process; retain the same authorized child and consume its eventual result once. |
| `ownership-loss` | Transfer the completion lease with the real store's expected-owner CAS to a separately running successor using a real goal-evidence scope. Cancel the original operation through its owner; require old child death while the successor process and lease survive. The successor releases its own lease independently. |
| `replacement` | Reset the originating dispatch through the kernel; require a new dispatch to be rejected while custody is unresolved. Cancel the exact old operation, then record a new dispatch and launch a new real build. An obsolete cancellation selector must fail without affecting the replacement child. |
| `stale-candidate` | Finish real compilation, mutate source before reconciliation, then require `StaleCandidate`, no successful completion and no commit. |
| `stale-dispatch` | Finish real compilation, reset the dispatch before application and settle only historical custody. Start a new dispatch/build, then present the actual old child's result at the new result path after the new child exits. Require `ApparatusFailure`, no completion and no commit despite a current compiler-success worker receipt. |

`-Control <name>` runs one case independently. Every fresh reconciliation host holds
the production conductor lease and reloads the isolated SQLite state. The lease-successor
control holds only goal-evidence authority while the predecessor is settled. This tests
owner-qualified lease preservation; it does not claim an unauthorized lease transfer
occurs in ordinary operation.

Inspect `<case>\receipts.jsonl` and retain the whole case directory. Receipts include
source HEAD/dirty status, App and fixture SHA256, operation identity, candidate digest,
required projects, result path, child PID/start/image, intent identity/application time,
tick elapsed milliseconds, host PIDs, release ordering, terminal disposition and custody
settlement. Check that `compiler-checkpoint` precedes `held-control-proven`, which
precedes `operator-release` (where applicable), with a different host PID for each tick.
For renewal also inspect `handoff-logs` and the `loop-exit` handoff result. Compiler
logs and worker receipts remain in the case's isolated build storage. Compare the
recorded assembly hashes to the candidate build receipt before accepting evidence.

Success emits one bounded JSON result per case, followed by a bounded script summary.
Any assertion, absent signal or nonzero host exit fails the command. `control-passed`
is written only after custody cleanup proves the attached child dead. Failure evidence
is retained; do not treat a partial JSONL file as a passing control. Cleanup cancels only
the exact attached operation through the coordinator. If custody cannot be proven,
retain the directory and recover that recorded identity before removing artifacts.
After recording operator evidence in the canonical goal, the operator may remove the
explicit disposable output directory. No fixture automatically deletes evidence.

The corrected prior provenance remains: backlog `53ce5bfc` is Done; owner goal
`35bcb7de5e00455094d9b6510f442b11` was accepted at `7e237d822`. These controls do not
reopen that provenance question or substitute historical receipts for current execution.
