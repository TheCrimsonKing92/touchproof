# Replay final accounting control

Status: execution deferred to Acceptance; Developer is authorized to build only.

Reported operator reproduction at candidate `d1149513296c16ae2c160522ca3e69867586b0c1`:
`ExperimentReplayProcess.RunAsync` with Windows PowerShell
`-NoProfile -NonInteractive -Command "exit 0"` returned exit 0,
state `apparatus-failed`, reason `Job accounting unavailable.`, resources null.
The brief references `operator-evidence/cohort-comparison/adapter-smoke-powershell/measurement.json`
and independent Claude review `operator-evidence/paired-accounting-review-20261110.json`.
These operator artifacts are not present in this worker checkout; this is a record of
the supplied finding, not a newly inspected or executed receipt.

Acceptance must execute `ExperimentReplayProcessTests` with nonzero selected counts:

- RED: keep the new tests, observation seam and observer API, but restore the two
  post-cleanup accounting reads in `ExperimentReplayProcess.RunAsync` to
  `group.TryGetTotalProcesses(out var total)` and `group.TryReadAccounting(out var accounting)`.
  This reproduces the original disposed-group behavior while keeping the control buildable.
  `RunAsync_ExitZero_RetainsFinalAccounting` must fail on expected `exited` versus
  actual `apparatus-failed`. The cleanup theory must fail on the corresponding
  expected execution state versus `apparatus-failed`, with unavailable resources.
- GREEN: restore both reads through `retainedAccounting` and run the identical
  selection on the candidate. Retain candidate/patch identities, command, counts,
  failed assertions and TRX identities for both arms. A missing host run, zero
  selection, or unrelated assertion failure is not a demonstrated negative control.

The theory synchronizes descendant completion/cancellation on actual sampled job
membership. Its independent job handle stays open across cleanup, proving that
termination cannot depend on the last handle closing. It checks final cumulative
CPU, memory and process totals after the original group is disposed, then checks
that the runner released its own observer. The timeout case uses a single blocking
child and a zero deadline; it asserts the timeout decision, never elapsed speed.
The observation scope is async-local; events, jobs and directories are private to
each test. No machine-global process observations or shared scheduling exclusion
are required.

Operator must rerun the same tiny process probe before expensive comparisons.
Corpus, calibration, value-bound, independent ledger cross-check and empirical
pilot obligations remain with their existing owners.
