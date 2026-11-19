using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

public sealed class CliExecutorScreenCommandTests
{
    [Theory]
    [InlineData("off", "screening-off")]
    [InlineData("on", "screening-invalid")]
    public async Task RefusalDoesNotSubmit(string mode, string reason)
    {
        using var fixture = new Fixture();
        var path = WriteConfiguration(fixture, mode);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await Run(fixture, path, [fixture.Candidate], output, error));
        Assert.Equal("SCREEN refused reason=" + reason + Environment.NewLine, output.ToString());
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("off", "screening-off")]
    [InlineData("on", "screening-invalid")]
    public async Task WholeGateRunsWithNamedExecutorWhenOrdinaryScreeningRefuses(string mode, string refusal)
    {
        using var fixture = new Fixture();
        var path = WriteConfiguration(fixture, mode);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error,
            ["executor-screen", "--whole-gate", "--executor", "laptop"]));
        var receipt = Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath));
        Assert.Equal(fixture.Candidate.Commit, receipt.CandidateCommit);
        Assert.Equal("laptop", receipt.ExecutorId);
        Assert.Equal(1, Assert.Single(receipt.Lanes).Passed);
        Assert.Empty(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath));
        Assert.Equal("laptop", Assert.Single(fixture.Executor.Requests).ExecutorId);
        Assert.Contains("TRIAL compare local attempt missing parity=unavailable", output.ToString());
        Assert.Equal("", error.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(2, await Run(fixture, path, [fixture.Candidate], output, error));
        Assert.Equal("SCREEN refused reason=" + refusal + Environment.NewLine, output.ToString());
        Assert.Single(fixture.Executor.Requests);
    }

    [Fact]
    public async Task WholeGateRequiresAnExecutorName()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await Run(fixture, WriteConfiguration(fixture, "off"), [fixture.Candidate], output, error,
            ["executor-screen", "--whole-gate"]));
        Assert.Equal("", output.ToString());
        Assert.Equal(CliCommandHelp.ExecutorScreenUsage + Environment.NewLine, error.ToString());
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public void LocalComparisonUsesLatestCompletedExactCandidateAndItsRecordedDuration()
    {
        using var fixture = new Fixture();
        const string laneName = "infrastructure tests: A";
        var directory = Path.Combine(fixture.Root, "acceptance-gate-attempts", fixture.Candidate.GoalId);
        Directory.CreateDirectory(directory);
        var baselineCutoff = fixture.Clock.GetUtcNow();
        var trxName = "gate.infrastructure-tests-a.trx";
        var trxPath = Path.Combine(directory, trxName);
        var trx = new XDocument(new XElement("TestRun",
            new XElement("Times", new XAttribute("start", baselineCutoff.AddMinutes(-3).ToString("O")),
                new XAttribute("finish", baselineCutoff.AddMinutes(-2).ToString("O"))),
            new XElement("ResultSummary", new XAttribute("outcome", "Completed"),
                new XElement("Counters", new XAttribute("total", 3), new XAttribute("executed", 3),
                    new XAttribute("passed", 3), new XAttribute("failed", 0)))));
        trx.Save(trxPath);
        var commit = fixture.Candidate.Commit;
        StoreAttempt("earlier", commit, baselineCutoff.AddMinutes(-3), ConductorParallelAcceptanceAttemptOutcome.Passed);
        StoreAttempt("matching", commit, baselineCutoff.AddMinutes(-1), ConductorParallelAcceptanceAttemptOutcome.Failed);
        StoreAttempt("different-sha", fixture.Candidate.Tree, baselineCutoff, ConductorParallelAcceptanceAttemptOutcome.Passed);
        StoreAttempt("completed-after-start", commit, baselineCutoff.AddSeconds(1), ConductorParallelAcceptanceAttemptOutcome.Passed);

        var comparison = CliExecutorScreenCommand.ReadLocalAttempt(fixture.Root, fixture.Candidate,
            new([new(laneName, ExecutorScreeningPlanner.Project, "A", [], 0, TimeSpan.FromHours(1))],
                [], AcceptanceDeclaredTestSettings.Empty), baselineCutoff);

        Assert.NotNull(comparison);
        Assert.Equal("matching", comparison.AttemptId);
        Assert.Equal(commit, comparison.CandidateCommit);
        Assert.Equal(1200, comparison.WallSeconds);
        var lane = Assert.Single(comparison.Lanes);
        Assert.Equal(laneName, lane.LaneName);
        Assert.Equal((60d, 3, 0), (lane.WallSeconds!.Value, lane.Passed!.Value, lane.Failed!.Value));

        void StoreAttempt(string id, string sha, DateTimeOffset completedAt, ConductorParallelAcceptanceAttemptOutcome outcome)
        {
            File.WriteAllText(Path.Combine(directory, id + ".attempt.json"), JsonSerializer.Serialize(new
            {
                AttemptId = id, fixture.Candidate.GoalId, Kind = "gate", Outcome = outcome,
                StartedAt = completedAt.AddMinutes(-20), CompletedAt = completedAt, BranchHeadSha = sha,
                MainHeadSha = fixture.Candidate.MainSha, TestResultPaths = new[] { trxName }
            }));
            File.WriteAllText(Path.Combine(directory, id + ".result.json"), JsonSerializer.Serialize(new
            {
                Kind = "gate", BranchHeadSha = sha, MainHeadSha = fixture.Candidate.MainSha
            }));
        }
    }

    [Fact]
    public async Task DisabledRemoteLanesTakePrecedenceOverInvalidScreening()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "missing.json");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await Run(fixture, path, [], output, error));
        Assert.Equal("SCREEN refused reason=remote-lanes-disabled" + Environment.NewLine, output.ToString());
        Assert.Empty(fixture.Executor.Requests);
    }

    [Fact]
    public async Task PassWritesOneReceiptAndSecondCallFindsNoCandidate()
    {
        using var fixture = new Fixture();
        var path = WriteConfiguration(fixture, "shadow");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error));
        var receipt = Assert.Single(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath));
        Assert.Equal(ExecutorScreeningVerdict.Pass, receipt.Verdict);
        Assert.Equal($"SCREEN goal=01234567 commit=aaaaaaaaa executor=laptop verdict=pass lanes=1 failing=0 excluded=0 receipt={receipt.ReceiptId}" +
            Environment.NewLine, output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error));
        Assert.Equal("SCREEN idle reason=no-candidate" + Environment.NewLine, output.ToString());
        Assert.Single(fixture.Executor.Requests);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task MaximumTwoScreensBothOldestFirst()
    {
        using var fixture = new Fixture();
        var path = WriteConfiguration(fixture, "shadow");
        var older = fixture.Candidate with { GoalId = "older-goal", CommittedAt = fixture.Candidate.CommittedAt.AddHours(-1) };
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate, older], output, error,
            ["executor-screen", "--max-candidates", "2", "--executor", "laptop"]));
        Assert.Equal(new[] { older.GoalId, fixture.Candidate.GoalId },
            ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath).Select(receipt => receipt.GoalId));
        Assert.Equal(2, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("invalid")]
    public async Task InvalidCandidateBoundsAreUsageErrors(string maximum)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await Run(fixture, WriteConfiguration(fixture, "shadow"), [], output, error,
            ["executor-screen", "--max-candidates", maximum]));
        Assert.Equal("", output.ToString());
        Assert.Equal(CliCommandHelp.ExecutorScreenUsage + Environment.NewLine, error.ToString());
        Assert.Empty(fixture.Executor.Requests);
    }

    [Fact]
    public async Task UnknownExecutorIsAUsageError()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await Run(fixture, WriteConfiguration(fixture, "shadow"), [], output, error,
            ["executor-screen", "--executor", "typo"]));
        Assert.Equal("", output.ToString());
        Assert.Equal(CliCommandHelp.ExecutorScreenUsage + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task BusyExecutorAndLostClaimBothPrintIdleWithoutAReceipt()
    {
        using var fixture = new Fixture();
        var path = WriteConfiguration(fixture, "shadow");
        using var output = new StringWriter();
        using var error = new StringWriter();
        RemoteExecutorOccupancy.Claim(fixture.Root, fixture.Entry.Id, "other");
        try
        {
            Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error));
            Assert.Equal("SCREEN idle reason=no-idle-executor" + Environment.NewLine, output.ToString());
        }
        finally { RemoteExecutorOccupancy.Release(fixture.Root, fixture.Entry.Id, "other"); }
        output.GetStringBuilder().Clear();
        using var held = RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, fixture.Entry.Id, 1);
        Assert.NotNull(held);
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error));
        Assert.Equal("SCREEN idle reason=no-idle-executor" + Environment.NewLine, output.ToString());
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task UnreadableManifestCreatesIncompleteReceiptAndDoesNotStarveTheLoop()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliExecutorScreenCommand.RunAsync(["executor-screen", "--max-candidates", "2"],
            WriteConfiguration(fixture, "shadow"), () => [fixture.Candidate],
            _ => throw new IOException("fixture manifest unavailable"), (_, _, _) => fixture.Executor,
            fixture.Clock, TimeSpan.FromMilliseconds(1), fixture.Root, fixture.ReceiptPath, output, error);
        Assert.Equal(0, exit);
        Assert.Equal(ExecutorScreeningVerdict.Incomplete,
            Assert.Single(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath)).Verdict);
        Assert.Contains("verdict=incomplete", output.ToString());
        Assert.Contains("SCREEN idle reason=no-candidate", output.ToString());
        Assert.Empty(fixture.Executor.Requests);
    }

    [Fact]
    public void HelpAcceptsDocumentedFlagsAndRejectsUnknownFlags()
    {
        CliCommandHelp.ThrowIfInvalidFlags(["executor-screen", "--max-candidates", "2", "--executor", "mcg-exec-1"]);
        CliCommandHelp.ThrowIfInvalidFlags(["executor-screen", "--whole-gate", "--executor", "mcg-exec-1"]);
        Assert.Contains("--whole-gate", CliCommandHelp.ExecutorScreenUsage);
        Assert.ThrowsAny<Exception>(() => CliCommandHelp.ThrowIfInvalidFlags(["executor-screen", "--unknown"]));
        Assert.Contains("executor-screen", CliArgumentParser.RecognizedCommands);
        Assert.True(CliExecutorScreenCommand.IsCommand(["executor-screen"]));
    }

    private static Task<int> Run(Fixture fixture, string config, IReadOnlyList<ExecutorScreeningCandidate> candidates,
        TextWriter output, TextWriter error, string[]? args = null) => CliExecutorScreenCommand.RunAsync(
            args ?? ["executor-screen"], config, () => candidates,
            _ => (new[] { new AcceptanceTestLane("A", "FullyQualifiedName~A") }, TimeSpan.FromHours(1)),
            (_, _, prefix) =>
            {
                Assert.StartsWith(Path.Combine(fixture.Root, ".orchestrator", "executor-screening"), prefix);
                return fixture.Executor;
            }, fixture.Clock, TimeSpan.FromMilliseconds(1), fixture.Root, fixture.ReceiptPath, output, error,
            trialPlanSource: _ => new(
                [new("infrastructure tests: A", ExecutorScreeningPlanner.Project, "FullyQualifiedName~A", [], 1, TimeSpan.FromHours(1))],
                [], AcceptanceDeclaredTestSettings.Empty));

    private static string WriteConfiguration(Fixture fixture, string mode)
    {
        var path = Path.Combine(fixture.Root, "remote-lanes.json");
        File.WriteAllText(path, """
            {"executors":[{"id":"laptop","transport":"ssh","runnerAlias":"runner","adminAlias":"admin",
              "remoteRepository":"C:/repo","slots":2}],"lanes":["*"],"screening":{"mode":"MODE"}}
            """.Replace("MODE", mode, StringComparison.Ordinal));
        return path;
    }
}
