namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SoloHoldNextAction
{
    internal static (string Mechanism, string Owner, string NextAction) For(string reason)
    {
        if (reason.Contains(SoloHoldReasons.DuplicateTestRow, StringComparison.Ordinal))
            return ("solo acceptance failure reproduction", "Developer", "route the Developer because the failure reproduced");

        if (new[] { SoloHoldReasons.EvidenceInvalid, SoloHoldReasons.CandidateNotIntroduced, SoloHoldReasons.CaptureUnavailable }
            .Any(head => reason == head || reason.StartsWith(head + ":", StringComparison.Ordinal)))
            return ("solo evidence validation", "operator", "inspect the solo failure evidence named in the hold");

        return reason switch
        {
            SoloHoldReasons.AwaitingConsumption =>
                ("solo evidence consumption", "conductor", "wait for the next tick"),
            SoloHoldReasons.RetryCap =>
                ("solo acceptance correction retry cap", "operator", "review the solo acceptance correction evidence"),
            SoloHoldReasons.CorrectionCeiling =>
                ("solo acceptance correction ceiling", "operator", "review the solo acceptance correction evidence"),
            SoloHoldReasons.FreshGateUnavailable =>
                ("solo fresh gate admission", "operator", "resolve incomplete tasks or unavailable candidate identity, then re-gate"),
            SoloHoldReasons.CurrentIdentityUnavailable or SoloHoldReasons.WorkerStillRunning =>
                ("solo correction readiness", "conductor", "re-evaluate on the next tick"),
            SoloHoldReasons.ReceiptAbsent or SoloHoldReasons.RevisionMismatch =>
                ("solo attempt evidence recovery", "operator", "inspect the named attempt's results and re-gate the current candidate"),
            SoloHoldReasons.ReceiptAbsentInherited =>
                ("solo inherited failure", "operator", "review the baseline failure on main"),
            SoloHoldReasons.StartClaimedReconciliationRequired =>
                ("solo Developer start reconciliation", "operator", "confirm whether the claimed Developer start ran"),
            SoloHoldReasons.NoAttributedFailure or SoloHoldReasons.UnrepresentableFailingTests or
            SoloHoldReasons.CandidateMismatch or SoloHoldReasons.ClassifiedAcceptanceFailure or
            SoloHoldReasons.NoCommittedDeveloper or SoloHoldReasons.AlreadyRouted =>
                ("solo Developer route admission", "operator", "review the named evidence and route or re-gate manually"),
            SoloHoldReasons.GoalOrAttemptMismatch or SoloHoldReasons.ProofIdentityMismatch or
            SoloHoldReasons.BaselineUnavailable or SoloHoldReasons.CheckMismatch or
            SoloHoldReasons.AuthoritativeAttributionMismatch or SoloHoldReasons.TestBindingMismatch or
            SoloHoldReasons.BaselineCheckMismatch or SoloHoldReasons.BaselineResultMismatch or
            SoloHoldReasons.SourceAbsenceInvalid or SoloHoldReasons.BaselineApparatusOrUnknown or
            SoloHoldReasons.BaselineInheritedOrUncovered or SoloHoldReasons.BaselineGreenUncovered or
            SoloHoldReasons.DataCaseAmbiguous or SoloHoldReasons.CandidateRerunUnconfirmed or SoloHoldReasons.FailingTestsAbsent =>
                ("solo evidence validation", "operator", "inspect the solo failure evidence named in the hold"),
            _ => ("unmapped", "unmapped", "unmapped")
        };
    }
}
