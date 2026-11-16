using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainMemberRecheckSkip
{
    internal static string? Guard(MergeTrainReceipt receipt, MergeTrainMemberBinding? attributed,
        bool messageSubjectFailure, int attempt) =>
        attributed is not null ? "already-attributed-member" :
        messageSubjectFailure ? "message-subject-failure" :
        attempt != 0 ? "non-first-attempt" :
        receipt.Identity.Members.Count != 3 ? "member-count-not-three" :
        receipt.Outcome != MergeTrainGateOutcome.Failed ? "not-genuine-train-red" : null;

    internal static void Record(MergeTrainReceipt receipt, string reason, int attempt,
        MergeTrainMemberRecheck.IOutcomeCache cache, Action<string, string> log)
    {
        // A separate key preserves the real outcome slot and uses the cache's atomic insert-once boundary.
        var marker = Marker(receipt);
        var outcome = new MergeTrainMemberRecheck.Outcome(receipt.Identity.Members.Select(member =>
            new MergeTrainMemberRecheck.MemberResult(member.GoalId.Value, member.CandidateRevision,
                member.BranchRevision, member.BriefVersion, "unrun", null)).ToArray(), "skipped");
        bool saved;
        try
        {
            // A saved sweep (including legacy outcomes) has already consumed this receipt's diagnostic.
            if (cache.Read(receipt) is not null) return;
            saved = cache.TrySave(marker, outcome);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return; } // Diagnostics must preserve the formerly silent skip's behavior.
        if (saved) log("train-member-recheck", $"MEMBER_RECHECK_SKIPPED train={receipt.Identity.Value} " +
            $"receipt={receipt.ReceiptId} reason={reason} attempt={attempt} " +
            $"members={receipt.Identity.Members.Count} outcome={receipt.Outcome}");
    }

    internal static void RecordEarlyReturn(MergeTrainReceipt receipt, MergeTrainMemberBinding? attributed,
        bool messageSubjectFailure, int attempt, MergeTrainMemberRecheck.IOutcomeCache cache, Action<string, string> log)
    {
        if (receipt.Outcome == MergeTrainGateOutcome.Failed && Guard(receipt, attributed, messageSubjectFailure, attempt) is { } reason)
            Record(receipt, reason, attempt, cache, log);
    }

    internal static bool WasRecorded(MergeTrainReceipt receipt, MergeTrainMemberRecheck.IOutcomeCache cache)
    {
        try { return cache.Read(Marker(receipt)) is not null; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
    }

    private static MergeTrainReceipt Marker(MergeTrainReceipt receipt) =>
        receipt with { ReceiptId = receipt.ReceiptId + "#member-recheck-skipped" };
}
