# Untyped recorded-reservation restoration

Developer test execution is prohibited for this task; RED/GREEN execution is owned by Acceptance.

Run `ConductorMissingWorktreeRecoveryTests.UntypedReservation_RestoresContextAndHandoffWithoutReplacingAttempt`
for both stale-registration cases. For RED, restore the early return on a null `ContextPackageReceipt`
in `WorkerProfileDispatcher.RestorePreparedContextIfMissing`. The test must reach the disabled process
boundary and fail its context-directory assertion. A second RED can retain regeneration but restore
the unconditional null-receipt rejection in `WorkerDispatchPromptPreparation.RecordRestoredReceipt`;
the expected failure is the typed-package exception instead of the disabled-process boundary.
Restore both changes and run GREEN against the final candidate. Preserve the candidate identity,
non-zero case counts, and assertion messages in the Acceptance receipts. Build evidence alone does
not establish either behavioral result.
