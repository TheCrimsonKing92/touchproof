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
  The inspected `WorkerProcessJobs.cs:225-244` terminated/confirmed the Windows job
  and awaited only the launcher's native exit handle. No alternate invoker or
  process object was found.
- Cleanup failure metadata survives through `JunitCommandAcceptanceCheck.cs:173-186`.
  Successful cancellation metadata is consumed by the catch at 188-191, followed
  by the verifier's boundary cancellation at 1589/1609. Therefore a generic final
  cancellation exception alone cannot attest that the cleanup helper ran.

These are static call-path findings, not a receipt that this path executed in the
failed gate. The fixture's pipe blocks until release, but source inspection does
not establish that this wait prevents Windows job termination. No such claim
or production change was made in that diagnostic revision.

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

## Current bounded revision

The current typed retry feedback names gate `18718c48-0-20261105092617486`
at candidate `9623342a0534352c1228bbfd2645b9414e35fc9b`, Balanced 2, and the
same cancellation case. Its reported assertion is "Cancellation returned before
all fixture processes exited", with launcher PID 60632 exit observed true and
child PID 1636 exit observed false on the first run and rerun. This package
contains that feedback, but no original executed-count receipt or full
cancellation exception for this run. The precise runtime mechanism remains
undetermined; Developer test execution is prohibited.

The source establishes a narrower gap: the Windows branch waited on the root
process handle, whereas the Unix branch pinned and awaited the observed children
as well. `RegisteredOwnedProcess.KillTreeAndWaitForExitAsync` now applies that
handle wait to Windows too, verifies each acquired child's membership in the
owned job before retaining it, and disposes every pinned handle after the bounded
wait. It still invokes the existing job termination/empty-accounting operation
for descendants created after the snapshot. That operation now checks the return
from explicit termination through its retained job handle. Unconfirmed exits
remain failures with the union of live observed children, root and job members.

Hypothesis for Acceptance: waiting for observed child handles in addition to job
occupancy closes the reported cancellation boundary. This is not a proven account
of why that gate returned early. Execute
`JunitCommandEnvironmentIsolationTests.FailureOrCancellation_LeavesParentEnvironmentUnchanged(cancel: True)`
in its normal Balanced shard, plus `AcceptanceProcessRunnerTests`. The original
pre-cleanup assertion is retained and now includes the captured cancellation
message, allowing the next receipt to distinguish a reported job-cleanup failure
from a child surviving a confirmed cleanup. A build proves compilation only.

## Revision after the main-move re-gate

Typed retry feedback for candidate `ad017405582179a1b047673c00719e9d1af5d6c6`
names attempt `18718c48-0-20261105121314372`, Balanced 1, and the same cancellation
case: child PID 45068 remained unobserved at exit while launcher PID 46288 had
exited. No original executed-count receipt or cleanup metadata is supplied in this
package. Which cleanup path ran remains undetermined; Developer test execution is
prohibited.

Source inspection identifies two independently unsafe paths. A member inspection
exception could bypass termination, and final-heartbeat cancellation could unwind
through the early registration release before the outer cancellation catch. The
revision puts termination in a `finally` around member pinning and moves release
and disposal to the outer finalizer, after cancellation cleanup. Accounting is
attached to the command result after release. Cancellation detected during final
capture/heartbeat draining also stops the owned tree before release.

`AcceptanceCancellationCleanupRan` and a skipped-cleanup reason now accompany
the existing cancellation evidence. The JUnit wrapper preserves these safe fields
on caller cancellation, and the existing fixture boundary assertion prints every
`AcceptanceCancellation*` entry and whether cleanup ran.

Acceptance owns these executable checks and their negative controls:

- `RegisteredOwnedProcessCancellationTests.KillTree_MemberPinningThrows_StillTerminatesAndEmptiesJob`
  injects a Win32 failure into member pinning of a live job. A duplicated job handle
  must report empty before release can mask missing termination. Moving termination
  out of the pinning `finally` must fail this assertion.
- `AcceptanceProcessRunnerTests.Caller_cancellation_at_final_heartbeat_keeps_custody_until_cleanup`
  injects cancellation at the final heartbeat and requires cleanup evidence plus
  exactly one release/disposal. Restoring early release must fail its cleanup assertion.
- Rerun `JunitCommandEnvironmentIsolationTests`, including the original cancellation
  boundary assertion, and `JunitCommandEnvironmentDiagnosticTests`. Build success
  does not prove the failed boundary has been repaired.

## Bounded revision for receipt finding-evidence-6ee0d0fa5cafece1a84ff18a

The typed retry feedback binds two failures to candidate
`dc41c11fbf465a3735e153238a37ffe1d6add106`. The relevant source was unchanged at
dispatch HEAD `8beaa1a8028617294ee505beca0c07b693269795`.

`AcceptanceProcessRunnerTests.Caller_cancellation_at_final_heartbeat_keeps_custody_until_cleanup`
failed at `Assert.True(observation.RegistryActive)` before injecting cancellation.
`HasActiveRegistryEntryForTests` queries the optional SQLite registry, whereas
`HasActiveJobForTests` queries the owned-job registration. The test now requires
the latter and the retained Windows native handle, then verifies job release after
cleanup. It does not configure or mutate the shared optional registry.

The same receipt reports that
`JunitCommandEnvironmentIsolationTests.FailureOrCancellation_LeavesParentEnvironmentUnchanged(cancel: True)`
observed child PID 39788 alive after cleanup reported `TreeExitConfirmed=True` for
launcher PID 48784. It does not report whether that child belonged to the owned
job. The runtime mechanism remains undetermined. No additional production
termination change is justified by this receipt alone.

The cancellation fixture now asserts that both pinned processes belong to the
launcher's registered job before cancellation, retaining that member list in
boundary diagnostics. The existing immediate exit assertion is unchanged.
Only after that assertion and invocation drain does teardown release a surviving
gated child, preserving the first failure while avoiding an indefinitely blocked
fixture process.

Acceptance must execute both named cases and retain the complete exception,
including `owned_job_members_before_cancellation`. A precondition failure proves
missing membership at the ready event; a passing precondition followed by the
original exit failure localizes the remaining defect to termination/exit
observation. Neither result alone establishes why membership or termination
failed. Developer test execution remains prohibited, and the supplied package
does not contain a full executed-count receipt admitting a local reproduction.
This is a test correction and diagnostic revision, not a completed repair of
the surviving-child failure.

## Containment correction for receipt finding-evidence-ef6f345688c084b22398294c

The supplied typed receipt binds three executed tests to candidate
`4d41436a6634f463b960a4e0edd4eb314c6a7620`, with the cancellation row failing:
"Fixture processes must belong to the owned job before cancellation". The pinned
launcher PID was 58716, the child PID was 3576, and the reported job contained
only 58716. These source anchors are unchanged at dispatch HEAD `bb553e64`.
This failure precedes cancellation and does not establish a termination defect.

The shell branch of `AcceptanceCheckProcessInvoker.StartAcceptanceProcess` omitted
`containDescendants`, whose default is false. Its job therefore enabled
`JOB_OBJECT_LIMIT_BREAKAWAY_OK`; the direct file-capture branch already used a
contained job. The shell branch now explicitly requests containment through the
existing typed launch argument. This aligns both acceptance launch paths with
their obligation to retain descendants until cleanup. An explicit breakaway request
now fails at launch instead of escaping this job. Other worker launch defaults
are unchanged.

Hypothesis, not an established runtime cause: the permissive shell-job policy
allowed the observed child to escape. The receipt does not show how the child
left the job. A read-only Claude Opus 5.5 cross-family review agreed with the
bounded policy correction and found no PID-list layout defect, while retaining
that uncertainty. The current worker's PATH resolves to an installed
`AppData/Local/Programs/PowerShell/7/pwsh.exe`, rather than an app-execution alias;
this observation does not identify the executable used by the historical gate.

The isolation test now requires membership for both failure/cancellation rows.
Pinned identity diagnostics include full image
paths. The original immediate exit assertion, cleanup evidence requirement and
survivor release ordering remain intact.

Developer-side test execution remains prohibited. Acceptance must execute
`JunitCommandEnvironmentIsolationTests`, including at least five runs of
`FailureOrCancellation_LeavesParentEnvironmentUnchanged(cancel: True)`, plus
`AcceptanceProcessRunnerTests.Caller_cancellation_at_final_heartbeat_keeps_custody_until_cleanup`
and `JunitCommandEnvironmentDiagnosticTests`. Retain each complete failing
exception and distinguish membership failures, cancellation-cleanup failures,
and passing cancellation boundaries. A membership failure after this correction
rejects the hypothesis that containment alone is sufficient. Build evidence
proves compilation, not repair of that boundary.

## Installed-shell correction after containment remained red

Typed retry feedback `finding-evidence-8cc010c9e9be58424f2bed84` binds both
failure/cancellation rows to candidate `54f86771ad97d75b9edab3a94788c8cf47f146b7`.
Both failed "Fixture processes must belong to the owned job before cancellation".
The reported child image was the WindowsApps PowerShell 7.6.9 package executable;
each job contained only its `cmd.exe` launcher. The fixture and contained launch
path are unchanged at dispatch HEAD `93e5ec6d`. This feedback rejects containment
alone as a sufficient correction; it does not establish the runtime escape mechanism.
The current package supplies these assertions but no raw executed-count receipt
for this candidate, and Developer test execution is prohibited.

The fixture still selected the first existing `pwsh.exe` on PATH, bypassing the
shared resolver's WindowsApps exclusion. It now uses `TestPowerShell.Executable`.
`TestPowerShellResolutionTests.EveryPwshFixtureUsesARealInstalledShell` includes
this fixture in its absolute, existing, non-WindowsApps path checks. The gated
ownership assertion also requires the pinned child's image to match the selected
shell, so runtime redirection cannot silently defeat that selection. Membership
and immediate exit assertions remain in place. No production termination code
or cancellation deadline changes in this revision.

A read-only Claude Opus cross-family review supported the correction and suggested
the child-image assertion. The remaining hypothesis is that selecting the installed
shell restores the controlled fixture's membership. Acceptance must execute
`TestPowerShellResolutionTests`, `JunitCommandEnvironmentIsolationTests` (including
at least five cancellation rows), `JunitCommandEnvironmentDiagnosticTests`, and
`AcceptanceProcessRunnerTests.Caller_cancellation_at_final_heartbeat_keeps_custody_until_cleanup`.
Retain full exceptions and distinguish image/membership failures, cleanup failures,
and passing cancellation boundaries. A successful build proves compilation only.
