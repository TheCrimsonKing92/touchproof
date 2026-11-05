# JUnit cancellation trace

Candidate inspected: `655f3f29187e65e09a4d5d0e7d56b959df5e1d9f`.
The supplied retry feedback names gate `18718c48-0-20261105082222549`,
`JunitCommandEnvironmentIsolationTests.FailureOrCancellation_LeavesParentEnvironmentUnchanged(cancel: True)`,
and an `IOException` deleting `suite`, with child PID 48520, launcher PID 59952,
and both exit-observed flags false. The package does not contain that execution's
original exception and stack trace before teardown. Its production cause remains
undetermined; the teardown summary does not prove which cancellation boundary ran.

## Source trace for the orchestrator commit body

- `JunitCommandEnvironmentFixture.cs:55-62` creates an attempt owner linked to the
  caller token and awaits `RunInvocationForOwnerTestsAsync` with that same token.
- `GoalAcceptanceVerifier.cs:1594-1602` binds the owner. Lines 1576-1587 await the
  cancellation monitor and invocation task. The monitor awaits the invocation
  (`AcceptanceGateCancellationMonitor.cs:13-26`); it does not abandon it on cancellation.
- `GoalAcceptanceVerifier.cs:2263-2271` supplies separate command and parser callbacks.
  `JunitCommandAcceptanceCheck.cs:64-66` links caller, owner and deadline tokens;
  lines 167-168 pass the owner/caller lifetime separately from the deadline signal.
- `GoalAcceptanceVerifier.cs:3852-3871` selects the production process runner for
  this fixture: its public resolver constructor sets `requiresTestTelemetryReceipt`
  true at lines 319-343. Both verifier copies preserve that field (409, 424).
  `AcceptanceProcessRunner.cs:127-147` forwards the token and typed request to
  `AcceptanceCheckProcessInvoker`.
- `AcceptanceShellProcessStartInfo.cs:80-94` constructs the Windows `cmd.exe`
  launcher and declared command. `AcceptanceCheckProcessInvoker.cs:135` starts it;
  lines 529-532 delegate to `WorkerProcessJobs.StartRegisteredOwnedOrThrow`.
  `WorkerProcessJobs.cs:686-695,852-901` register a suspended owned process before
  resuming it, then transfer that launcher's process and native handle into the
  returned `RegisteredOwnedProcess`. The gated `pwsh` is that launcher's child.
- `AcceptanceCheckProcessInvoker.cs:187-189` observes caller cancellation during
  capture connection; lines 233-250 observe it during the exit wait. Both throw
  into the outer caller-cancellation catch at 378-388, which passes the same local
  `process` to `AcceptanceProcessCancellationCleanup.StopOwnedTreeAsync` at 385.
  The helper calls `RegisteredOwnedProcess.KillTreeAndWaitForExitAsync` at line 15.
  `WorkerProcessJobs.cs:225-244` terminates/confirms the Windows job and awaits the
  launcher's native exit handle. No alternate invoker or process object was found.
- Cleanup failure metadata survives through `JunitCommandAcceptanceCheck.cs:173-186`.
  Successful cancellation metadata is consumed by the catch at 188-191, followed
  by the verifier's boundary cancellation at 1589/1609. Therefore a generic final
  cancellation exception alone cannot attest that the cleanup helper ran.

These are static call-path findings, not a receipt that this path executed in the
failed gate. The fixture's pipe blocks until release, but source inspection does
not establish that this wait prevents Windows job termination. No such claim or
speculative production fix is made.

## Bounded correction and remaining evidence

The cancellation test now preserves its first exception together with drain,
process-wait and deletion failures. Its existing pre-disposal exit assertion now
reports both pinned process identities and exit states, and a failed exit wait
names the process identity. Fixture deletion makes one attempt, with no retry or
sleep. No assertion, cancellation deadline, or production gate was relaxed.

Acceptance must execute the named cancellation test and retain the full aggregate
exception, including its first inner exception and stack trace. This distinguishes
a ready-event failure, cancellation wait timeout, live-process assertion, and
cleanup exception. Also select `AcceptanceProcessRunnerTests` to retain the direct
owned-launch cancellation control. Developer-side test execution is prohibited by
`current-task.md`; a build is not behavioral proof of cancellation cleanup.
