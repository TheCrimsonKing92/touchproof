using FocusedEvidenceFilter = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilter;
using FocusedEvidencePlannedCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidencePlannedCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceOverflowPacker
{
    internal static IReadOnlyList<FocusedEvidencePlannedCheck> Pack(
        IReadOnlyList<(string Target, string Project, FocusedEvidenceFilter? Filter)> compatible,
        string project)
        => Pack(compatible, project, NoClassDurationHistory.Instance);

    internal static IReadOnlyList<FocusedEvidencePlannedCheck> Pack(
        IReadOnlyList<(string Target, string Project, FocusedEvidenceFilter? Filter)> compatible,
        string project, IClassDurationHistory history)
    {
        var legacy = PackLegacy(compatible, project);
        var model = new FocusedEvidenceGroupCostModel(project, compatible.SelectMany(item => item.Filter!.Tokens), history);
        var costs = compatible.Select(item => model.PredictSeconds(item.Filter!.Tokens)).ToArray();
        if (costs.Length == 0 || costs.Any(cost => cost is null)) return legacy;

        var boundary = Enumerable.Range(0, compatible.Count).Where(index =>
            compatible[index].Filter!.CanonicalText.Length >= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength).ToArray();
        var groups = Enumerable.Range(0, legacy.Count - boundary.Length).Select(_ => new List<int>()).ToList();
        foreach (var index in Enumerable.Range(0, compatible.Count).Except(boundary)
                     .OrderByDescending(index => costs[index])
                     .ThenBy(index => compatible[index].Filter!.CanonicalText, StringComparer.Ordinal)
                     .ThenBy(index => compatible[index].Target, StringComparer.Ordinal))
        {
            var best = groups.Select((members, position) => new { Members = members, Position = position })
                .Where(group => CanonicalLength(group.Members.Append(index)) <= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength)
                .OrderBy(group => group.Members.Sum(member => costs[member]!.Value))
                .ThenBy(group => group.Position).FirstOrDefault();
            if (best is null) groups.Add([index]);
            else best.Members.Add(index);
        }
        groups.AddRange(boundary.Select(index => new List<int> { index }));
        var balanced = groups.Where(group => group.Count > 0)
            // Reuse legacy construction, including member order and selection-filter identity.
            .Select(group => PackLegacy(group.Select(index => compatible[index]).ToArray(), project).Single())
            .OrderByDescending(Cost).ThenBy(check => check.Filter!.CanonicalText, StringComparer.Ordinal).ToArray();
        // Filter constraints can make LPT worse; preserve the legacy upper bound in that case.
        return balanced.Max(Cost) > legacy.Max(Cost) ? legacy : balanced;

        double Cost(FocusedEvidencePlannedCheck check) =>
            FocusedEvidenceGroupCostModel.Sum(check.SelectionFilters.Select(filter => model.PredictSeconds(filter.Tokens))) ?? double.MaxValue;

        int CanonicalLength(IEnumerable<int> members) => string.Join("|", members
            .SelectMany(index => compatible[index].Filter!.Tokens)
            .Select(token => token.CanonicalToken).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)).Length;
    }

    private static IReadOnlyList<FocusedEvidencePlannedCheck> PackLegacy(
        IReadOnlyList<(string Target, string Project, FocusedEvidenceFilter? Filter)> compatible,
        string project)
    {
        var checks = new List<FocusedEvidencePlannedCheck>();
        var group = new List<(string Target, string Project, FocusedEvidenceFilter? Filter)>();
        foreach (var item in compatible
            .OrderBy(item => item.Filter!.CanonicalText, StringComparer.Ordinal)
            .ThenBy(item => item.Target, StringComparer.Ordinal))
        {
            var candidateTokens = group.Append(item)
                .SelectMany(member => member.Filter!.Tokens)
                .DistinctBy(token => token.CanonicalToken, StringComparer.Ordinal)
                .OrderBy(token => token.CanonicalToken, StringComparer.Ordinal)
                .ToArray();
            var candidateCanonical = string.Join("|", candidateTokens.Select(token => token.CanonicalToken));
            if (group.Count > 0 &&
                (candidateCanonical.Length > GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength ||
                 item.Filter!.CanonicalText.Length >= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength))
            {
                AddGroup();
            }

            group.Add(item);
            // Boundary-sized items stay whole and occupy their own check.
            if (item.Filter!.CanonicalText.Length >= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength)
            {
                AddGroup();
            }
        }

        if (group.Count > 0)
        {
            AddGroup();
        }

        return checks;

        void AddGroup()
        {
            if (group.Count == 1)
            {
                var item = group[0];
                checks.Add(new FocusedEvidencePlannedCheck(
                    [item.Target], item.Project, item.Filter, [item.Filter!]));
            }
            else
            {
                var groupTokens = group
                    .SelectMany(item => item.Filter!.Tokens)
                    .DistinctBy(token => token.CanonicalToken, StringComparer.Ordinal)
                    .OrderBy(token => token.CanonicalToken, StringComparer.Ordinal)
                    .ToArray();
                checks.Add(new FocusedEvidencePlannedCheck(
                    group.Select(item => item.Target).Distinct(StringComparer.Ordinal).ToArray(),
                    project,
                    new FocusedEvidenceFilter(
                        string.Join("; ", group.Select(item => item.Target)),
                        string.Join("|", groupTokens.Select(token => token.CanonicalToken)),
                        groupTokens),
                    group.Select(item => item.Filter!).ToArray()));
            }

            group.Clear();
        }
    }
}
