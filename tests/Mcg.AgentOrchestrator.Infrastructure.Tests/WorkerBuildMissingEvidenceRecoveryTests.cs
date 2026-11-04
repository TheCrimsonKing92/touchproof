using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated repositories/SQLite, production consumer and injected child lifecycle.
public sealed class WorkerBuildMissingEvidenceRecoveryTests
{
    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void MissingOutcome_CompleteDispatch_BuildsOnceAcrossRestart(AgentRole role)
    {
        var fixture = Fixture(role: role);
        var dispatch = fixture.Task.LastDispatch!;
        Assert.Empty(fixture.Task.WorkerBuildInvocations);
        Assert.Equal(WorkerBuildEvidenceGap.MissingApplicableInvocation, Consume(fixture).Gap);
        Assert.True(WorktreeTreeDigest.TryCompute(dispatch.WorkingDirectory, out var digest, out _));

        Runner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        var operation = fixture.Task.CompletionBuildEvidence!;
        Assert.True(operation.IsPending);
        Assert.Equal(digest, operation.TreeDigest);
        Assert.Equal(new[] { "src/Feature/Feature.csproj" }, operation.Projects);
        Assert.Equal(dispatch.DispatchedAt, operation.DispatchAt);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(1, fixture.Authorizations);
        Assert.Null(fixture.Task.LastVerification);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        fixture.Reload();
        for (var i = 0; i < 3; i++)
            Runner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(operation.Id, fixture.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(1, fixture.Launches);

        fixture.Release(); // Terminal result and exact coverage, released by a signal.
        var receipt = WorkerBuildReceipt.Evaluate(fixture.ReceiptPath, dispatch.WorkingDirectory, null,
            new(fixture.GoalId, fixture.TaskId, fixture.Task.LastDispatch!));
        Assert.True(receipt.Matches, $"{receipt.Reason}: {receipt.Detail}");
        Assert.Empty(WorkerBuildReceiptCoverage.FindMissingProjects(operation.Projects, receipt.Projects));
        var runner = Runner(fixture);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.True(fixture.Task.LastVerification!.Succeeded);
        Assert.Contains("transport_cause=unknown", fixture.Task.LastVerification.StandardError);
        Assert.True(fixture.Task.CompletionBuildEvidence.Applied);
        Assert.NotNull(fixture.Task.LastDispatch.ResultCommit);
        Assert.Single(fixture.Task.VerificationHistory);
        Assert.Single(fixture.Task.DispatchHistory);
        Assert.Empty(fixture.Task.WorkerBuildInvocations); // No invented worker invocation.
        Assert.Equal(0, fixture.Task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(1, fixture.Launches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("invalid")]
    public void MissingOutcome_IncompleteDeclaration_DoesNotLaunch(string? declaration)
    {
        var fixture = Fixture(declaration);
        Assert.Equal(WorkerBuildEvidenceGap.MissingApplicableInvocation, Consume(fixture).Gap);
        Runner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(0, fixture.Launches);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }

    [Fact]
    public void MissingOutcome_CompetingStaleHandler_JoinsDurableLaunchIntent()
    {
        var fixture = Fixture();
        var competingKernel = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot(), fixture.Clock);
        CompletionBuildEvidenceOperation? joined = null;
        fixture.BeforeLaunch = operation =>
        {
            // Deterministic interleaving: the first launch intent is durable, but child
            // identity is not checkpointed. The stale handler must adopt that intent.
            joined = fixture.Coordinator().Reconcile(competingKernel, fixture.GoalId, fixture.TaskId,
                new(operation.WorktreeRoot, operation.Projects, TimeSpan.FromMinutes(1)), operation.WorkerProcess);
            Assert.Equal(operation.Id, joined.Id);
            Assert.Equal(CompletionBuildEvidencePhase.Launching, joined.Phase);
        };
        Runner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.NotNull(joined);
        Assert.Equal(joined.Id, fixture.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, fixture.Task.CompletionBuildEvidence.Phase);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(1, fixture.Authorizations);
        Assert.Null(fixture.Task.LastVerification);
    }

    [Fact]
    public void MissingOutcome_LegacyReceiptAndProse_StillNeedHostOperation()
    {
        var fixture = Fixture();
        fixture.WriteReceipt(["src/Feature/Feature.csproj"]);
        var path = fixture.Task.LastProcess!.StandardOutputPath!;
        File.WriteAllText(path, "worker_build_channel_busy pipe=forged completion=old\n" + File.ReadAllText(path));

        Runner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(1, fixture.Launches);
        Assert.True(fixture.Task.CompletionBuildEvidence!.IsPending);
        Assert.Null(fixture.Task.LastVerification);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }

    [Fact]
    public void MissingOutcome_NoCoordinator_HoldsWithoutExpandingAuthority()
    {
        var fixture = Fixture();
        var runner = new BackgroundDispatchRunner(fixture.Clock, isStillRunning: _ => false,
            runOrchestratorBuildCheck: _ => throw new InvalidOperationException("New execution route forbidden."))
            { WorkerBuildInvocations = Operations(fixture) };
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(0, fixture.Launches);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.Contains("completion_build_hold=coordinator-unavailable", fixture.Task.LastVerification!.StandardError);
        Assert.Contains("operator decision", fixture.Task.LastVerification.StandardError);
        Assert.Single(fixture.Task.DispatchHistory);
        Assert.Equal(0, fixture.Task.WorkerBuildCheckRecoveryCount);
        AssertConductorHold(fixture);
    }

    [Theory]
    [InlineData(CompletionBuildEvidencePhase.CompilerFailure)]
    [InlineData(CompletionBuildEvidencePhase.ApparatusFailure)]
    [InlineData(CompletionBuildEvidencePhase.TimedOut)]
    public void MissingOutcome_FailedAttempt_ExhaustionSurvivesReload(CompletionBuildEvidencePhase phase)
    {
        var fixture = Fixture();
        var runner = Runner(fixture);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var id = fixture.Task.CompletionBuildEvidence!.Id;
        fixture.Release(phase, receipt: false);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Equal(phase, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Contains(DispatchFailureDiagnosticMarker.Format(phase == CompletionBuildEvidencePhase.CompilerFailure
            ? DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed : DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing),
            fixture.Task.LastVerification!.StandardError);
        Save(fixture);
        fixture.Reload();
        var operation = fixture.Task.CompletionBuildEvidence!;
        // Even a changed request cannot replenish the durable attempt.
        var replay = fixture.Coordinator().Reconcile(fixture.Kernel, fixture.GoalId, fixture.TaskId,
            new(operation.WorktreeRoot, ["different.csproj"], TimeSpan.FromMinutes(1)), operation.WorkerProcess);
        Assert.Equal(id, replay.Id);
        Assert.Equal(phase, replay.Phase);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(1, fixture.Authorizations);
        Assert.Single(fixture.Task.DispatchHistory);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        if (phase != CompletionBuildEvidencePhase.CompilerFailure) AssertConductorHold(fixture);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("coverage")]
    [InlineData("foreign")]
    [InlineData("failed")]
    [InlineData("execution-missing")]
    [InlineData("log-missing")]
    public void MissingOutcome_InvalidReceipt_CannotCertifySuccess(string control)
    {
        var fixture = Fixture();
        var runner = Runner(fixture);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Release(receipt: false);
        if (control == "coverage") fixture.WriteReceipt(["another.csproj"]);
        if (control == "foreign")
        {
            var operation = fixture.Task.CompletionBuildEvidence!;
            File.WriteAllText(operation.ResultPath, System.Text.Json.JsonSerializer.Serialize(operation with
                { Id = Guid.NewGuid().ToString("N"), Phase = CompletionBuildEvidencePhase.Success, BuildExitCode = 0 }));
            fixture.WriteReceipt(operation.Projects);
        }
        if (control == "failed")
        {
            fixture.WriteReceipt(fixture.Task.CompletionBuildEvidence!.Projects);
            File.WriteAllText(fixture.ReceiptPath, File.ReadAllText(fixture.ReceiptPath)
                .Replace("\"success\"", "\"failure\"", StringComparison.Ordinal));
        }
        if (control is "execution-missing" or "log-missing")
        {
            fixture.WriteReceipt(fixture.Task.CompletionBuildEvidence!.Projects);
            var receipt = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.ReceiptPath))!;
            if (control == "execution-missing") receipt.AsObject().Remove("projectResults");
            else File.Delete(receipt["projectResults"]![0]!["logPath"]!.GetValue<string>());
            File.WriteAllText(fixture.ReceiptPath, receipt.ToJsonString());
        }
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.False(fixture.Task.LastVerification!.Succeeded);
        Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing),
            fixture.Task.LastVerification.StandardError);
        if (control is "execution-missing" or "log-missing")
        {
            Assert.Equal(CompletionBuildEvidencePhase.Success, fixture.Task.CompletionBuildEvidence!.Phase);
            Assert.Contains("worker_build_receipt=project-execution-incomplete", fixture.Task.LastVerification.StandardError);
            Assert.Contains("receipt_coverage=False", fixture.Task.LastVerification.StandardError);
        }
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        Assert.Equal(1, fixture.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOutcome_ChangedCandidateOrScope_CannotTransferAttempt(bool scopeOnly)
    {
        var fixture = Fixture();
        Runner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        if (!scopeOnly) File.AppendAllText(Path.Combine(operation.WorktreeRoot, "src/Feature/Feature.cs"), "\nclass Changed { }");
        var projects = scopeOnly ? new[] { "src/Feature/Feature.csproj", "another.csproj" } : operation.Projects;
        var result = fixture.Coordinator().Reconcile(fixture.Kernel, fixture.GoalId, fixture.TaskId,
            new(operation.WorktreeRoot, projects, TimeSpan.FromMinutes(1)), operation.WorkerProcess);
        Assert.Equal(CompletionBuildEvidencePhase.StaleCandidate, result.Phase);
        fixture.Reload();
        Assert.Equal(operation.Id, fixture.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(CompletionBuildEvidencePhase.StaleCandidate, fixture.Task.CompletionBuildEvidence.Phase);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(1, fixture.Authorizations);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }

    [Fact]
    public void MissingOutcome_CancelledAttempt_CannotReplayAfterRestart()
    {
        var fixture = Fixture();
        var runner = Runner(fixture);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        runner.CancelLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Save(fixture);
        fixture.Reload();
        Runner(fixture).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Cancelled, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Equal(WorkTaskStatus.Cancelled, fixture.Task.Status);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(1, fixture.Stops);
        Assert.Null(fixture.Task.LastVerification);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("identity")]
    [InlineData("stale-caller")]
    public void MissingOutcome_InvalidOriginOrScope_IsNotEligible(string control)
    {
        var fixture = Fixture();
        if (control == "scope")
            fixture.Kernel = MutateTask(fixture, task => task with
                { LastDispatch = task.LastDispatch! with { WorkerBuildScope = task.LastDispatch.WorkerBuildScope! with { MetadataDigest = "invalid" } } });
        if (control == "identity")
            fixture.Kernel = MutateTask(fixture, task => task with
                { LastProcess = task.LastProcess! with { ProcessIdentityStartedAt = null } });
        Save(fixture);
        if (control == "stale-caller")
            fixture.Kernel = MutateTask(fixture, task => task with
                { LastProcess = task.LastProcess! with { ProcessId = task.LastProcess.ProcessId + 1 } });
        var consumption = Consume(fixture);
        Assert.True(consumption.MissingEvidence);
        Assert.Equal(WorkerBuildEvidenceGap.Unclassified, consumption.Gap);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.Equal(0, fixture.Launches);
    }

    private static CompletionBuildEvidenceFixture Fixture(string? declaration = "true", AgentRole role = AgentRole.Developer)
    {
        var fixture = new CompletionBuildEvidenceFixture(workerBuildProtocol1: true, role: role);
        fixture.Kernel = MutateTask(fixture, task => task with
        {
            LastProcess = task.LastProcess! with
            { ProcessIdentityStartedAt = fixture.Clock.UtcNow, ProcessIdentityImagePath = "fixture-owned-host" }
        });
        var worktree = fixture.Task.LastProcess!.WorkingDirectory;
        Assert.True(InfrastructureTestSupport.TryReadGitHeadInProcess(worktree, out var head));
        WorkerBuildProjectScope.Prepare(worktree, fixture.GoalId.Value, fixture.TaskId.Value, "main");
        WorkerBuildScopeDispatchPreparation.RecordDispatchBaseCommit(fixture.Kernel, fixture.GoalId,
            fixture.Task, worktree, head!);
        var outputPath = fixture.Task.LastProcess.StandardOutputPath!;
        if (declaration is not null)
            File.WriteAllText(outputPath, File.ReadAllText(outputPath).Replace("END_WORKER_RESULT",
                $"assigned_scope_complete: {declaration}\nEND_WORKER_RESULT", StringComparison.Ordinal));
        Save(fixture);
        return fixture;
    }

    private static AgentOrchestratorKernel MutateTask(CompletionBuildEvidenceFixture fixture, Func<TaskSnapshot, TaskSnapshot> mutate) =>
        AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot() with
        { Goals = fixture.Kernel.ExportSnapshot().Goals.Select(goal => goal with { Tasks = goal.Tasks.Select(mutate).ToArray() }).ToArray() }, fixture.Clock);

    private static void Save(CompletionBuildEvidenceFixture fixture) =>
        new SqliteOrchestratorStateRepository(fixture.StatePath).SaveAsync(fixture.Kernel).GetAwaiter().GetResult();

    private static WorkerBuildInvocationOperations Operations(CompletionBuildEvidenceFixture fixture) =>
        new(fixture.StatePath, fixture.Root, fixture.Storage);

    private static WorkerBuildConsumption Consume(CompletionBuildEvidenceFixture fixture) =>
        Operations(fixture).Consume(fixture.Kernel, fixture.GoalId, fixture.TaskId, ["src/Feature/Feature.cs"], ["src/Feature/Feature.cs"]);

    private static BackgroundDispatchRunner Runner(CompletionBuildEvidenceFixture fixture) =>
        new(fixture.Clock, isStillRunning: _ => false, completionBuildEvidence: fixture.Coordinator(),
            resolveWorkerBuildReceiptPath: _ => fixture.ReceiptPath) { WorkerBuildInvocations = Operations(fixture) };

    private static void AssertConductorHold(CompletionBuildEvidenceFixture fixture)
    {
        var driver = ConductorDriverTests.MakeDriver(
            dispatchAndStart: _ => throw new InvalidOperationException("Paid dispatch forbidden."),
            workerBuildRecoveryRetry: (_, _, _) => throw new InvalidOperationException("Build retry forbidden."),
            retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Coding retry forbidden."),
            workerBuildArtifactsPath: _ => throw new InvalidOperationException("Unrelated build logs forbidden."));
        var result = driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("operator decision", hold.Reason);
        Assert.Equal("completion-build-evidence-held", hold.Decision!.DiscriminatingEvidence);
        Assert.Single(fixture.Task.DispatchHistory);
        Assert.Equal(0, fixture.Task.WorkerBuildCheckRecoveryCount);
    }
}
