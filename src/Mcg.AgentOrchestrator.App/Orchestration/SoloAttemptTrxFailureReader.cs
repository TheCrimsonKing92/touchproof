using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SoloAttemptTrxFailureReader
{
    internal static AttributedFailure? Read(Goal goal, ConductorParallelAcceptanceAttempt? attempt)
    {
        var failure = goal.LatestAcceptanceFailure;
        if (failure is null || attempt is null || attempt.GoalId != goal.Id.Value ||
            string.IsNullOrWhiteSpace(failure.AttemptId) || attempt.AttemptId != failure.AttemptId ||
            string.IsNullOrWhiteSpace(failure.BranchHeadSha) || attempt.BranchHeadSha != failure.BranchHeadSha ||
            attempt.MainHeadSha != failure.MainHeadSha) return null;
        try
        {
            var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                File.ReadAllText(attempt.ResultPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var summary = artifact?.Acceptance;
            if (summary is null || summary.Passed || summary.AttemptId != attempt.AttemptId ||
                summary.BranchHeadSha != attempt.BranchHeadSha || summary.MainHeadSha != attempt.MainHeadSha ||
                artifact!.BranchHeadSha != attempt.BranchHeadSha || artifact.MainHeadSha != attempt.MainHeadSha ||
                summary.CheckAttributions?.Any(check => check.Origin == AcceptanceFailureOrigin.Inherited) == true)
                return null;
            var checks = summary.RequiredUnmetCriteria.Where(check => failure.FailedChecks.Contains(check.Name)).ToArray();
            if (checks.Length == 0 || failure.FailedChecks.Any(name => !checks.Any(check => check.Name == name))) return null;
            var rows = new List<(AcceptanceTrxFailure Row, string Path)>();
            foreach (var check in checks)
            {
                if (check.TestResultPaths is not { Count: > 0 } paths ||
                    check.FailingTestIdentities is not { Count: > 0 } identities ||
                    identities.Any(string.IsNullOrWhiteSpace) ||
                    check.FailingTestAttributions?.Any(test => test.Origin == AcceptanceTestFailureOrigin.Inherited) == true)
                    return null;
                var expected = identities.ToHashSet(StringComparer.Ordinal);
                var reads = paths.Select(path => AcceptanceTrxFailureReader.Read(Path.IsPathFullyQualified(path)
                    ? path : Path.GetFullPath(path, Path.GetDirectoryName(attempt.MetadataPath)!))).ToArray();
                if (reads.Any(read => read.Status != AcceptanceTrxReadStatus.Readable)) return null;
                // Narrowed reruns retain first-run TRX too. Only the effective failed
                // check's identities authorize routing; earlier flaked rows do not.
                var failures = reads.SelectMany(read => read.Failures
                    .Where(row => row.Outcome == "Failed" && row.TestName is not null && expected.Contains(row.TestName))
                    .Select(row => (Row: row, read.Path))).ToArray();
                if (expected.Any(identity => !failures.Any(row => row.Row.TestName == identity))) return null;
                rows.AddRange(failures);
            }
            var tests = rows.Select(row => row.Row.TestName!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var detail = string.Join(Environment.NewLine,
                new[] { "No solo receipt or baseline attribution is available; failures read from this attempt's TRX." }
                .Concat(rows.Select(row => $"failed {row.Row.TestName}: {FirstLine(row.Row.Message)} (trx: {row.Path})")));
            return new("solo", attempt.AttemptId, failure.BranchHeadSha, tests, detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            return null;
        }
    }

    private static string FirstLine(string? message)
    {
        var line = message?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(message unavailable)";
        return line.Length > 2000 ? line[..2000] : line;
    }
}
