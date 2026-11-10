# Acceptance closure reuse investigation: dfcef632

## Current evidence boundary

Baseline: `d06b8c519d325da9c224444a105783ac227f6b4a`. The hypothesis is that a
newer failed execution can be collapsed into an absent green lookup, allowing
an older closure green to bypass it. Acceptance reproduced this on candidate
`61d0fc59d34820b45be77b232a30b35880340fcf`, receipt
`finding-evidence-3515a13ac0ef6f3e07c230d0`. The supplied typed retry feedback is
`task/criterion-retry-feedback.json` in the Developer context package. The named
guard failed at `AcceptancePartitionVerdictCacheContentReuseTests.cs:56`:
`C must miss after B's same-pair/lane failed execution`; C instead reported
`source_attempt_id=A-pass`, `reuse_rule=closure`. The earlier positive-control,
forced-rerun, parsed nonzero-coverage and journal assertions precede this failure.
This selects the reproduced-and-repaired branch. Post-repair execution remains
Acceptance-owned; no post-repair pass is claimed here.

Pre-repair source paths:

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

The current-candidate failure above is the discriminating result, rather than
the historical hypothesis. Acceptance must retain the enclosing named TRX,
attachments and output. The worker does not retrieve operator state or execute
test hosts. Registered focused evidence owns the post-repair result.

## Branch decision and compatibility

The repair preserves the latest journal record at the lookup seam: absent
permits a proven closure proposal; non-green or incomplete authority produces a
miss before closure or disjoint fallback; green retains existing artifact and
coverage checks. Both partition and eligible whole-project reuse use this
policy. Journal append order, rather than worker timestamps, chooses authority.
Cached-reuse summaries are not execution records, and `RecordExecution` ignores
explicit cached-reuse results. A newer executed green can supersede red only
with retained, parsed passing TRX and nonzero completed coverage, even if the
structural-coverage switch is off. Non-green alone establishes neither a
deterministic defect nor an apparatus cause; either blocks only its exact scope.

No persisted representation change is needed for this repair:
the current authoritative goal journal and version-1 closure index remain intact.
The existing acceptance execution owner delivered by `209400a8b` is untouched.
The v1 index's source attempt, filter and artifacts resolve against existing
goal journals. A proposal must identify exactly one executed source, match its
key rebuilt with the manifest and its artifacts, and remain the latest reusable
execution within that source's own identity. Missing, ambiguous or incomplete
source authority misses. Legacy partition journal records need no new closure
field. Sources are discovered once per cache session, lazily on closure fallback;
the owning source journal is revalidated at reuse. Pair hits keep their existing path. New miss reasons describe the
authority failure without changing existing diagnostic strings.

Blocking survives deletion or reconstruction of the derived index because it
comes from the journal. Supported compatibility is current main's journal and
version-1 index, with provenance sufficient to validate reuse. Returning to an
older binary without this policy is not a supported safe rollback. No global
negative-closure propagation, new index format or ambient setting is introduced.

`AcceptancePartitionVerdictCacheAuthorityTests` adds partition and whole-project
controls for timestamp ties/reversal, derived-index eviction/rebuild, later
executed green, invalid recovery artifacts, cached-reuse isolation, independently
valid sources, non-green classification scope, supported v1 reads, unsupported
versions, missing/ambiguous source history and mismatched artifacts. The real
committed-repository safety guard remains unchanged.

Acceptance owns the source-revert negative control: retain all tests and restore
only `AcceptancePartitionVerdictCache.TryReuse` to its body at
`61d0fc59d34820b45be77b232a30b35880340fcf`. This restores the null-collapsing
`LatestGreenVerdict` call and removes both requesting-authority and source-authority
checks from that path. Keep the journal snapshot shape and helper declarations
so the negative control compiles. Require the same named guard to fail because
C reuses A via closure, restore the repaired body, then require the safe miss.
Compiler, fixture or positive-control failures do not qualify. Repaired source:
`AcceptancePartitionVerdictCache.cs`, `.Authority.cs` and `.MissReasons.cs`.
Different-family review and final integrated full acceptance remain separately
owned requirements before landing.
