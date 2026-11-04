# Developer build report reconciliation fixture matrix

Developer authored these fixtures; Tester audits them and Acceptance executes them. No worker-side test execution is claimed.

| Fixture | Production seam | Discriminating assertion |
| --- | --- | --- |
| `DeveloperBuildReportEvidenceClosureTests.StoredBuildReport_ClosesAutomaticallyAndPreservesAuthority` | Kernel / `RoleReportEvidenceClosure` | Completes the stored wait, preserves identity/payload/owner, attributes a Worker answer to exact invocation, dispatch, report log, implementation tree and gate IDs; repeated closure creates no answer. Both historical and different-identifier rows are present. |
| `InsufficientStoredEvidence_RetainsWaitWithSpecificReason` / missing-receipt, report-only | Stored invocation lookup | Report text and a passed gate leave the wait pending without its referenced record. |
| Same / stale-round, wrong-goal, wrong-task, wrong-dispatch, stale-report | Invocation / authoritative dispatch and report | Changing one binding retains the wait and identifies the conflicting round. |
| Same / stale-brief, stale-wait | Current authoritative brief | Stale owner dispatch or request cannot supply current evidence. The future-brief request stays stored and unanswered with `brief-version-differs`, while the current-brief pending query excludes it. |
| Same / stale-tree | Build tree / implementation tree | Exact digest mismatch retains the wait. |
| Same / report-reference, missing-reference, non-authoritative-report | Terminal report grammar | Another invocation ID, no ID, or unavailable authoritative output cannot close the wait. The unavailable-output row removes authority from both latest verification and same-round history, then asserts the restored authority is absent while the report preview remains. |
| Same / missing-project, multiple-projects | Explicit requested project coverage | A different project, or coverage of only one of two requested projects, retains the wait. |
| Same / report-class, missing-class, unrelated-gate | Exact declared/requested classes / gate | Missing declaration or candidate gate coverage retains the wait. |
| Same / failed-compiler, invalid-compiler | Host invocation validity / compiler outcome | Failed or internally inconsistent compiler observations retain the wait. |
| Same / unproved-candidate, fixture-equality-only; `MissingProvenanceAdapter_RetainsPreciseEvidenceRequest` | Existing provenance boundary | No candidate proof or no adapter retains the wait with the exact missing relationship. |
| `Recognition_DoesNotManufactureBuildAuthority` | Structured request metadata and full report-request grammar | Generic assertions, live observations, approvals, other roles, worker aliases and conflicting metadata remain unanswered; no gate is queried. |
| `BuildReport_AdmissionAllowsGateProductionWithoutAnsweringWait` | `DeveloperReportWait` / grouped admission | Admission permits producing evidence while the request stays open. |
| `LandingBuildReportAttentionTests.LandingCaller_RequiresActualCandidateProofAndBoundGate` | Landing caller / real Git adapter / stored gate files | Conductor integration closes the wait; worker integration, extra goal work with identical fixture contents, wrong gate candidate and wrong gate brief retain it. |
| `ResolvedWait_RetiresOnlyExactAttentionAndPreservesCurrentTrainHold` | Landing caller / inbox occurrence / SQLite / conductor blocker recorder | Resolves the exact correlated item, removes the answered wait from the worklist, preserves unrelated same-body attention and train attention, recomputes a current train blocker and is idempotent. |
| `Attention_InsufficientCorrelationLeavesOccurrenceUntouched` | Inbox persisted correlation | Wrong correlation, unknown request (including a valid multi-wait serialization containing an unknown ID), ambiguous short ID and arbitrary extra text leave attention unchanged. |
| `CompoundReason_AnsweredPortionRetiresAndRemainingDemandSurvives` / blockers, verification, mixed-waits, mixed-kinds, combined | Inbox occurrence / SQLite / persisted escalation | Retires the original answered demand, publishes the exact remaining serialized waits and blockers under their own correlation, preserves unrelated attention and the current train hold, and leaves repeat reconciliation unchanged. These assertions fail on the prior wait-only parser. |
| `RecurringReason_ResolvesActiveOccurrenceAndPreservesTerminalHistory` | Inbox occurrence lifecycle / SQLite / persisted escalation | Resolves the single active recurrence despite terminal history sharing its key, preserves the earlier terminal row byte-for-byte, retires the persisted escalation and is idempotent. The prior all-history count leaves this occurrence raised. |

The historical reconstruction uses the operator-bound answer in
`.orchestrator-context/34110efcdcec4920b1c2f1633d5a8ded/authoritative/c713067f92074710a25c3424beddc4b4/typed/goal/timeline.json`
(identity `goal/timeline.json`, SHA-256 `166372d0a192e7fb8b13e892f64cdb28af0a5d4702296d6c4980cd699d9f5377`).
Its embedded report contains `/n` and `/"` transport artifacts; fixture extraction converts those to newlines and quotes.
It preserves the supplied original wait metadata, full Developer WORKER_RESULT, invocation
`a2dc31071353480ca3e840ba5949b412`, completion `2026-11-03T18:53:04.2883284Z`, implementation
`640565002810cda17193ea99b7b2fd3ed0a5ed08`, tree `445f730202eb65fc9f17c418b5b67c53b92bb5f2`,
candidate `cae4ce619d501148545db8c02dfeb4cb4e5bc4e3` and corroborating Tester invocation
`814727958d5a46bab28613a5491069d7`. The answered snapshot is reconstructed before its operator answer.
The original refined criterion text was not included; the fixture supplies a report criterion at the bound index.
The Core fixture controls the provenance adapter result; it does not assert that parent IDs or unchanged fixtures prove provenance.
The separate landing fixture invokes the real existing `ConductorOnlyCandidateRange` rule in an isolated synthetic repository.

Recognition uses the complete structured request phrase from the supplied incident, or its explicit backticked-project-list form,
with matching request/payload ownership, identity and current criterion metadata. It does not search question prose for build keywords.
Missing candidate provenance identifies the implementation-to-candidate edge and leaves the wait unanswered.

Acceptance also owns regression execution of `RoleReportEvidenceClosureTests`, `RoleReportEvidenceClosureTestsTesterResult`,
`LandingExecutorRoleReportEvidenceTests`, `LandingHoldSkipReasonTests`, `GroupedAdmissionLandingHoldTests`,
`ConductorOnlyCandidateRangeTests`, `PassedGateHoldPolicyTests` and all normal required gates.
Independent authority and narrow-seam review remain assigned to the configured Reviewer in the standard Scout workflow.
