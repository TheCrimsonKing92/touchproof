using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its attempt and ledger directories.
public sealed class AcceptanceGateOutcomeLedgerPublicationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("gate-outcome-publication-").FullName;
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 18, 1, 0, 0, TimeSpan.Zero);
    internal const string LaunchMain = "launch-main";
    private const string ResultMain = "result-main";

    [Fact]
    public void WrapperPublication_WritesOneRowFromTerminalMetadataAndLanePlan()
    {
        var (coordinator, attempt) = PublishSolo(root, StartedAt);
        var terminal = Assert.IsType<ConductorParallelAcceptanceAttempt>(coordinator.TryReadLatest(attempt.GoalId));
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, terminal.Outcome);
        Assert.Equal(ResultMain, terminal.MainHeadSha);

        AssertPublication(attempt.ResultPath[..^".result.json".Length], StartedAt,
            terminal.StartedAt, LaunchMain, "Passed", ResultMain, hasSoloMetadata: true);
    }

    [Fact]
    public async Task OwnedTrainPublication_WritesOneRowFromResultAndLanePlan()
    {
        var prefix = await PublishOwnedTrain();
        AssertPublication(prefix, StartedAt, StartedAt, LaunchMain, "Failed", LaunchMain, hasSoloMetadata: false);
    }

    [Fact]
    public async Task OwnedTrainPublication_DoesNotDuplicateAnObservationAppendedByBackfill()
    {
        var prefix = await PublishOwnedTrain(() =>
        {
            var reader = new CostGoalOutcomeGateLedgerReader(root);
            Assert.Single(reader.Read(StartedAt.AddMinutes(1)));
            Assert.Equal(1, reader.GateFileReads);
        });

        var lines = File.ReadAllLines(Path.Combine(root, AcceptanceGateOutcomeLedger.FileName));
        var observations = lines.Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(row => row.GetProperty("kind").ValueEquals(AcceptanceGateOutcomeLedger.GateKind));
        Assert.Equal(Path.GetFileName(prefix), Assert.Single(observations).GetProperty("attemptId").GetString());
        Assert.Equal(2, lines.Length);
    }

    private async Task<string> PublishOwnedTrain(Action? resultWritten = null)
    {
        var group = GoalId.New().Value;
        var attemptId = FormattableString.Invariant($"{group[..8]}-gate-{StartedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        var directory = Directory.CreateDirectory(Path.Combine(root, "acceptance-gate-attempts", group));
        var prefix = Path.Combine(directory.FullName, attemptId);
        await using var owner = CreateOwner(root, group, prefix, writesResultRecord: true);
        PublishLanePlan(owner);

        owner.TryWriteResultRecord(new AcceptanceVerificationResult(false, false, 1, null,
            TestResultPaths: [prefix + ".trx"]), resultWrittenForTests: resultWritten);
        return prefix;
    }

    internal static (ConductorParallelAcceptanceAttemptCoordinator Coordinator, ConductorParallelAcceptanceAttempt Attempt)
        PublishSolo(string root, DateTimeOffset startedAt)
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("Record a selected gate");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", LaunchMain);
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, "acceptance-gate-attempts"), "main", utcNow: () => startedAt,
            acquireStableSlotLease: (_, _) => null);
        var attempt = coordinator.CreateAttemptForTests(candidate);
        coordinator.RunAttemptForTests(attempt, candidate, ConductorAutonomyPolicy.Permissive,
            (current, _, _, _, options) =>
            {
                var owner = CreateOwner(root, goal.Id.Value, options.ResultsPrefix!, options: options);
                return AcceptanceExecutionOwnerLifetime.Run(owner, () =>
                {
                    PublishLanePlan(owner);
                    return ConductorParallelAcceptanceRunResult.Accepted(current with { MainHeadSha = ResultMain },
                        new AcceptanceVerificationSummary(true, [], TestResultPaths: [options.ResultsPrefix + ".trx"]));
                });
            });
        return (coordinator, attempt);
    }

    private static AcceptanceAttemptExecutionOwner CreateOwner(string root, string goalId, string prefix,
        bool writesResultRecord = false, AcceptanceRunExecutionOptions? options = null) =>
        new(new(Path.GetFileName(prefix), goalId, root, "tree", LaunchMain, "branch", prefix, null, 0, null),
            new AcceptanceGateEngineSettings(), options: options, writesResultRecord: writesResultRecord);

    private static void PublishLanePlan(AcceptanceAttemptExecutionOwner owner)
    {
        var plan = new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, owner.RunId, ["lane"], [])
        {
            SelectedExecution = CostGoalOutcomeBaselineTests.Selection(12.345678901, 17, "fixture full gate")
        };
        AcceptanceGateLanePlanPublisher.Publish(plan, owner.ResultsPrefix, new GoalId(owner.Identity.GoalId),
            owner.ReportProgress, TimeProvider.System);
    }

    private void AssertPublication(string prefix, DateTimeOffset recordedAt, DateTimeOffset startedAt,
        string launchMain, string outcome, string resultMain, bool hasSoloMetadata)
    {
        var plan = JsonSerializer.Deserialize<AcceptanceGateLanePlan>(
            File.ReadAllText(prefix + AcceptanceGateLanePlan.ArtifactSuffix), AcceptanceGateLanePlan.JsonOptions)!;
        var result = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
            File.ReadAllText(prefix + ".result.json"), AcceptanceGateOutcomeLedger.Json)!;
        Assert.Equal("accepted", result.Kind);
        Assert.Equal("gate", result.DispatchKind);
        Assert.Equal(resultMain, result.MainHeadSha);
        Assert.Equal(outcome == "Passed", result.Acceptance!.Passed);

        var line = Assert.Single(File.ReadAllLines(Path.Combine(root, AcceptanceGateOutcomeLedger.FileName)));
        using var json = JsonDocument.Parse(line);
        Assert.Equal("gate", json.RootElement.GetProperty("kind").GetString());
        var selection = plan.SelectedExecution!;
        Assert.Equal(new AcceptanceGateOutcomeObservation(plan.AttemptId, Path.GetFileName(Path.GetDirectoryName(prefix))!,
            recordedAt, startedAt, launchMain, selection.Mode, selection.FullGateReason,
            selection.ExecutedClassCount, selection.SelectionMilliseconds, outcome) { HasSoloMetadata = hasSoloMetadata },
            JsonSerializer.Deserialize<AcceptanceGateOutcomeObservation>(line, AcceptanceGateOutcomeLedger.Json));
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
