using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainFallbackAttribution
{
    internal static MergeTrainMemberBinding? TryAttribute(
        IReadOnlyList<string> resultPaths, string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members, int attempt,
        out IReadOnlyList<string> subjects, out string source,
        MergeTrainReceipt? receipt = null, MergeTrainMemberBinding? ownership = null,
        IReadOnlyList<string>? ownershipSubjects = null)
    {
        if (ownership is not null ||
            receipt?.FailedChecks.Contains(SourceSizeRatchetPreflight.CheckName, StringComparer.Ordinal) == true)
        {
            subjects = ownershipSubjects ?? [];
            source = "ownership";
            return ownership;
        }
        var confirmed = receipt is null ? null : MergeTrainRedAttribution.ReadConfirmedIdentities(receipt);
        var stack = MergeTrainStackFrameAttribution.TryAttribute(resultPaths, workspacePath, members,
            receipt?.Identity.ObservedMainRevision, out var stackSubjects, confirmed);
        IReadOnlyList<string> reachSubjects = [];
        var reach = attempt == 0
            ? MergeTrainReachAttribution.TryAttribute(resultPaths, workspacePath, members, out reachSubjects, confirmed) : null;
        subjects = [];
        source = "ownership";
        var attributed = MergeTrainImpactAttribution.TryAttribute(resultPaths, workspacePath, members, out subjects);
        source = "impact";
        if (attributed is not null) return attributed;
        // With no ownership verdict, disagreement between the new rules skips only
        // those rules. Preserve the existing impact-then-path fallback order.
        var conflictingRules = stack is not null && reach is not null && stack.GoalId != reach.GoalId;
        if (!conflictingRules && stack is not null)
        {
            subjects = stackSubjects;
            source = "stack-frame";
            return stack;
        }
        if (!conflictingRules && reach is not null)
        {
            subjects = reachSubjects;
            source = "reach";
            return reach;
        }
        attributed = MergeTrainPathAttribution.TryAttribute(resultPaths, workspacePath, members, out subjects);
        source = "path";
        if (attributed is not null) return attributed;
        subjects = [];
        source = "ownership";
        return null;
    }
}
