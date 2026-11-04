using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Attempt = Mcg.AgentOrchestrator.App.Orchestration.GateFailureClassificationReader.Attempt;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GateFailureClassificationReport(string Form, int Attempts, IReadOnlyList<GateFailureClassificationReport.Row> Rows,
    IReadOnlyList<GateFailureClassificationReport.CauseCount> CauseCounts,
    IReadOnlyList<GateFailureClassificationReport.RecurringTest> RecurringTests,
    IReadOnlyList<string> ReadProblems, IReadOnlyList<string> Excluded, int Skipped)
{
    internal sealed record RunOutcome(int Run, string Outcome);
    internal sealed record TestOutcome(string Lane, string Test, string FirstRunOutcome, string RerunOutcome, IReadOnlyList<RunOutcome> Reruns);
    internal sealed record Denial(string Lane, string Reason);
    internal sealed record Row(string AttemptId, string GoalPrefix, DateTimeOffset? StartedAt, string ResultKind, string Cause,
        IReadOnlyList<string> FailedChecks, IReadOnlyList<TestOutcome> Tests, IReadOnlyList<Denial> Denials,
        IReadOnlyList<string> DecisiveEvidence, IReadOnlyList<string> EvidenceGaps);
    internal sealed record CauseCount(string Cause, int Count);
    internal sealed record RecurringTest(string Test, int Attempts);

    internal static GateFailureClassificationReport Read(string directory, int last)
    {
        var problems = new List<string>();
        var attempts = GateFailureClassificationReader.Read(directory, last, problems, out var skipped);
        var home = attempts.Where(attempt => attempt.Exclusion is null).ToArray();
        var rows = home.Where(attempt => !(attempt.Kind == "accepted" && attempt.Passed == true && attempt.ResultProblems.Count == 0))
            .Select(Classify).ToArray();
        var counts = new[] { "identity-stale", "early-outcome", "apparatus-or-environment", "flake-denied", "candidate", "unattributed" }
            .Select(cause => new CauseCount(cause, rows.Count(row => row.Cause == cause))).ToArray();
        // A passing attempt can still contain a credited flake. Count distinct test
        // identities once per attempt, across all lanes and run ordinals in the window.
        var recurring = home.SelectMany(attempt => Tests(attempt).Select(test => test.Test).Distinct(StringComparer.Ordinal))
            .GroupBy(test => test, StringComparer.Ordinal).Where(group => group.Count() >= 2)
            .Select(group => new RecurringTest(group.Key, group.Count()))
            .OrderByDescending(test => test.Attempts).ThenBy(test => test.Test, StringComparer.Ordinal).ToArray();
        foreach (var attempt in home)
            foreach (var problem in attempt.Problems.Concat(attempt.ResultProblems)) problems.Add($"{attempt.Id}: {problem}");
        return new("failures", home.Length, rows, counts, recurring, problems,
            attempts.Where(attempt => attempt.Exclusion is not null).Select(attempt => $"{attempt.Id}: {attempt.Exclusion}").ToArray(), skipped);
    }

    private static Row Classify(Attempt attempt)
    {
        var gaps = attempt.ResultProblems.Concat(attempt.Problems).ToList();
        var evidence = new List<string>();
        var denials = Regex.Matches(attempt.Log ?? "", "^LANE_RERUN_CREDIT_DENIED lane=\"(?<lane>[^\"]+)\" reason=(?<reason>[^\\s]+)", RegexOptions.Multiline)
            .Select(match => new Denial(match.Groups["lane"].Value, match.Groups["reason"].Value)).ToArray();
        var cause = "unattributed";
        if (attempt.ResultProblems.Count == 0)
        {
            // Adjustment to the brief's terminal rule: identity-stale is a faultKind,
            // not state/earlyOutcomeKind. Producer: src/Mcg.AgentOrchestrator.App/
            // Orchestration/ConductorParallelAcceptanceAttempts.cs:2914-2952.
            if (attempt.FaultKind == "identity-stale")
            { cause = "identity-stale"; evidence.Add("faultKind=identity-stale"); }
            else if (attempt.Kind is "early-held" or "early-escalated" or "early-done" or "early-executed")
            {
                cause = "early-outcome";
                evidence.Add($"kind={attempt.Kind}; state={attempt.State ?? "unknown"}; earlyOutcomeKind={attempt.EarlyOutcomeKind ?? "not-recorded"}");
            }
            else if (attempt.Problems.Count == 0)
            {
                if (attempt.FaultKind is "blocked-build-slot" or "blocked-build-lock" or "structural-coverage-permit-unavailable" or
                    "infrastructure-deferred" or "gate-engine-fault" || !string.IsNullOrWhiteSpace(attempt.InfrastructureReasonCode))
                {
                    // Explicit apparatus faults and reason code are written by the
                    // same producer at :2917-2935, never inferred from check names.
                    cause = "apparatus-or-environment";
                    evidence.Add($"faultKind={attempt.FaultKind ?? "not-recorded"}; infrastructureReasonCode={attempt.InfrastructureReasonCode ?? "not-recorded"}");
                }
                else if (attempt.Kind == "accepted" && attempt.Passed == false)
                {
                    var lanes = AcceptanceAttributionLaneScope.FromCombinedFailedChecks(attempt.FailedChecks)?.Names ?? attempt.FailedChecks;
                    var attributions = attempt.Attributions.Where(attribution => lanes.Contains(attribution.CheckName, StringComparer.Ordinal)).ToArray();
                    // Source enum meanings: src/Mcg.AgentOrchestrator.Core/Domain/Goal.cs:1804,1811.
                    // Apply AcceptanceFailureSummary.IsEnvironmentalApparatus (:1833)
                    // to the source-owned lane scope (AcceptanceAttributionLaneScope.cs:20).
                    // Every failure must be covered; a mixed candidate/environment attempt
                    // cannot be called wholly environmental from one check's evidence.
                    if (lanes.Count > 0 && lanes.All(lane => attributions.Any(attribution => attribution.CheckName == lane &&
                        attribution.Cause == (int)AcceptanceFailureCause.EnvironmentalApparatus)))
                    {
                        cause = "apparatus-or-environment";
                        evidence.AddRange(attributions.Where(attribution => attribution.Cause == (int)AcceptanceFailureCause.EnvironmentalApparatus).Select(Describe));
                    }
                    else if (lanes.Count > 0 && lanes.All(lane => denials.Any(denial => denial.Lane == lane) && PassedRerun(attempt, lane)))
                    {
                        // Denial alone is insufficient: not-eligible can mean a failed
                        // probe (src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/
                        // AcceptanceProbeRerunCredit.cs:36). Require positive rerun evidence too.
                        cause = "flake-denied";
                        foreach (var lane in lanes)
                        {
                            var run = LatestRerun(attempt, lane);
                            evidence.Add(run is null ? $"LANE_RERUN lane=\"{lane}\" outcome=flake" :
                                $"{attempt.Id}.{run.Lane}-run-{run.Run}.trx: outcomes={string.Join(',', run.Results.Select(test => test.Outcome).Distinct(StringComparer.Ordinal))}; passing rerun");
                            evidence.AddRange(denials.Where(denial => denial.Lane == lane)
                                .Select(denial => $"LANE_RERUN_CREDIT_DENIED lane=\"{lane}\" reason={denial.Reason}"));
                        }
                    }
                    else if (attempt.Attributions.Any(attribution => attempt.FailedChecks.Contains(attribution.CheckName, StringComparer.Ordinal) &&
                        attribution.Origin == (int)AcceptanceFailureOrigin.Introduced))
                    {
                        // Introduced is candidate attribution, not the word 'infrastructure'
                        // in a check name. Producer: Orchestration/CleanTestBaseline.cs:328.
                        cause = "candidate";
                        evidence.AddRange(attempt.Attributions.Where(attribution => attempt.FailedChecks.Contains(attribution.CheckName, StringComparer.Ordinal) &&
                            attribution.Origin == (int)AcceptanceFailureOrigin.Introduced).Select(Describe));
                    }
                    else
                    {
                        gaps.Add("No recorded Introduced origin, complete environmental attribution, or passing rerun plus credit denial for every failing lane");
                        gaps.AddRange(attempt.Attributions.Select(Describe));
                        // FixturePublication=2 has no class here. The conditional receipt
                        // adjudicator in src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/
                        // GoalAcceptanceVerifier.FailureCauseAdjudication.cs:92 requires
                        // validated repository/probe facts; the enum alone supplies none.
                        if (attempt.Attributions.Any(attribution => attribution.Cause == (int)AcceptanceFailureCause.FixturePublication))
                            gaps.Add("FixturePublication cause alone does not record EnvironmentalApparatus receipt adjudication");
                        if (attempt.Attributions.Count == 0) gaps.Add("acceptance.checkAttributions: missing causal evidence");
                    }
                }
                else gaps.Add($"No supported causal evidence for kind={attempt.Kind ?? "unknown"}; faultKind={attempt.FaultKind ?? "not-recorded"}");
            }
        }
        return new(attempt.Id, attempt.GoalPrefix, attempt.StartedAt, attempt.Kind ?? "unknown", cause,
            attempt.FailedChecks, Tests(attempt), denials, evidence, gaps);
    }

    private static string Describe(GateFailureClassificationReader.Attribution attribution) =>
        $"checkName={attribution.CheckName}; origin={attribution.Origin}; cause={attribution.Cause}; evidence={attribution.Evidence ?? "not-recorded"}";

    private static bool PassedRerun(Attempt attempt, string lane)
    {
        var run = LatestRerun(attempt, lane);
        var log = Regex.Matches(attempt.Log ?? "", "^LANE_RERUN lane=\"(?<lane>[^\"]+)\" outcome=(?<outcome>[^\\s]+)", RegexOptions.Multiline)
            .LastOrDefault(match => match.Groups["lane"].Value == lane);
        var logged = log?.Groups["outcome"].Value;
        // Reject conflicting/partial recorded outcomes; a lane log never supplies an
        // individual test outcome. TRX fallback requires positive passes and only
        // passing/skipped outcomes, never an unknown or fatal outcome.
        if (logged is not null and not "flake") return false;
        if (run is not null) return run.Results.Any(test => test.Outcome == "Passed") &&
            run.Results.All(test => test.Outcome is "Passed" or "NotExecuted");
        return logged == AcceptanceLaneRerunEvidence.Flake;
    }

    private static GateStatsReader.LaneRun? LatestRerun(Attempt attempt, string lane) =>
        attempt.Runs.Where(run => run.Lane == AcceptanceArtifactFileNames.Slug(lane) && run.Run > 0).MaxBy(run => run.Run);

    private static IReadOnlyList<TestOutcome> Tests(Attempt attempt) => attempt.Runs
        .GroupBy(run => run.Lane, StringComparer.Ordinal).SelectMany(lane => lane
            .SelectMany(run => run.Results.Where(test => Failed(test.Outcome)).Select(test => test.Test ?? test.Class))
            .Distinct(StringComparer.Ordinal).Select(test =>
            {
                string Outcome(GateStatsReader.LaneRun run) => string.Join(',', run.Results.Where(result => (result.Test ?? result.Class) == test)
                    .Select(result => result.Outcome ?? "unknown").Distinct(StringComparer.Ordinal)) is { Length: > 0 } outcome ? outcome : "not-recorded";
                var first = lane.FirstOrDefault(run => run.Run == 0);
                var reruns = lane.Where(run => run.Run > 0).OrderBy(run => run.Run).Select(run => new RunOutcome(run.Run, Outcome(run))).ToArray();
                return new TestOutcome(lane.Key, test, first is null ? "not-recorded" : Outcome(first),
                    reruns.LastOrDefault()?.Outcome ?? "not-recorded", reruns);
            })).OrderBy(test => test.Lane, StringComparer.Ordinal).ThenBy(test => test.Test, StringComparer.Ordinal).ToArray();

    private static bool Failed(string? outcome) => AcceptanceTrxOutcomeTaxonomy.IsFatal(outcome);
}
