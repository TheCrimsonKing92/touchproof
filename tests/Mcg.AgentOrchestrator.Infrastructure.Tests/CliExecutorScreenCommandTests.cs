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

    [Fact]
    public async Task WholeGateRerunConsumesTheBudgetBeforeTheNextCandidate()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var at = fixture.Clock.GetUtcNow();
        var next = fixture.Candidate with { GoalId = "fedcba9876543210", Commit = new string('e', 40) };
        StoreAttempt(fixture, fixture.Candidate.GoalId, "newest-passed", fixture.Candidate.Commit, at,
            ConductorParallelAcceptanceAttemptOutcome.Passed);
        StoreAttempt(fixture, next.GoalId, "older-passed", next.Commit, at.AddMinutes(-1),
            ConductorParallelAcceptanceAttemptOutcome.Passed);
        fixture.Executor.Submit = (request, _) => Task.FromResult(
            fixture.Submission(request, failure: request.VerifyingCommitSha == fixture.Candidate.Commit &&
                request.Filter == "FullyQualifiedName~A"));

        Assert.Equal(0, await Run(fixture, WriteConfiguration(fixture, "off"), [fixture.Candidate, next], output, error,
            ["executor-screen", "--whole-gate", "--executor", "laptop", "--max-candidates", "2", "--max-lanes", "2"]));

        Assert.Equal(new[] { "FullyQualifiedName~A", "Name~Suite.ExampleTests.Broken" },
            fixture.Executor.Requests.Select(request => request.Filter));
        Assert.All(fixture.Executor.Requests, request => Assert.Equal(fixture.Candidate.Commit, request.VerifyingCommitSha));
        var receipt = Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath));
        Assert.Equal(2, receipt.SubmissionAttempts);
        Assert.Equal(AcceptanceLaneRerunEvidence.Flake, Assert.Single(receipt.Lanes).Rerun?.Outcome);
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
        StoreAttempt(fixture, fixture.Candidate.GoalId, "passed", fixture.Candidate.Commit,
            fixture.Clock.GetUtcNow().AddMinutes(-1), ConductorParallelAcceptanceAttemptOutcome.Passed);
        Assert.Equal(0, await Run(fixture, path, [fixture.Candidate], output, error,
            ["executor-screen", "--whole-gate", "--executor", "laptop"]));
        var receipt = Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath));
        Assert.Equal(fixture.Candidate.Commit, receipt.CandidateCommit);
        Assert.Equal("laptop", receipt.ExecutorId);
        Assert.Equal(1, Assert.Single(receipt.Lanes).Passed);
        Assert.Empty(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath));
        Assert.Equal("laptop", Assert.Single(fixture.Executor.Requests).ExecutorId);
        Assert.Equal("passed", receipt.LocalAttempt?.AttemptId);
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
    public async Task WholeGateSelectsOnlyPassedAttemptsAndKeepsTheSelectedAttemptBoundToItsCommit()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var at = fixture.Clock.GetUtcNow();
        var historical = fixture.Candidate with { Commit = new string('d', 40) };
        var otherGoal = fixture.Candidate with { GoalId = "fedcba9876543210", Commit = new string('e', 40) };
        var failedOnly = fixture.Candidate with { GoalId = "f0123456789abcde" };
        StoreAttempt(fixture, historical.GoalId, "historical", historical.Commit, at.AddMinutes(-3),
            ConductorParallelAcceptanceAttemptOutcome.Passed);
        StoreAttempt(fixture, historical.GoalId, "later-failed-same-commit", historical.Commit, at.AddMinutes(-2),
            ConductorParallelAcceptanceAttemptOutcome.Failed);
        StoreAttempt(fixture, historical.GoalId, "current-failed", fixture.Candidate.Commit, at.AddMinutes(-1),
            ConductorParallelAcceptanceAttemptOutcome.Failed);
        StoreAttempt(fixture, otherGoal.GoalId, "other-passed", otherGoal.Commit, at.AddMinutes(-2),
            ConductorParallelAcceptanceAttemptOutcome.Passed);
        StoreAttempt(fixture, failedOnly.GoalId, "failed-only", failedOnly.Commit, at,
            ConductorParallelAcceptanceAttemptOutcome.Failed);
        var path = WriteConfiguration(fixture, "off");
        ExecutorScreeningCandidate[] candidates = [fixture.Candidate, otherGoal, failedOnly];

        Assert.Equal(0, await Run(fixture, path, candidates, output, error,
            ["executor-screen", "--whole-gate", "--executor", "laptop", "--max-candidates", "3", "--max-lanes", "1"]));
        Assert.Equal(otherGoal.Commit, Assert.Single(fixture.Executor.Requests).VerifyingCommitSha);
        Assert.Equal("other-passed", Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath)).LocalAttempt?.AttemptId);

        Assert.Equal(0, await Run(fixture, path, candidates, output, error,
            ["executor-screen", "--whole-gate", "--executor", "laptop", "--goal", "0123"]));
        Assert.Equal(new[] { otherGoal.Commit, historical.Commit }, fixture.Executor.Requests.Select(r => r.VerifyingCommitSha));
        var selected = Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath),
            receipt => receipt.GoalId == historical.GoalId);
        Assert.Equal(historical.Commit, selected.CandidateCommit);
        Assert.Equal(historical.Commit, selected.LocalAttempt?.CandidateCommit);
        Assert.Equal("historical", selected.LocalAttempt?.AttemptId);
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData(null, "no-eligible-candidates", 0)]
    [InlineData("012", "goal-has-no-passed-gate", 2)]
    [InlineData("0", "goal-ambiguous", 2)]
    [InlineData("xyz", "goal-unknown", 2)]
    public async Task WholeGateSelectionFailuresSubmitNothing(string? prefix, string reason, int exitCode)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        StoreAttempt(fixture, fixture.Candidate.GoalId, "failed", fixture.Candidate.Commit,
            fixture.Clock.GetUtcNow(), ConductorParallelAcceptanceAttemptOutcome.Failed);
        string[] args = ["executor-screen", "--whole-gate", "--executor", "laptop"];
        if (prefix is not null) args = [.. args, "--goal", prefix];
        Assert.Equal(exitCode, await Run(fixture, WriteConfiguration(fixture, "off"),
            [fixture.Candidate, fixture.Candidate with { GoalId = "0fedcba987654321" }], output, error, args));
        Assert.Equal($"SCREEN {(exitCode == 0 ? "idle" : "refused")} reason={reason}{Environment.NewLine}", output.ToString());
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Theory]
    [InlineData("--max-lanes", "0", true)]
    [InlineData("--max-lanes", "1", false)]
    [InlineData("--goal", "012", false)]
    [InlineData("--goal", " ", true)]
    public async Task WholeGateOptionsValidateTheirScopeAndValue(string flag, string value, bool wholeGate)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = ["executor-screen", "--executor", "laptop", flag, value];
        if (wholeGate) args = [.. args, "--whole-gate"];
        Assert.Equal(2, await Run(fixture, WriteConfiguration(fixture, "shadow"), [fixture.Candidate], output, error, args));
        Assert.Equal(CliCommandHelp.ExecutorScreenUsage + Environment.NewLine, error.ToString());
        Assert.Empty(fixture.Executor.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalComparisonUsesExactAttemptDurationAndPreservesRecordedLaneFailure(bool recordLaneFailure)
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
        var storedLaneFailure = recordLaneFailure
            ? new AcceptanceCheckResult(laneName, false, 1, null, CompletionDecision: new(false,
                AcceptanceShardCompletionPredicates.NonzeroExit, false, 1, 3, 3, "Completed"))
            : null;
        StoreAttempt("matching", commit, baselineCutoff.AddMinutes(-1), ConductorParallelAcceptanceAttemptOutcome.Failed,
            storedLaneFailure);
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
        Assert.Equal(recordLaneFailure ? AcceptanceShardCompletionPredicates.NonzeroExit : null, lane.ExecutionFailure);
        var trial = new ExecutorWholeGateTrialReceipt("trial", fixture.Candidate.GoalId, commit, "laptop", baselineCutoff,
            600, ExecutorWholeGateTrialStatus.Complete, [new(laneName, 60, 3, 0, [])],
            ExecutorWholeGateTrialMemory.Unavailable("fixture"), comparison, []);
        var parity = recordLaneFailure ? $"different lanes=[{laneName}]" : "identical";
        Assert.Equal($"TRIAL compare executor/local=0.50 parity={parity}", ExecutorWholeGateTrialComparison.Format(trial));

        void StoreAttempt(string id, string sha, DateTimeOffset completedAt, ConductorParallelAcceptanceAttemptOutcome outcome,
            AcceptanceCheckResult? failure = null)
        {
            File.WriteAllText(Path.Combine(directory, id + ".attempt.json"), JsonSerializer.Serialize(new
            {
                AttemptId = id, fixture.Candidate.GoalId, Kind = "gate", Outcome = outcome,
                StartedAt = completedAt.AddMinutes(-20), CompletedAt = completedAt, BranchHeadSha = sha,
                MainHeadSha = fixture.Candidate.MainSha, TestResultPaths = new[] { trxName }
            }));
            File.WriteAllText(Path.Combine(directory, id + ".result.json"), JsonSerializer.Serialize(new
            {
                Kind = "gate", BranchHeadSha = sha, MainHeadSha = fixture.Candidate.MainSha,
                Acceptance = failure is null ? null : new AcceptanceVerificationSummary(false, [failure],
                    FailedChecks: [laneName], CheckNames: [laneName])
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
        Assert.Equal("SCREEN idle reason=slot-busy slot=1" + Environment.NewLine, output.ToString());
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task NamedExecutorReportsTheLiveSlotHolderWithoutWaiting()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var occupancy = new CliExecutorScreenWaitTests.SlotOccupancy { BusySlot = 1, BusyMarkerVisible = true };

        Assert.Equal(0, await Run(fixture, WriteConfiguration(fixture, "shadow"), [fixture.Candidate], output, error,
            ["executor-screen", "--executor", "laptop"], occupancy));

        Assert.Equal("SCREEN idle reason=slot-busy slot=1 attempt=external class=FocusedEvidence" + Environment.NewLine,
            output.ToString());
        Assert.Empty(occupancy.ClaimAttempts);
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,5")]
    public async Task InvalidWaitMinutesReportUsage(string? value)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = value is null ? new[] { "executor-screen", "--wait" } : ["executor-screen", "--wait", value];

        Assert.Equal(2, await Run(fixture, WriteConfiguration(fixture, "shadow"), [fixture.Candidate], output, error, args));
        Assert.Equal(CliCommandHelp.ExecutorScreenUsage + Environment.NewLine, error.ToString());
        Assert.Empty(fixture.Executor.Requests);
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
        CliCommandHelp.ThrowIfInvalidFlags(["executor-screen", "--whole-gate", "--executor", "mcg-exec-1",
            "--goal", "0123", "--max-lanes", "1", "--wait", "0.5"]);
        Assert.Contains("--whole-gate", CliCommandHelp.ExecutorScreenUsage);
        Assert.Contains("--goal", CliCommandHelp.ExecutorScreenUsage);
        Assert.Contains("--max-lanes", CliCommandHelp.ExecutorScreenUsage);
        Assert.Contains("--wait <minutes>", CliCommandHelp.ExecutorScreenUsage);
        Assert.ThrowsAny<Exception>(() => CliCommandHelp.ThrowIfInvalidFlags(["executor-screen", "--unknown"]));
        Assert.Contains("executor-screen", CliArgumentParser.RecognizedCommands);
        Assert.True(CliExecutorScreenCommand.IsCommand(["executor-screen"]));
    }

    private static Task<int> Run(Fixture fixture, string config, IReadOnlyList<ExecutorScreeningCandidate> candidates,
        TextWriter output, TextWriter error, string[]? args = null, IExecutorSlotOccupancy? occupancy = null) =>
        CliExecutorScreenCommand.RunAsync(
            args ?? ["executor-screen"], config, () => candidates,
            _ => (new[] { new AcceptanceTestLane("A", "FullyQualifiedName~A") }, TimeSpan.FromHours(1)),
            (_, _, prefix) =>
            {
                Assert.StartsWith(Path.Combine(fixture.Root, ".orchestrator", "executor-screening"), prefix);
                return fixture.Executor;
            }, fixture.Clock, TimeSpan.FromMilliseconds(1), fixture.Root, fixture.ReceiptPath, output, error,
            trialPlanSource: _ => new(
                [new("infrastructure tests: A", ExecutorScreeningPlanner.Project, "FullyQualifiedName~A", [], 1,
                    TimeSpan.FromHours(1), RetryCheck: new()
                    {
                        Name = "infrastructure tests: A", Type = "dotnet-test", Runner = "mtp",
                        Project = ExecutorScreeningPlanner.Project, Arguments = ["--filter", "FullyQualifiedName~A"],
                        IsResolvedInfrastructureLane = true
                    })],
                [], AcceptanceDeclaredTestSettings.Empty),
            localAttemptSource: (candidate, plan, at) => CliExecutorScreenCommand.ReadLocalAttempt(fixture.Root, candidate, plan, at),
            passedGateSource: goal => CliExecutorScreenCommand.ReadPassedGates(fixture.Root, goal, CancellationToken.None),
            goalIds: () => candidates.Select(candidate => candidate.GoalId),
            trialCheckout: gate => (candidates.Single(candidate => candidate.GoalId == gate.GoalId) with
            {
                Commit = gate.Commit, MainSha = gate.MainSha, PassedGateAttemptId = gate.AttemptId
            }, null), occupancy: occupancy);

    private static void StoreAttempt(Fixture fixture, string goalId, string id, string commit, DateTimeOffset completed,
        ConductorParallelAcceptanceAttemptOutcome outcome)
    {
        var directory = Path.Combine(fixture.Root, "acceptance-gate-attempts", goalId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".attempt.json"), JsonSerializer.Serialize(new
        {
            AttemptId = id, GoalId = goalId, Kind = "gate", Outcome = outcome, StartedAt = completed.AddMinutes(-1),
            CompletedAt = completed, BranchHeadSha = commit, MainHeadSha = fixture.Candidate.MainSha
        }));
        File.WriteAllText(Path.Combine(directory, id + ".result.json"), JsonSerializer.Serialize(new
        {
            Kind = "gate", BranchHeadSha = commit, MainHeadSha = fixture.Candidate.MainSha
        }));
    }

    internal static string WriteConfiguration(Fixture fixture, string mode)
    {
        var path = Path.Combine(fixture.Root, "remote-lanes.json");
        File.WriteAllText(path, """
            {"executors":[{"id":"laptop","transport":"ssh","runnerAlias":"runner","adminAlias":"admin",
              "remoteRepository":"C:/repo","slots":2}],"lanes":["*"],"screening":{"mode":"MODE"}}
            """.Replace("MODE", mode, StringComparison.Ordinal));
        return path;
    }
}
