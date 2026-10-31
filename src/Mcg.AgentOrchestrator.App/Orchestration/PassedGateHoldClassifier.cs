using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PassedGateHoldClassifier
{
    internal static PassedGateHold? Classify(Goal goal, string attemptId, string candidate)
    {
        if (goal.Status != GoalStatus.Verified || goal.LandingBlocker is not { } blocker) return null;
        var detail = blocker.Detail;
        PassedGateHoldCategory category;
        string rule;
        // Preserve the landing policy's order. Retain the complete detail as supporting evidence.
        var wait = Regex.Match(detail, @"request ([a-fA-F0-9]{32})\b");
        if (!wait.Success)
            wait = Regex.Match(detail, @"pending human wait\(s\) \(\w+ ([a-fA-F0-9]{8})\b");
        var obligation = Regex.Match(detail, @"gate test obligation [^;\r\n]+(?:; source [^;\r\n]+)?");
        if (wait.Success)
        {
            category = PassedGateHoldCategory.PendingHumanWait;
            rule = "request " + wait.Groups[1].Value;
        }
        else if (obligation.Success)
        {
            category = PassedGateHoldCategory.GateTestObligation;
            rule = obligation.Value;
        }
        else if (blocker.Kind.Contains("criterion-evidence", StringComparison.Ordinal))
        {
            category = PassedGateHoldCategory.CriterionEvidenceObligation;
            rule = blocker.Kind + ": " + detail;
        }
        else if (detail.Contains(LandingDenylistOwnerGate.HoldReasonPrefix, StringComparison.Ordinal))
        {
            category = PassedGateHoldCategory.LandingDenylist;
            rule = string.Join("; ", Regex.Matches(detail, @"rule=\S+ path=\S+").Select(match => match.Value));
            if (rule.Length == 0) rule = LandingDenylistOwnerGate.HoldReasonPrefix;
        }
        else return null;
        var evidence = detail;
        if (category == PassedGateHoldCategory.GateTestObligation && !detail.Contains("closest:", StringComparison.Ordinal))
            evidence += "; no matching evidence";
        return new(category, rule, evidence, attemptId, candidate,
            PassedGateHoldResolvingVerbs.For(category, goal, candidate, wait.Success ? wait.Groups[1].Value : null));
    }
}
