using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: in-memory goals, inline coordinators, and per-test artifact directories.
public sealed class FailedLaneSelectedClassesTests
{
    private const string IntegrationLane = "Integration";
    private const string CoreLane = "Core tests";
    private const string UnrelatedLane = "Unrelated";
    private const string FailedIdentity = "Example.FailedTests.First";
    private const string FailedLaneFeedback =
        "Failed lane 'Integration' selected classes: Infrastructure.Tests/AlphaSiblingTests, " +
        "Infrastructure.Tests/FailedTests, Infrastructure.Tests/ZuluSiblingTests";
    private static readonly JsonSerializerOptions ArtifactOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RetainedFailure_UnionsOnlyLanesWithRequiredUnmetChecks()
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record();
        fixture.Trx(FailedIdentity);
        fixture.SaveResult(FailedCheck(), FailedCheck() with { FailingTestIdentities = ["Example.LegacyTests.Case"] },
            new(UnrelatedLane, false, 1, "advisory", Advisory: true),
            new(UnrelatedLane, true, 0, "passed"), new(CoreLane, false, 1, "no failing identity"));
        WritePlan(fixture.Attempt!, "valid");
        fixture.Goal.DeferAcceptanceFailureForRetry();

        Assert.Equal(new[]
        {
            ("Core.Tests", "CoreSiblingTests"), ("Infrastructure.Tests", "AlphaSiblingTests"),
            ("Infrastructure.Tests", "FailedTests"), ("Infrastructure.Tests", "LegacyTests"),
            ("Infrastructure.Tests", "ZuluSiblingTests")
        }, FailedGateFocusedClasses.Read(fixture.Goal, _ => fixture.Attempt));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("other-attempt")]
    public void UnusableLanePlan_PreservesFailedClasses(string scenario)
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record();
        fixture.Trx(FailedIdentity);
        fixture.SaveResult(FailedCheck());
        WritePlan(fixture.Attempt!, scenario);
        fixture.Goal.DeferAcceptanceFailureForRetry();

        Assert.Equal(new[] { ("Infrastructure.Tests", "FailedTests") },
            FailedGateFocusedClasses.Read(fixture.Goal, _ => fixture.Attempt));
    }

    [Fact]
    public void RedSibling_RequestsAllFailedLaneClassesAndRetriesDeveloperBeforeReview()
    {
        using var harness = new DriverHarness();
        const string sibling = "Example.ZuluSiblingTests.Fails";
        harness.RecordRetainedFailure();
        WritePlan(harness.Attempt, "valid");
        harness.RedIdentity = sibling;

        harness.Driver().AdvanceOnce(harness.Goal, ConductorAutonomyPolicy.Permissive);

        const string expectedRequest = "Infrastructure.Tests: FullyQualifiedName~AlphaSiblingTests|" +
            "FullyQualifiedName~FailedTests|FullyQualifiedName~ZuluSiblingTests";
        Assert.Equal([expectedRequest], harness.Requests);
        var receipt = Assert.IsType<PreReviewEvidenceReceipt>(harness.Reviewer.PreReviewEvidenceReceipt);
        Assert.Equal([expectedRequest], receipt.SelectedFocusedTests);
        Assert.Equal(PreReviewEvidenceDisposition.Red, receipt.Disposition);
        Assert.Equal([sibling], receipt.FailingTestIdentities);
        Assert.Equal(harness.Developer.Id, harness.RetriedTask);
        Assert.Equal(WorkTaskStatus.Assigned, harness.Developer.Status);
        Assert.Equal([harness.Developer.Id], harness.DispatchedTasks);
        Assert.Equal(0, harness.GateRuns);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("other-attempt")]
    public void GateRetry_AddsOneLinePerFailedLaneWhenPlanIsUsable(string scenario)
    {
        using var harness = new DriverHarness();
        harness.CompleteReview();
        harness.SaveGateResult(new(IntegrationLane, false, 1, "failed", TestProjectPath: "Infrastructure.Tests"),
            new("Core tests: child check", false, 1, "failed"), new(UnrelatedLane, false, 1, "advisory", Advisory: true));
        WritePlan(harness.Attempt, scenario);

        var result = harness.Driver().AdvanceOnce(harness.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(1, harness.GateRuns);
        Assert.Equal(harness.Developer.Id, harness.RetriedTask);
        Assert.Equal(WorkTaskStatus.Assigned, harness.Developer.Status);
        Assert.Equal(1, harness.Goal.AutomaticAcceptanceRetryCount);
        string[] expected = scenario == "valid"
            ? ["Failed lane 'Core tests' selected classes: Core.Tests/CoreSiblingTests", FailedLaneFeedback]
            : [];
        var message = Assert.IsType<string>(harness.RetryMessage);
        Assert.Equal(expected, LaneLines(message.Split(Environment.NewLine)));
        Assert.Equal(expected, LaneLines(harness.Developer.CriterionRetryFeedback));
    }

    private static IEnumerable<string> LaneLines(IEnumerable<string> lines) =>
        lines.Where(line => line.StartsWith("Failed lane '", StringComparison.Ordinal));

    private static AcceptanceCheckResult FailedCheck() => new("infrastructure tests: Integration: shard", false, 1,
        "failed", TestProjectPath: "Infrastructure.Tests", FailingTestIdentities: [FailedIdentity]);

    private static void WritePlan(ConductorParallelAcceptanceAttempt attempt, string scenario)
    {
        if (scenario == "missing") return;
        var path = Path.Combine(Path.GetDirectoryName(attempt.ResultPath)!, attempt.AttemptId + AcceptanceGateLanePlan.ArtifactSuffix);
        var plan = new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion,
            scenario == "other-attempt" ? "different-attempt" : attempt.AttemptId, [IntegrationLane, CoreLane, UnrelatedLane], [])
        {
            SelectedExecution = AcceptanceSelectedExecutionRecord.Disabled with
            {
                Mode = "Enforce", Decision = "plan", Applied = true,
                Lanes =
                [
                    new(IntegrationLane, "narrowed", ["Example.ZuluSiblingTests", "Example.AlphaSiblingTests",
                        "Example.FailedTests", "Example.AlphaSiblingTests+Nested"], [], null),
                    new(CoreLane, "narrowed", ["Mcg.AgentOrchestrator.Core.Tests.CoreSiblingTests"], [], null),
                    new(UnrelatedLane, "narrowed", ["Mcg.AgentOrchestrator.Infrastructure.Tests.UnrelatedTests"], [], null)
                ]
            }
        };
        File.WriteAllText(path, scenario == "malformed" ? "{" : plan.ToJson());
    }

    private sealed class DriverHarness : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly AgentOrchestratorKernel _kernel;
        private AcceptanceVerificationSummary? _gateResult;
        internal Goal Goal { get; }
        internal TaskSpec Developer { get; }
        internal TaskSpec Reviewer { get; }
        internal ConductorParallelAcceptanceAttempt Attempt { get; }
        internal List<string> Requests { get; } = [];
        internal List<TaskId> DispatchedTasks { get; } = [];
        internal TaskId? RetriedTask { get; private set; }
        internal string? RetryMessage { get; private set; }
        internal string? RedIdentity { get; set; }
        internal int GateRuns { get; private set; }

        internal DriverHarness()
        {
            (_kernel, Goal) = ReviewGoalWithoutTester();
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            PassVerification(_kernel, Goal, Developer, hasCommittedChanges: true);
            var directory = Path.Combine(_root, "gate", Goal.Id.Value);
            Directory.CreateDirectory(directory);
            Attempt = new("failed-attempt", Goal.Id.Value, Goal.Id.Value[..8], 0, "failed-candidate", "main",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, ConductorParallelAcceptanceAttemptOutcome.Failed,
                "stdout", "stderr", "exit", "heartbeat", Path.Combine(directory, "failed-attempt.result.json"),
                Path.Combine(directory, "failed-attempt.attempt.json"),
                CompletedAt: DateTimeOffset.UnixEpoch, ReconciledAt: DateTimeOffset.UnixEpoch);
            File.WriteAllBytes(Attempt.MetadataPath, JsonSerializer.SerializeToUtf8Bytes(Attempt, ArtifactOptions));
        }

        internal void RecordRetainedFailure()
        {
            var check = FailedCheck();
            SaveGateResult(check);
            _kernel.RecordAcceptanceFailure(Goal.Id, [check.Name], Attempt.BranchHeadSha, Attempt.MainHeadSha,
                attemptId: Attempt.AttemptId);
            Goal.DeferAcceptanceFailureForRetry();
            Assert.Equal(Attempt.AttemptId, Goal.RetainedAcceptanceFailure?.AttemptId);
        }

        internal void CompleteReview() => PassVerification(_kernel, Goal, Reviewer);

        internal void SaveGateResult(params AcceptanceCheckResult[] checks)
        {
            _gateResult = new(false, checks, BranchHeadSha: Attempt.BranchHeadSha, MainHeadSha: Attempt.MainHeadSha,
                AttemptId: Attempt.AttemptId);
            var artifact = new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null, _gateResult,
                Attempt.BranchHeadSha, Attempt.MainHeadSha, null, null);
            File.WriteAllBytes(Attempt.ResultPath, JsonSerializer.SerializeToUtf8Bytes(artifact, ArtifactOptions));
        }

        internal ConductorDriver Driver() => new(
            getFacts: _ => new(WorkspaceExists: true), getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => throw new InvalidOperationException("Unexpected workspace creation"),
            dispatchAndStart: goal =>
            {
                var task = Assert.Single(goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned &&
                    DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Length == 0));
                DispatchedTasks.Add(task.Id);
                return DispatchStartOutcome.Started();
            },
            startRecordedDispatches: null, buildServerShutdown: null,
            runAcceptanceVerification: _ =>
            {
                GateRuns++;
                return _gateResult ?? throw new InvalidOperationException("Missing fixture gate result");
            },
            runAdvisorySemanticAcceptance: null, retryTask: null, recordTaskNote: null,
            recordCriterionRetryFeedback: _kernel.RecordCriterionRetryFeedback, clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "Already fast-forwardable", [], null),
            land: (_, _) => throw new InvalidOperationException("Unexpected landing"), afterSuccessfulLanding: null, record: _ => { },
            cleanup: _ => throw new InvalidOperationException("Unexpected cleanup"),
            writeEscalation: (_, _, reason) => throw new InvalidOperationException(reason), classifyChangeRisk: _ => null,
            parallelAcceptanceAttemptCoordinator: Coordinator("gate"), focusedEvidenceAttemptCoordinator: Coordinator("focused"),
            getPreReviewEvidenceContext: _ => NoPreReviewContext("corrected-candidate"),
            recordPreReviewEvidence: (goal, task, receipt) => _kernel.RecordPreReviewEvidence(goal, task, receipt),
            runFocusedEvidence: (_, request) =>
            {
                Requests.Add(request);
                var red = RedIdentity is { } identity && request.Contains(
                    $"FullyQualifiedName~{AcceptanceTestSourceResolver.ExtractClassName(identity)}", StringComparison.Ordinal)
                    ? new[] { identity } : [];
                return new(request, Accepted: true, Passed: red.Length == 0, Summary: "fixture focused result",
                    Checks: [new(request, red.Length == 0, red.Length, "fixture", TestResultPaths: [Path.Combine(_root, "focused.trx")],
                        FailingTestIdentities: red, ExecutedTestCount: 1)]);
            },
            retryTaskWithCause: (goal, task, message, round, cause) =>
            {
                RetriedTask = task;
                RetryMessage = message;
                return _kernel.RetryTask(goal, task, message, retryRoundKind: round, retryCause: cause);
            });

        private ConductorParallelAcceptanceAttemptCoordinator Coordinator(string directory) => new(
            Path.Combine(_root, directory), TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
