using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using static Mcg.AgentOrchestrator.App.Orchestration.GateTestObligationEvaluator;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateTestObligationPrefixedNameFollow
{
    internal static Result Follow(Result result, IReadOnlyList<Source> sources, string orchestratorDirectory,
        string candidateSha, Func<IReadOnlyList<CandidateAddedClassEvidence.AddedClass>> readAddedClasses,
        Func<string, bool> provesClassAbsent)
    {
        if (result.Outcome != Outcome.SelectorNotFound) return result;
        var added = readAddedClasses().GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.Path, StringComparer.Ordinal).First()).ToArray();
        if (added.Length == 0) return result;
        foreach (var source in sources)
        {
            if (result.Obligation.PlannerIdentity is { } identity && source.BriefVersion != identity.BriefVersion) continue;
            var (evidence, pending, _) = ReadSelectorEvidence(result.Obligation.Selectors, source, orchestratorDirectory);
            var selectors = result.Obligation.Selectors.ToArray();
            var used = new List<TestClassRenameEvidence.ClassRename>();
            for (var index = 0; index < selectors.Length; index++)
            {
                var item = evidence[index];
                if (pending.Contains(item.Selector) || item.Passed + item.Failed + item.Superseded + item.Skipped > 0 ||
                    item.AmbiguousClassNames is not null) continue;
                var segments = selectors[index].Split('.');
                var matches = segments.SelectMany((segment, position) => added.Where(candidate =>
                        SyntaxFacts.IsValidIdentifier(segment) && candidate.Name.Length > segment.Length &&
                        candidate.Name.EndsWith(segment, StringComparison.Ordinal))
                    .Select(candidate => (position, candidate))).ToArray();
                if (matches.Length != 1) continue;
                var (position, candidate) = matches[0];
                var planned = segments[position];
                if (added.Any(item => item.Name == planned) || !provesClassAbsent(planned)) continue;
                segments[position] = candidate.Name;
                selectors[index] = string.Join('.', segments);
                var binding = new TestClassRenameEvidence.ClassRename(planned, candidate.Name, "none", candidate.Path, 0,
                    $"prefixed-name planned={planned} committed={candidate.Name} candidate={candidateSha}");
                if (!used.Contains(binding)) used.Add(binding);
            }
            if (used.Count == 0) continue;
            var (rewritten, stillPending, _) = ReadSelectorEvidence(selectors, source, orchestratorDirectory);
            if (stillPending.Count == 0 && rewritten.All(item => item.Satisfied))
                return new(result.Obligation, Outcome.Satisfied, result.Obligation.Selectors, source.Id, RenameFollows: used);
        }
        return result;
    }
}
