# Slice c negative controls

Developer execution is restricted to the sanctioned build check. Behavioral RED/GREEN execution is deferred to Acceptance; no executed test result is claimed here.

Run each control in the candidate worktree, restore the production change, then run the same focused test for GREEN. A compiler error or zero selected tests is not a RED receipt.

| Focused test | Compiling mutation | Predicted RED assertion |
| --- | --- | --- |
| `LinkedBacklogPointerTests.PlannerGetsPointerAndOnDemandRecord` | Remove ` | Status: {other.Status}` from the healthy neighbor evidence in `LinkedBacklogRecordEvidence.Capture`. | Pointer/record relationship evidence lacks `Status: Open`. |
| `LinkedBacklogPointerTests.CaptureReusesEachNeighborIncludingUnavailable` | Replace `if (!neighbors.TryGetValue(id, out var evidence))` with `var evidence = string.Empty; if (true)` and replace `neighbors.Add(id, evidence)` with `neighbors[id] = evidence`. | The repeated neighbor has two lookups instead of one, including null/throw cases. |
| `LinkedBacklogPointerTests.RelationshipPressureKeepsCompleteArtifactsBoundedAndCountsOmissions` | Remove the relationship section from `header` in `RenderRecord`, retaining `header += "\n## Body\n"`. | Record has no included relationship entries or link omission count. |
| `BacklogCloseFollowUpTests.Close_SelectsOpenCauseNeighborsAndPreservesRelatedState` | Remove the advisory `Console.Write` call from the `backlog-close` handler. | Successful receipt remains, but the RelatedCause advisory and open neighbors are missing. |
| `BacklogCloseFollowUpTests.Close_SelectsOpenCauseNeighborsAndPreservesRelatedState` | Change the advisory filter from `other.Status == BacklogItemStatus.Open` to `other.Status != BacklogItemStatus.Open`. | Open neighbor entries disappear and closed neighbors leak into output. |
| `BacklogCloseFollowUpTests.FollowUp_ReadFailuresPreserveCommittedClose` | Replace the root/neighbor read catch bodies in `RenderBacklogCloseFollowUp` with `throw;`. | The corresponding injected IOException escapes instead of producing unavailable information. |
| `BacklogCloseFollowUpTests.FollowUp_UnicodePressureBoundsWholeSectionAndReportsOmissions` | Change its production `maxBytes` from 1000 to 10000. | Complete advisory exceeds the asserted 1000 UTF-8 byte bound. |

Acceptance also runs `LinkedBacklogPointerTests`, `BacklogCloseFollowUpTests`, `BacklogTypedRelationTests`, `BacklogStoreTests`, `WorkerContextPackageTests.ExhaustiveSemanticSourceInventoryRecoversEveryAuthoritativeSourceAndHash`, and existing `CliCommandTests.CliBacklogClose*` cases. Tester and Reviewer own delivery, snapshot, budget, typed-contract and mutation inspections.
