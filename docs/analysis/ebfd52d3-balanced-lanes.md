# Infrastructure lane balancing evidence

Baseline supplied in goal ebfd52d3, from the host's
`.orchestrator/gate-lane-receipts.jsonl` and `.orchestrator/gate-lane-time.jsonl`:

- Last ten gates: Remainder median wall 1,331 s; gate wall 18.5–26.7 min.
- Attempt `operator-gate-20261030162207158`: Remainder wall 1,322,866 ms,
  slot-held 2,490,083 ms, slot-wait 4,581,565 ms, three slots.
- Daily mean Remainder wall, October 25–30: 607, 579, 500, 594, 852,
  1,145 s. October 30 had 26 runs.

These are the supplied before figures, not a new host-ledger query. The worker
context prohibits reaching orchestrator state. The current-history prediction
therefore remains unavailable to this worker. Each attempt's `balancedLayout`
records the actual prediction, members, default estimate and history window.
The first completed gate supplies the first per-class observations; missing
history uses the configured fallback. Unreadable or corrupt history also uses
the fallback, with `historyReadFailure` in `balancedLayout` distinguishing it
from an empty store. A failed write records `historyWriteFailure` in the final
plan before publication; malformed history is preserved for diagnosis.
Measurements include summed test
durations, subtracting recorded slot wait when its sidecar is available.

The pool has four lanes and a five-observation window. It absorbs the nine
unconstrained lanes: Cli, Conduct watch sweep scoping, Goal lifecycle commands,
Goal worktree cleanup cohort, Goal worktree cleanup git, Goal worktree parallel,
Process spawning parallel, Remainder balance B and Remainder balance C. The
`Category!=HostIntegration` exclusion is configured on the pool and inherited by
every generated balanced lane and the residual. The pool declares no exclusive
resource keys, so its generated lanes and residual keep the original keyless
lanes' eligibility for parallel execution and remote offers.

Operator ruling `d7eb800ab1824d31a23f2069dce8b7fb` withdraws ruling `6099d9b2`.
Remainder retains its main-branch filter and `machine:GitHeavyLanes` key; its
classes stay outside the pool and remain serialized with Remainder balance A
and Remainder merge train and ratchet. All other constrained filters and keys
remain. New unlisted classes still match Remainder's constrained catch-all;
routing them elsewhere is follow-up backlog item `f95032ce`. Balanced packing
alone does not establish a gate-wall improvement; the post-landing comparison
below measures the resulting gate wall.
This changes layout, not the host's slot capacity or remote offer policy.
Non-owned collections remain indivisible packing units within the pool;
constrained members retain their existing lane membership.

The residual complements every scanned class. A scanner-missed class still runs
there, even if a constrained filter also selects it. Such duplicate observations
contribute only once to history. Its observed class names and count (including
zero) are republished into the same attempt's lane-plan artifact and event, with
an advisory gate-summary receipt. Before execution `residualObserved` is false;
an unexecuted residual must not be read as proof of an empty selection.

Command transport uses the existing MTP filter response-file mechanism. For a
fixed Windows example with 1,200 classes named
`Example.Infrastructure.AcceptanceBalancedFixture0000Tests` through `1199Tests`,
four lanes, artifacts under `C:\gate\artifacts`, results under `C:\gate\results`,
TRX name `lane.trx`, UTF-8 console preflight and named-pipe capture:

| Command | Members/exclusions | Quoted inline command | Composed cmd arguments after spill | Limit |
| --- | ---: | ---: | ---: | ---: |
| Largest balanced lane | 300 | 23,382 chars | 361 chars | 8,191 chars |
| Residual | 1,200 | 97,476 chars | 355 chars | 8,191 chars |

These are static composition measurements, not executed test receipts. The
balanced prediction for that no-history fixture is 300 s per lane at the 1 s
fallback. `AcceptanceBalancedLaneCommandLengthTests` exercises the real composer
and lossless spill; Acceptance owns execution. Actual local launch lengths,
response-file usage and the 8,191-character limit are recorded in
`balancedLayout.commands`. The existing launch guard still rejects overlong
commands after spilling. Remote launches are not reported as locally measured.

The remote job carries filters in JSON. `Invoke-ExecutorLane.ps1` writes the
filter into its result directory and passes `-FilterFile` to the candidate's
`Invoke-TestSummary.ps1`. That wrapper reads it in process. `New-MtpRunnerArguments`
then spills managed MTP filter arguments over the 4,096-character budget into a
UTF-8 response file. Exact class includes/exclusions retain their full symbols
without substring wildcards. This preserves the remote offer policy.

For the same 1,200-class fixture, composing the actual PowerShell runner arguments
with `C:\gate\Tests.dll`, a 111-character scratch results-directory path, `lane.trx`,
minimum count 1 and timeout 780 s gives these lengths (including `dotnet `):

| Remote managed launch | Inline | After response-file spill | Windows CreateProcess limit |
| --- | ---: | ---: | ---: |
| Largest balanced lane, 300 classes | 22,129 chars | 386 chars | 32,767 chars including terminator |
| Residual, 1,200 exclusions | 92,629 chars | 386 chars | 32,767 chars including terminator |

The wrapper launch is 423 characters for `balanced-1`, or 449 for
`infrastructure-residual`, using `C:\mcg-executor`, candidate prefix `123456789`,
the infrastructure test project and `C:\Users\mcgrunner\AppData\Local\Temp\Low\mcg-tests`
for results. Its limit is also 32,767 characters including the terminator; no
`cmd.exe` shell is involved. These are composition measurements, with no tests
launched. The executor must receive the updated deployed `Invoke-ExecutorLane.ps1`
before using this transport; keep the existing executor-tools reconciliation and
digest preconditions when rolling it out. The new exact-selector regression class
checks argument translation, lossless spill and filter-file error handling;
Acceptance owns executing it.

Only the residual removes `--minimum-expected-tests` and uses
`--ignore-exit-code 8`, following the
[MTP command options](https://github.com/dotnet/docs/blob/main/docs/core/testing/microsoft-testing-platform-cli-options.md).
Its completion decision still requires a readable passing TRX, complete counts,
no interruption and a zero process exit. Other lanes retain the zero-test guard.

Operator follow-up after landing: compare ten full gates' largest infrastructure
lane wall, gate wall and observed residual counts against these before figures.
Record that comparison in the canonical dogfood store; it does not hold landing.
