using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.GateTestObligationEvaluator;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateTestObligationTraitExclusionFollow
{
    internal sealed record TraitExclusion(string Selector, IReadOnlyList<string> Reasons);

    internal static Result Follow(Result result, IReadOnlyList<Source> sources,
        string orchestratorDirectory, string candidateSha)
    {
        if (result.Outcome != Outcome.SelectorNotFound) return result;
        var source = sources.FirstOrDefault(item => item.Id == result.SourceId);
        if (source?.Checks is not { } checks || string.IsNullOrWhiteSpace(source.WorktreeRoot)) return result;

        // The source is the live candidate worktree. HEAD must match the receipt;
        // this does not attest uncommitted edits in that worktree.
        var head = GitCli.Run(source.WorktreeRoot, "rev-parse", "HEAD");
        if (!head.Succeeded || head.DrainTimedOut ||
            !string.Equals(head.Output.Trim(), candidateSha, StringComparison.OrdinalIgnoreCase)) return result;

        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in checks.Declared.Where(check =>
            string.Equals(check.Type, "dotnet-test", StringComparison.OrdinalIgnoreCase)))
        {
            // A solution or implicit project does not identify a bounded source scope.
            if (string.IsNullOrWhiteSpace(check.Project) ||
                !check.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return result;
            if (!AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(check) &&
                !AcceptanceCheckCommandBuilder.UsesVstestRunner(check)) return result;
            var filter = AcceptanceCheckCommandBuilder.ExtractFilterArguments(check.Arguments, []);
            var excludes = AcceptanceCheckCommandBuilder.NeedsUnattendedHostIntegrationExclusion(check) &&
                (AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(check) ||
                    string.IsNullOrWhiteSpace(filter));
            if (excludes)
                projects.Add(check.Project.Replace('\\', '/'));
            else if (!string.IsNullOrWhiteSpace(filter) || check.Arguments.Any(argument =>
                argument.StartsWith("--filter", StringComparison.OrdinalIgnoreCase)))
                return result;
        }
        if (projects.Count == 0) return result;

        var discoveries = projects.Select(project =>
            FocusedEvidenceTraitExclusionDiscovery.Read(source.WorktreeRoot, project)).ToArray();
        if (discoveries.Any(discovery => discovery is null)) return result;

        // Recheck all selectors: Grade's unmatched list omits pending checks and
        // superseded-only evidence, which must not be mistaken for satisfied tests.
        var evidence = GroupedGateReceiptTrxEvidence.Evaluate(
            result.Obligation.Selectors, source.Paths, orchestratorDirectory);
        var proven = new List<TraitExclusion>();
        foreach (var item in evidence.Where(item => result.Selectors.Contains(item.Selector, StringComparer.Ordinal)))
        {
            if (item.Passed + item.Failed + item.Superseded + item.Skipped > 0 ||
                item.AmbiguousClassNames is not null ||
                GateCheckSelectorMatcher.Grade(item.Selector, checks) != GateCheckSelectorMatcher.Outcome.NotApplicable) continue;
            var reasons = new SortedSet<string>(StringComparer.Ordinal);
            var covered = true;
            foreach (var discovery in discoveries)
            {
                var proof = discovery!.ProveExcluded(item.Selector, [], [("Category", "HostIntegration")]);
                // Null also means no matching class. Never infer coverage from
                // a proof in just one of several potentially matching projects.
                if (proof is not { Count: > 0 })
                {
                    covered = false;
                    break;
                }
                reasons.UnionWith(proof);
            }
            if (covered) proven.Add(new(item.Selector, reasons.ToArray()));
        }
        if (proven.Count == 0) return result;

        var excluded = proven.Select(item => item.Selector).ToHashSet(StringComparer.Ordinal);
        var remaining = result.Selectors.Where(selector => !excluded.Contains(selector)).ToArray();
        if (remaining.Length == 0 && evidence.All(item => excluded.Contains(item.Selector) ||
            item.Satisfied && GateCheckSelectorMatcher.Grade(item.Selector, checks) is not
                (GateCheckSelectorMatcher.Outcome.Pending or GateCheckSelectorMatcher.Outcome.Failed)))
            return result with { Outcome = Outcome.TraitExcluded, TraitExclusions = proven };

        // Keep the unresolved evidence visible even if Grade omitted it originally.
        var unresolved = remaining.Concat(evidence.Where(item => !item.Satisfied && !excluded.Contains(item.Selector))
            .Select(item => item.ReportedEntry)).Distinct(StringComparer.Ordinal).ToArray();
        return result with { Selectors = unresolved, TraitExclusions = proven };
    }
}
