using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

// Presentation only: the typed evidence obligations keep their execution authority.
internal static class ReviewerExecutedCriterionPresentation
{
    private static readonly Regex SentenceBoundary = new(
        @"(?<=[.!?])(?:\s+|$)|[\r\n]+", RegexOptions.CultureInvariant);
    private static readonly Regex Declaration = new(
        @"^(?<owner>[A-Za-z]+(?:[\s-]+gate)?)\s+owns;\s*(?<executor>[A-Za-z]+(?:[\s-]+gate)?)\s+executes\.$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool ShowAsGateOwned(AgentRole role, string criterion, IReadOnlyList<string> operatorOwned)
    {
        if (operatorOwned.Contains(criterion, StringComparer.OrdinalIgnoreCase)) return false;
        if (role != AgentRole.Reviewer) return true;
        var declarations = SentenceBoundary.Split(criterion.Trim())
            .Select(sentence => Declaration.Match(sentence.Trim()))
            .Where(match => match.Success).ToArray();
        return declarations.Length != 1 || !IsKnownOwner(declarations[0].Groups["owner"].Value) ||
            !declarations[0].Groups["executor"].Value.Equals("reviewer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownOwner(string owner) =>
        Enum.TryParse<AgentRole>(owner, ignoreCase: true, out _) ||
        owner.ToLowerInvariant() is "operator" or "acceptance" or "acceptance gate" or "acceptance-gate" or "gate";
}
