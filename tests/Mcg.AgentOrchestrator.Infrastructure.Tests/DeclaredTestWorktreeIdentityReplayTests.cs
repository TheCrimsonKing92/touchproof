using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each replay owns its report, manifest and attempt metadata.
public sealed class DeclaredTestWorktreeIdentityReplayTests
{
    internal const string TestFile = "tests/Get-McgTrxFailure.Tests.ps1";

    [Fact]
    public void AbsoluteWorktreeIdentities_AreRelativeAndSatisfyFileSelector()
    {
        using var fixture = new DeclaredTestReplayFixture();
        var worktreeRoot = GoalWorktreeLayout.WorktreePath(fixture.Gate.Root, fixture.Gate.Goal.Id);
        WriteReport(fixture, worktreeRoot);
        var attempt = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(
            fixture.Gate.OrchestratorDirectory, fixture.Gate.Goal.Id, fixture.Gate.CandidateSha));
        Assert.Equal(worktreeRoot, attempt.WorktreeRoot);
        Assert.Contains(attempt.Checks!.Declared, DeclaredTestReportRows.IsDeclaredCheck);

        var report = DeclaredTestReportRows.Read(attempt.Paths, fixture.Gate.Root,
            attempt.Checks, attempt.WorktreeRoot);

        Assert.True(report.Applies);
        Assert.Equal(new[] { TestFile + "::Get-McgTrxFailure.first", TestFile + "::Get-McgTrxFailure.second" },
            report.Rows.Select(row => row.Identity));
        var result = fixture.Evaluate(TestFile);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(DeclaredTestReplayFixture.SourceId, result.SourceId);
        // The raw report and the rootless read retain their absolute identities.
        var raw = DeclaredTestReportRows.Read(attempt.Paths, fixture.Gate.Root, attempt.Checks);
        Assert.Equal(new[] { worktreeRoot.Replace('\\', '/') + "/" + TestFile + "::Get-McgTrxFailure.first",
            worktreeRoot.Replace('\\', '/') + "/" + TestFile + "::Get-McgTrxFailure.second" },
            raw.Rows.Select(row => row.Identity));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("skipped")]
    public void FileSelector_GradesEverySelectedTestUsingExistingPolicy(string secondStatus)
    {
        using var fixture = new DeclaredTestReplayFixture();
        WriteReport(fixture, GoalWorktreeLayout.WorktreePath(fixture.Gate.Root, fixture.Gate.Goal.Id), secondStatus);

        Assert.Equal(secondStatus == "failed" ? GateTestObligationEvaluator.Outcome.Failed
            : GateTestObligationEvaluator.Outcome.Satisfied, fixture.Evaluate(TestFile).Outcome);
    }

    internal static void WriteReport(DeclaredTestReplayFixture fixture, string worktreeRoot,
        string secondStatus = "passed")
    {
        var document = JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        var file = worktreeRoot.Replace('\\', '/') + "/" + TestFile;
        document["results"]!["tests"] = new JsonArray(
            new JsonObject { ["testId"] = file + "::Get-McgTrxFailure.first",
                ["name"] = "Get-McgTrxFailure.first", ["suite"] = new JsonArray(file), ["status"] = "passed" },
            new JsonObject { ["testId"] = file + "::Get-McgTrxFailure.second",
                ["name"] = "Get-McgTrxFailure.second", ["suite"] = new JsonArray(file), ["status"] = secondStatus });
        File.WriteAllText(fixture.Path, document.ToJsonString());
    }
}
