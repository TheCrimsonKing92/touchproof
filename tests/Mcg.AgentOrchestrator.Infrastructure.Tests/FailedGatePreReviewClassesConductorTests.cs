using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: in-memory kernel, inline coordinators, unique owned files, and no real dispatches.
public sealed class FailedGatePreReviewClassesConductorTests
{
    [Fact]
    public void DeferredGateFailures_RunDistinctClassesAndDispatchReviewerWhenGreen()
    {
        using var harness = new Harness();
        harness.SeedFailure("Example.AlphaTests.First", "Example.BetaTests.First",
            "Example.BetaTests.Second", "Example.BetaTests.Second(value: 42)");
        var driver = harness.Driver();

        driver.AdvanceOnce(harness.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(["Infrastructure.Tests: FullyQualifiedName~AlphaTests|FullyQualifiedName~BetaTests"], harness.Requests);
        var receipt = Assert.IsType<PreReviewEvidenceReceipt>(harness.Reviewer.PreReviewEvidenceReceipt);
        Assert.Equal(PreReviewEvidenceDisposition.Green, receipt.Disposition);
        Assert.Equal([harness.Requests[0]], receipt.SelectedFocusedTests);
        Assert.Equal([harness.Reviewer.Id], harness.DispatchedTasks);
        Assert.Empty(harness.RetriedTasks);
        Assert.Equal(0, harness.GateRuns);
    }

    [Fact]
    public void FailedClassStillRed_RetriesDeveloperWithoutStartingGate()
    {
        using var harness = new Harness();
        const string identity = "Example.FailedTests.Case(value: 42)";
        harness.SeedFailure(identity);
        harness.RedIdentity = identity;
        var driver = harness.Driver();

        driver.AdvanceOnce(harness.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(["Infrastructure.Tests: FullyQualifiedName~FailedTests"], harness.Requests);
        Assert.Equal([harness.Developer.Id], harness.RetriedTasks);
        Assert.Equal(WorkTaskStatus.Assigned, harness.Developer.Status);
        Assert.Contains(identity, harness.RetryMessage, StringComparison.Ordinal);
        Assert.Equal(PreReviewEvidenceDisposition.Red, harness.Reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Equal([identity], harness.Reviewer.PreReviewEvidenceReceipt?.FailingTestIdentities);
        Assert.Equal([harness.Developer.Id], harness.DispatchedTasks);
        Assert.Equal(0, harness.GateRuns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureWithoutNamedTests_PreservesNoApplicablePath(bool hasFailure)
    {
        using var harness = new Harness();
        if (hasFailure) harness.SeedFailure();
        var driver = harness.Driver();

        driver.AdvanceOnce(harness.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Empty(harness.Requests);
        Assert.Equal(PreReviewEvidenceDisposition.NoApplicableTests, harness.Reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Empty(harness.Reviewer.PreReviewEvidenceReceipt!.SelectedFocusedTests);
        Assert.Equal([harness.Reviewer.Id], harness.DispatchedTasks);
        Assert.Empty(harness.RetriedTasks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiptMissingFailedClasses_RunsOnceThenReusesCompleteGreen(bool priorGreen)
    {
        using var harness = new Harness();
        harness.SeedFailure("Example.AlphaTests.First", "Example.BetaTests.First");
        if (priorGreen) harness.Context = FocusedPreReviewContext(Harness.Candidate);
        PreReviewEvidenceReceipts.Record((goal, task, receipt) => harness.Kernel.RecordPreReviewEvidence(goal, task, receipt), harness.Goal, harness.Reviewer,
            harness.Context, 1, priorGreen ? PreReviewEvidenceDisposition.Green : PreReviewEvidenceDisposition.NoApplicableTests,
            priorGreen ? PassingPreReviewEvidence(harness.Context.FocusedRequest!).Checks : [], [], null);
        var prior = harness.Reviewer.PreReviewEvidenceReceipt!;
        Assert.True(prior.MatchesCurrentCandidate(harness.Goal.Id.Value, Harness.Candidate, harness.Context.SelectedFocusedTests));
        Assert.DoesNotContain(prior.SelectedFocusedTests, item => item.Contains("FullyQualifiedName~AlphaTests", StringComparison.Ordinal));
        var driver = harness.Driver();

        driver.AdvanceOnce(harness.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(harness.Requests);
        Assert.Contains("FullyQualifiedName~AlphaTests", harness.Requests[0], StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName~BetaTests", harness.Requests[0], StringComparison.Ordinal);
        Assert.Equal(PreReviewEvidenceDisposition.Green, harness.Reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.False(prior.MatchesCurrentCandidate(harness.Goal.Id.Value, Harness.Candidate,
            harness.Reviewer.PreReviewEvidenceReceipt!.SelectedFocusedTests));
        Assert.Empty(harness.Reviewer.PreReviewEvidenceReceipt.Advisories ?? []);

        driver.AdvanceOnce(harness.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Single(harness.Requests);
        Assert.Contains(PreReviewEvidenceReceipt.SyntheticReuseAdvisory, harness.Reviewer.PreReviewEvidenceReceipt!.Advisories!);
        Assert.Equal(2, harness.DispatchedTasks.Count);
        Assert.Empty(harness.RetriedTasks);
        Assert.Equal(0, harness.GateRuns);
    }

    private sealed class Harness : IDisposable
    {
        internal const string Candidate = "corrected-candidate";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-failed-gate", Guid.NewGuid().ToString("N"));
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal TaskSpec Developer { get; }
        internal TaskSpec Reviewer { get; }
        internal PreReviewEvidenceContext Context { get; set; } = NoPreReviewContext(Candidate);
        internal List<string> Requests { get; } = [];
        internal List<TaskId> DispatchedTasks { get; } = [];
        internal List<TaskId> RetriedTasks { get; } = [];
        internal string? RetryMessage { get; private set; }
        internal string? RedIdentity { get; set; }
        internal int GateRuns { get; private set; }

        internal Harness()
        {
            (Kernel, Goal) = ReviewGoalWithoutTester();
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            PassVerification(Kernel, Goal, Developer, hasCommittedChanges: true);
            Directory.CreateDirectory(_root);
        }

        internal void SeedFailure(params string[] identities)
        {
            Kernel.RecordAcceptanceFailure(Goal.Id, ["gate tests"], "failed-candidate", "main", attemptId: "failed-attempt");
            var directory = Path.Combine(_root, "gate", Goal.Id.Value);
            Directory.CreateDirectory(directory);
            var attempt = new ConductorParallelAcceptanceAttempt("failed-attempt", Goal.Id.Value, Goal.Id.Value[..8],
                0, "failed-candidate", "main", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1,
                ConductorParallelAcceptanceAttemptOutcome.Failed, "stdout", "stderr", "exit", "heartbeat",
                Path.Combine(directory, "result.json"), Path.Combine(directory, "failed.attempt.json"),
                CompletedAt: DateTimeOffset.UnixEpoch, ReconciledAt: DateTimeOffset.UnixEpoch);
            var summary = new AcceptanceVerificationSummary(false,
                [new("gate tests", false, 1, "failed", TestProjectPath: "Infrastructure.Tests", FailingTestIdentities: identities)],
                BranchHeadSha: attempt.BranchHeadSha, MainHeadSha: attempt.MainHeadSha, AttemptId: attempt.AttemptId);
            var artifact = new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null, summary,
                attempt.BranchHeadSha, attempt.MainHeadSha, null, null);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            File.WriteAllBytes(attempt.MetadataPath, JsonSerializer.SerializeToUtf8Bytes(attempt, options));
            File.WriteAllBytes(attempt.ResultPath, JsonSerializer.SerializeToUtf8Bytes(artifact, options));
            Goal.DeferAcceptanceFailureForRetry();
            Assert.Null(Goal.LatestAcceptanceFailure);
            Assert.NotNull(Goal.RetainedAcceptanceFailure);
        }

        internal ConductorDriver Driver()
        {
            var gate = new ConductorParallelAcceptanceAttemptCoordinator(Path.Combine(_root, "gate"), TrunkBranchName.Default,
                runInline: true, acquireStableSlotLease: (_, _) => null);
            if (Goal.RetainedAcceptanceFailure is not null)
                Assert.Equal("failed-attempt", gate.TryReadLatest(Goal.Id.Value)?.AttemptId);
            return new ConductorDriver(
                getFacts: _ => new(WorkspaceExists: true), getRunningPaidWorkerCount: () => 0,
                createWorkspace: _ => throw new InvalidOperationException("Unexpected workspace creation"),
                dispatchAndStart: goal =>
                {
                    var next = Assert.Single(goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned &&
                        DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Length == 0));
                    DispatchedTasks.Add(next.Id);
                    return DispatchStartOutcome.Started();
                },
                startRecordedDispatches: null, buildServerShutdown: null,
                runAcceptanceVerification: _ => { GateRuns++; return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
                runAdvisorySemanticAcceptance: null, retryTask: null, recordTaskNote: null,
                recordCriterionRetryFeedback: null, clearCriterionRetryFeedback: null,
                rebaseOntoMain: _ => throw new InvalidOperationException("Unexpected git"),
                land: (_, _) => throw new InvalidOperationException("Unexpected landing"),
                afterSuccessfulLanding: null, record: _ => { },
                cleanup: _ => throw new InvalidOperationException("Unexpected cleanup"),
                writeEscalation: (_, _, reason) => throw new InvalidOperationException(reason), classifyChangeRisk: _ => null,
                parallelAcceptanceAttemptCoordinator: gate,
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(_root, "focused"), TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                getPreReviewEvidenceContext: _ => Context,
                recordPreReviewEvidence: (goal, task, receipt) => Kernel.RecordPreReviewEvidence(goal, task, receipt),
                runFocusedEvidence: (_, request) =>
                {
                    Requests.Add(request);
                    return new(request, Accepted: true, Passed: RedIdentity is null, Summary: "fixture focused result",
                        Checks: request.Split("; ").Select(item => new AcceptanceCheckResult(item, RedIdentity is null,
                            RedIdentity is null ? 0 : 1, "fixture", TestResultPaths: [Path.Combine(_root, "focused.trx")],
                            FailingTestIdentities: RedIdentity is null ? [] : [RedIdentity], ExecutedTestCount: 1)).ToArray());
                },
                retryTaskWithRoundKind: (goal, task, message, round) =>
                {
                    RetriedTasks.Add(task);
                    RetryMessage = message;
                    return Kernel.RetryTask(goal, task, message, retryRoundKind: round);
                });
        }

        public void Dispose()
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcg-failed-gate")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its owned root");
            Directory.Delete(_root, recursive: true);
        }
    }
}
