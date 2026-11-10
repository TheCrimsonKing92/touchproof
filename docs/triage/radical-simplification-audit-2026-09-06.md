# Acceptance closure reuse investigation: dfcef632

## Current evidence boundary

Baseline: `d06b8c519d325da9c224444a105783ac227f6b4a`. The hypothesis is that a
newer failed execution can be collapsed into an absent green lookup, allowing
an older closure green to bypass it. This round authors the controlled probe;
it establishes neither reproduction nor premise-disproof. Acceptance owns
execution. Production code and persisted formats are unchanged.

Current source paths:

- `AcceptancePartitionVerdictCache.TryReuse` calls `LatestGreenVerdict` before
  `AcceptanceClosureVerdictIndex.FindLatest`; a null result permits fallback.
- `LatestGreenVerdict` orders matching journal records by `RecordedAt` and
  returns null when the selected record is non-green.
- `RecordExecution` collects executed verdicts; `CompleteAttempt` appends them
  to the authoritative goal journal and exports eligible greens to the
  version-1 derived closure index.
- `Reuse_LatestJournalRecordRed_DoesNotReuseOlderGreen` in
  `AcceptancePartitionVerdictCacheTests` covers the journal path without a
  committed, hashable closure. It cannot establish the fallback outcome.

## Controlled probe and retained observations

`AcceptancePartitionVerdictCacheContentReuseTests.Reuse_ForcedRedWithAvailableClosure_DoesNotResurrectGreen`
uses a real committed local repository and its actual candidate tree and main
commit. A records a passing execution with one executed test. A separate-goal
positive control must reuse that exact artifact via `reuse_rule=closure`.
B must select forced full rerun, miss reuse, and append a failed execution with
one parsed failing test. C uses ordinary lookup and must miss rather than
resurrect A. A/B/C share goal, pair, candidate, main, manifest, lane, filter,
committed source closure and process environment. Only attempt and the explicit
B rerun policy differ. No repository content or environment is changed between
these steps.

`A-pass.trx` and `B-forced-red.trx` are synthetic cache inputs, parsed through
the existing completion and failure readers; they are not claims of external
test execution. They are attached by name to the xUnit result before fixture
cleanup. Test output records their SHA256, the successful closure-control
provenance, and C's actual result with pair/manifest/closure identities. Acceptance
must retain the enclosing named execution TRX and verify its attachments/output.

Companion controls in the same class cover retained valid closure reuse and
source artifacts, unrelated-lane failure isolation, and missing/empty artifacts.
Existing `AcceptanceStructuralCoverageEvaluatorTests` retain malformed-TRX and
zero-execution rejection; the cache's file-presence check alone is not a full
structural validation claim.

Current execution result: **pending Acceptance**. The registered evidence
request `current-controlled-investigation` (criterion 1, selector
`AcceptancePartitionVerdictCacheContentReuseTests`) owns the discriminating
receipt. Historical reports do not select the current branch.

## Branch decision and compatibility

If C misses with a passing positive control, retain the guard, record that
unchanged-production observation here, and stop production redesign. If C
reuses A after B, retain the intended assertion failure and its actual reuse
summary before repairing the existing seam. A setup, compiler, artifact or
positive-control failure does not reproduce the suspected policy defect.

No persisted representation change is needed for this investigation increment:
the current authoritative goal journal and version-1 closure index remain intact.
The existing acceptance execution owner delivered by `209400a8b` is untouched.
Any conditional repair must obey the supplied exact-pair/lane authority ruling:
authoritative journal order; no cached-reuse event superseding non-green;
validation of a proposed source within its own authority; blocking that survives
derived-cache eviction/rebuild; and a miss for missing/ambiguous provenance.
No global negative-closure propagation or old-binary rollback guarantee is added.
Non-green execution alone establishes neither determinism nor an apparatus cause;
apparatus classification needs separate typed evidence.

After any reproduced repair, Acceptance owns the source-revert negative control:
retain this probe, restore only the repaired production seam to the baseline,
observe C incorrectly reusing A, restore the repair, and observe the safe miss.
The repaired files and exact receipts must be added here once they exist.
Different-family review and final integrated full acceptance remain separately
owned requirements before landing.
