# Capture-limit diagnostic suppression

Developer execution is build-only. Acceptance must execute these controls; no RED/GREEN test
receipt is claimed here.

- Selection: `JunitCommandEnvironmentDiagnosticTests.CaptureLimit_PreservesEventAndCapWithCheckLocalReferenceSuppression`.
  In the candidate worktree, temporarily pass `capture.Path` unconditionally at the
  `AcceptanceCheckProcessInvoker` call to `EmitCaptureLimitReached`. Run the focused selection
  from that same worktree. The opt-in arm must fail its `Assert.DoesNotContain(" path=", line)`;
  the arm without inputs must still publish a capture reference. Both arms require an actual
  capture-limit event, its cap counter, a failed gate and the child environment observation.
  Restore the conditional null reference and record GREEN for the same selection.
- Selection: `JunitCommandEnvironmentDiagnosticTests.SuppressedCaptureLimit_HasNoCaptureFilenameRunFallback`.
  The null-attempt case requires `run=unknown` with no `path=` field. Restoring the old
  formatter must fail the exact event assertion (or its null-path handling). The explicit-attempt
  case retains `run=run-42` without a capture reference.

Retain the existing `AcceptanceOutputCaptureTests.CaptureLimitEventIsTypedAndCarriesGoalRunPathAndCap`
control for ordinary event publication. No deadline, stop-on-cap or capture-counter behavior changes.
