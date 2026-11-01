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

        var (evidence, pending, _) = ReadSelectorEvidence(result.Obligation.Selectors, source, orchestratorDirectory);
        var candidates = evidence.Where(item => result.Selectors.Contains(item.Selector, StringComparer.Ordinal) &&
            item.Passed + item.Failed + item.Superseded + item.Skipped == 0 && item.AmbiguousClassNames is null &&
            GateCheckSelectorMatcher.Grade(item.Selector, checks) == GateCheckSelectorMatcher.Outcome.NotApplicable).ToArray();
        if (candidates.Length == 0) return result;

        // The source is the live candidate worktree. HEAD must match the receipt;
        // this does not attest uncommitted edits in that worktree.
        var head = GitCli.Run(source.WorktreeRoot, "rev-parse", "HEAD");
        if (!head.Succeeded || head.DrainTimedOut ||
            !string.Equals(head.Output.Trim(), candidateSha, StringComparison.OrdinalIgnoreCase)) return result;

        var projects = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
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
            if (!excludes && (!string.IsNullOrWhiteSpace(filter) || check.Arguments.Any(argument =>
                argument.StartsWith("--filter", StringComparison.OrdinalIgnoreCase)))) return result;
            var project = check.Project.Replace('\\', '/');
            projects[project] = excludes && (!projects.TryGetValue(project, out var previous) || previous);
        }
        if (!projects.Values.Any(excludes => excludes)) return result;

        var discoveries = projects.Select(project => (Excludes: project.Value,
            Discovery: FocusedEvidenceTraitExclusionDiscovery.Read(source.WorktreeRoot, project.Key))).ToArray();
        if (discoveries.Any(item => item.Discovery is null)) return result;

        var proven = new List<TraitExclusion>();
        foreach (var item in candidates)
        {
            var reasons = new SortedSet<string>(StringComparer.Ordinal);
            var covered = true;
            foreach (var (excludes, discovery) in discoveries)
            {
                // Absence is distinct from a matching declaration whose exclusion cannot be proved.
                // Use discovery's own matching rule; every matching project must supply a proof.
                if (!discovery!.HasMatchingDeclaration(item.Selector)) continue;
                var proof = excludes ? discovery.ProveExcluded(item.Selector, [], [("Category", "HostIntegration")]) : null;
                if (proof is not { Count: > 0 })
                {
                    covered = false;
                    break;
                }
                reasons.UnionWith(proof);
            }
            if (covered && reasons.Count > 0) proven.Add(new(item.Selector, reasons.ToArray()));
        }
        if (proven.Count == 0) return result;

        var excluded = proven.Select(item => item.Selector).ToHashSet(StringComparer.Ordinal);
        var graded = Grade(result.Obligation, source.Id, evidence.Where(item =>
            !excluded.Contains(item.Selector) && !pending.Contains(item.Selector)).ToArray());
        if (graded.Outcome == Outcome.Satisfied)
            return pending.Count == 0
                ? result with { Outcome = Outcome.TraitExcluded, TraitExclusions = proven }
                : result with { Outcome = Outcome.Pending,
                    Selectors = result.Obligation.Selectors.Where(pending.Contains).ToArray(), TraitExclusions = proven };

        return result with { Outcome = graded.Outcome, Selectors = graded.Selectors, TraitExclusions = proven };
    }
}
