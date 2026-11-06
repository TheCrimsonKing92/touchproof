using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A read-only projection of the shared classifier, TRX diagnostics and flake census.
internal sealed record GateFailureDetailReport(string AttemptId, string Cause, string VerdictLine,
    IReadOnlyList<GateFailureDetailReport.Test> Tests)
{
    internal sealed record RerunOutcome(int Run, string? Outcome);
    internal sealed record FlakeHistory(int Attempts, int Flakes, int HardFailures);
    internal sealed record Test(string FullTestName, string Lane, string? FailureMessage,
        IReadOnlyList<string> StackTraceLines, string? FirstRunOutcome, IReadOnlyList<RerunOutcome> RerunOutcomes,
        string? TrxPath, FlakeHistory? FlakeHistory);

    internal static GateFailureDetailReport Read(string directory, string attemptId, ICollection<string> warnings)
    {
        var problems = new List<string>();
        // Addressing an explicit id must not depend on the census's default window.
        var attempt = GateFailureClassificationReader.Read(directory, int.MaxValue, problems, out _)
            .SingleOrDefault(attempt => attempt.Id.Equals(attemptId, StringComparison.Ordinal));
        foreach (var problem in problems) warnings.Add(problem);
        if (attempt is null)
            throw new ArgumentException($"gate-stats failure: no recorded gate attempt matches id '{attemptId}'");

        foreach (var problem in attempt.Problems.Concat(attempt.ResultProblems))
        {
            if (problem.Contains("malformed", StringComparison.Ordinal) ||
                problem.Contains("invalid", StringComparison.Ordinal) ||
                problem.Contains("expected JSON object", StringComparison.Ordinal) ||
                problem.Contains("UnauthorizedAccessException", StringComparison.Ordinal) ||
                problem.Contains("IOException", StringComparison.Ordinal))
                throw new InvalidOperationException($"gate-stats failure: {attemptId}: {problem}");
            warnings.Add($"{attemptId}: {problem}");
        }

        var classified = GateFailureClassificationReport.Classify(attempt);
        var history = GateStatsReport.Flakes(directory, 20, int.MaxValue).Tests
            .ToDictionary(row => row.Test, StringComparer.Ordinal);
        var sources = GateStatsReader.DiscoverWaitRuns(directory, out _)
            .Where(run => Path.GetFileName(run.Attempt).Equals(attemptId, StringComparison.Ordinal))
            .ToDictionary(run => (run.Lane, run.Retry), run => run.Attempt + "." + run.Lane +
                (run.Retry == 0 ? "" : $"-run-{run.Retry.ToString(CultureInfo.InvariantCulture)}") + ".trx");
        var diagnostics = new Dictionary<string, AcceptanceTrxReadResult>(StringComparer.Ordinal);
        var tests = classified.Tests.Select(test =>
        {
            var occurrence = attempt.Runs.Where(run => run.Lane == test.Lane && run.Results.Any(result =>
                    (result.Test ?? result.Class) == test.Test && AcceptanceTrxOutcomeTaxonomy.IsFatal(result.Outcome)))
                .OrderBy(run => run.Run).FirstOrDefault();
            AcceptanceTrxFailure? failure = null;
            string? trxPath = null;
            if (occurrence is not null && sources.TryGetValue((occurrence.Lane, occurrence.Run), out var source))
            {
                if (!diagnostics.TryGetValue(source, out var read))
                    diagnostics[source] = read = AcceptanceTrxFailureReader.Read(source);
                if (read.Status is AcceptanceTrxReadStatus.Unreadable or AcceptanceTrxReadStatus.Unparseable)
                    throw new InvalidOperationException($"gate-stats failure: {attemptId}: {source}: {read.Status}: {read.Detail}");
                if (read.Status == AcceptanceTrxReadStatus.Readable)
                {
                    trxPath = Path.GetRelativePath(directory, source);
                    failure = read.Failures.FirstOrDefault(value => value.TestName == test.Test &&
                        AcceptanceTrxOutcomeTaxonomy.IsFatal(value.Outcome));
                }
            }
            history.TryGetValue(test.Test, out var flake);
            return new Test(test.Test, test.Lane, failure?.Message,
                failure?.StackTrace?.ReplaceLineEndings("\n").Split('\n').Take(5).ToArray() ?? [],
                Recorded(test.FirstRunOutcome), test.Reruns.Select(run => new RerunOutcome(run.Run, Recorded(run.Outcome))).ToArray(),
                trxPath, flake is null ? null : new FlakeHistory(flake.Attempts, flake.Flake, flake.Hard));
        }).OrderBy(test => test.Lane, StringComparer.Ordinal).ThenBy(test => test.FullTestName, StringComparer.Ordinal).ToArray();
        return new(attempt.Id, classified.Cause, Verdict(classified), tests);
    }

    private static string? Recorded(string outcome) => outcome == "not-recorded" ? null : outcome;

    // Cause selection and precedence belong exclusively to the shared classifier.
    private static string Verdict(GateFailureClassificationReport.Row row) => row.Cause switch
    {
        "unattributed" => "unattributed",
        "flake-denied" => "failed: " + string.Join("; ", row.Denials.Where(denial => row.DecisiveEvidence.Contains(
            $"LANE_RERUN_CREDIT_DENIED lane=\"{denial.Lane}\" reason={denial.Reason}", StringComparer.Ordinal)).Select(denial =>
        {
            var run = row.Tests.Where(test => test.Lane == AcceptanceArtifactFileNames.Slug(denial.Lane))
                .SelectMany(test => test.Reruns).Where(run => run.Outcome == "Passed").MaxBy(run => run.Run);
            var rerun = run is null ? "rerun passed" : $"rerun {run.Run.ToString(CultureInfo.InvariantCulture)} passed";
            return $"{denial.Lane} {rerun} but credit denied, {denial.Reason}";
        })),
        "candidate" => "candidate: introduced failure attributed to the goal; " + string.Join("; ", row.DecisiveEvidence),
        "apparatus-or-environment" => "apparatus: " + string.Join("; ", row.DecisiveEvidence),
        _ => row.Cause + ": " + string.Join("; ", row.DecisiveEvidence)
    };
}
