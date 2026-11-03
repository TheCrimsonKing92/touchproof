using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A read-only projection. A recognized identity is neither a passing test nor a satisfied gate.
internal static class GateTestSelectorDiagnostic
{
    internal const string Marker = "GATE_TEST_SELECTOR_DIAGNOSTIC";
    [JsonConverter(typeof(JsonStringEnumConverter<Resolution>))]
    internal enum Resolution { Resolved, Unresolved, Unknown }
    internal sealed record Evidence(string Source, string Kind, string? Candidate, int? BriefVersion,
        string? UnavailableReason, IReadOnlyList<ProspectiveEvidenceTrxRows.Row> Rows,
        IReadOnlyList<DeclaredTestReportRows.Row> DeclaredRows,
        ProspectiveEvidenceGateResolver.GateCheckOutcomes? Checks = null, bool Complete = true);
    internal sealed record Report(string Source, string? Candidate, bool CurrentBrief,
        IReadOnlyList<string> Assertions);
    internal sealed record Entry(string ObligationId, int? CriterionIndex, string? PlannerIdentity,
        string? RequestingTaskId, int BriefVersion, IReadOnlyList<string> OriginalSelectors,
        string EffectiveSelector, string? Candidate, Resolution Status, IReadOnlyList<string> Evidence,
        IReadOnlyList<string> Suggestions, bool Ambiguous, bool Truncated, string SuggestionStatus,
        string DeveloperReportSource, IReadOnlyList<string> DeveloperAssertions,
        IReadOnlyList<AnsweredSelectorRebinding.SelectorEdit> AuthorizedEdits);

    internal static IReadOnlyList<Entry> Project(Goal goal, string? candidate,
        IReadOnlyList<Evidence> evidence, Report? report,
        IEnumerable<HumanInputRequest> answers,
        IReadOnlyList<GateTestObligationEvaluator.Result>? evaluated = null)
    {
        var requests = answers.ToArray();
        var entries = new List<Entry>();
        foreach (var obligation in goal.GateTestObligations.Where(o => o.BriefVersion == goal.AuthoritativeBrief.Version))
        {
            var effective = AnsweredSelectorRebinding.ApplySelectorEdits(goal, obligation, requests);
            foreach (var selector in effective.Selectors)
            {
                var descriptions = new List<string>();
                var current = new List<Evidence>();
                foreach (var source in evidence)
                {
                    var reason = source.UnavailableReason;
                    if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(source.Candidate))
                        reason = "candidate binding unavailable";
                    else if (!string.Equals(source.Candidate, candidate, StringComparison.OrdinalIgnoreCase))
                        reason = "stale candidate";
                    else if (source.BriefVersion != goal.AuthoritativeBrief.Version)
                        reason = "stale or unavailable brief binding";
                    if (obligation.PlannerIdentity is { } planner &&
                        (planner.GoalId != goal.Id.Value || planner.BriefVersion != goal.AuthoritativeBrief.Version))
                        reason = "stale Planner brief/goal provenance";
                    descriptions.Add($"{source.Kind}: {source.Source}; candidate={source.Candidate ?? "unknown"}; " +
                        $"brief={source.BriefVersion?.ToString() ?? "unknown"}; " +
                        (reason is null ? "available/current" : $"unknown: {reason}"));
                    if (reason is null) current.Add(source);
                }
                if (descriptions.Count == 0) descriptions.Add("unknown: missing candidate-bound identity evidence");
                var matched = current.Any(source =>
                    ProspectiveEvidenceTrxRows.Match(selector, source.Rows) is { Length: > 0 } matches &&
                        ProspectiveEvidenceTrxRows.AmbiguousClassNames(matches) is null ||
                    DeclaredTestSelectorResolver.Evaluate(selector, source.DeclaredRows) is var declared &&
                        declared.Passed + declared.Failed + declared.Superseded + declared.Skipped > 0 ||
                    source.Checks?.Declared.Any(check => !GateCheckSelectorMatcher.IsTestResultProducing(check) &&
                        GateCheckSelectorMatcher.Matches(selector, check)) == true);
                // Preserve the evaluator's authorized legacy rebind/rename semantics, including provenance.
                var gate = evaluated?.FirstOrDefault(result => result.Obligation.Id == obligation.Id);
                if (gate is { Outcome: GateTestObligationEvaluator.Outcome.Satisfied } && current.Count > 0)
                {
                    matched = true;
                    descriptions.Add($"existing evaluator source={gate.SourceId}; {gate.AnswerRebindProvenance}; {gate.RenameProvenance}");
                }
                var status = matched ? Resolution.Resolved : current.Any(source => source.Complete)
                    ? Resolution.Unresolved : Resolution.Unknown;
                var identities = current.SelectMany(source => source.DeclaredRows.Concat(source.Rows.Select(row =>
                    new DeclaredTestReportRows.Row(row.FullIdentity, row.ShortIdentity, [], "NotExecuted", row.Path))))
                    .DistinctBy(row => row.Identity, StringComparer.Ordinal).ToArray();
                var assertions = report is not null && report.CurrentBrief && !string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(report.Candidate, candidate, StringComparison.OrdinalIgnoreCase)
                    ? report.Assertions : [];
                // Ranking is the established advisory helper, never selector resolution.
                var suggestions = status == Resolution.Resolved ? [] : DeclaredTestClosestIdentities.Find([selector],
                    identities.Concat(assertions.Select(name =>
                        new DeclaredTestReportRows.Row(name, name, [], "NotExecuted", "Developer assertion"))).ToArray(), 5);
                var total = identities.Select(row => row.Identity).Concat(assertions).Distinct(StringComparer.Ordinal).Count();
                var effectiveSelector = gate is { Outcome: GateTestObligationEvaluator.Outcome.Satisfied, AnswerRebinds.Count: > 0 }
                    ? string.Join('.', selector.Split('.').Select(segment =>
                        gate.AnswerRebinds.FirstOrDefault(rebind => rebind.OldClass == segment)?.NewClass ?? segment))
                    : selector;
                entries.Add(new(obligation.Id, obligation.PlannerIdentity?.CriterionIndex,
                    obligation.PlannerIdentity?.StableId, obligation.RequestingTaskId, obligation.BriefVersion,
                    obligation.Selectors, effectiveSelector, candidate, status, descriptions, suggestions,
                    suggestions.Count > 1, total > suggestions.Count && status != Resolution.Resolved,
                    suggestions.Count == 0 ? "no reliable suggestions available" : "advisory only; no criterion coverage inferred",
                    report is null ? "accepted Developer report unavailable" :
                        $"{report.Source}; {(assertions.Count > 0 ? "current assertions only" : "unavailable/stale or no assertions")}",
                    assertions, AnsweredSelectorRebinding.SelectorEditsFor(goal, obligation, requests)));
            }
        }
        return entries;
    }

    internal static IReadOnlyList<string> Render(IReadOnlyList<Entry> entries) => entries.Count == 0 ? [] :
        [Marker + ": advisory identity projection; declarations and Developer assertions do not prove execution, " +
            "satisfy obligations, waive gates, or authorize selector changes. Use audited obligation-rebind for changes.",
         .. entries.Select(entry => Marker + " " + JsonSerializer.Serialize(entry))];
}
