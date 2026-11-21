using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;
using static AttributedFailureDiagnosisCoordinatorTests;

// Parallel-safe: independent in-memory goals, artifact directories, and a fake process runner.
public sealed class AttributedFailureDiagnosisTargetTests
{
    [Fact]
    public async Task OperatorClosedTaskUsesNewestCompletedDeveloperDispatch()
    {
        using var fixture = new SoloCorrectionTestFixture();
        var kernel = fixture.Kernel;
        var selected = new TaskSpec(TaskId.New(), "Earlier Developer", AgentRole.Developer);
        var competitor = new TaskSpec(TaskId.New(), "Other Developer", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Tester", AgentRole.Tester);
        var routed = new TaskSpec(TaskId.New(), "Operator-closed Developer", AgentRole.Developer);
        var goal = kernel.CreateGoal("Dispatch history", [selected, competitor, tester, routed]);
        kernel.ActivateGoal(goal.Id, ConductorDriverTests.DefaultAgents());

        var parent = CompleteAttempt(selected, fixture.Root, started: 1, completed: 5, exitCode: 1);
        CompleteAttempt(competitor, fixture.Root, started: 2, completed: 4);
        CompleteAttempt(tester, fixture.Root, started: 3, completed: 7);
        var unfinished = Dispatch(selected, fixture.Root, started: 6);
        routed.RecordVerification(new("operator", fixture.Root, 0, "", "", DateTimeOffset.UnixEpoch.AddSeconds(8),
            HasCommittedChanges: true, EvidenceSource: VerificationEvidenceSource.Manual));
        kernel.ReportTaskProgress(goal.Id, routed.Id, WorkTaskStatus.Completed, "Operator closed the Developer without a dispatch.");
        Assert.Null(routed.LastDispatch);
        var route = Route(goal);
        Assert.Equal(routed.Id, route.DeveloperTaskId);

        WorkerProcessRunRequest? request = null;
        var inquiry = Inquiry(fixture.Root, (run, _) =>
        {
            request = run;
            return Task.FromResult(new WorkerProcessRunResult(0, "DIAGNOSIS_STATUS: cause-found\nThe failure is in src/File.cs.", ""));
        });
        var result = await inquiry.RunAsync(goal, route, AttributedFailureDiagnosisCoordinator.DefaultTimeout, default);

        Assert.True(result.CauseFound);
        Assert.NotNull(request);
        Assert.Equal(parent.WorkingDirectory, request.WorkingDirectory);
        var path = Assert.Single(Directory.GetFiles(fixture.Root, "*.receipt.json"));
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(selected.Id.Value, receipt.RootElement.GetProperty("TaskId").GetProperty("Value").GetString());
        Assert.Equal(InquiryDispatcher.BuildParentDispatchId(goal.Id, selected.Id, parent),
            receipt.RootElement.GetProperty("ParentDispatchId").GetString());
        Assert.Same(unfinished, selected.LastDispatch);
        Assert.Null(routed.LastDispatch);
    }

    [Fact]
    public async Task NoCompletedDispatchRecordsTypedReasonAndReplays()
    {
        using var fixture = new SoloCorrectionTestFixture();
        var developer = fixture.Goal.Tasks.Single();
        // A manual verification and a dispatch history row alone do not prove a worker finished.
        developer.ClearLastDispatch();
        var route = Route(fixture.Goal);
        var launches = 0;
        var inquiry = Inquiry(fixture.Root, (_, _) =>
        {
            launches++;
            throw new InvalidOperationException("Unexpected process launch");
        });
        var enriched = await Coordinator(fixture.Kernel, inquiry.RunAsync).EnrichAsync(fixture.Goal, route);

        Assert.Equal(AttributedFailureDiagnosisUnavailableReason.NoCompletedDeveloperDispatch, enriched.DiagnosisUnavailableReason);
        Assert.Equal("no-completed-developer-dispatch", enriched.Diagnosis!.Status);
        var note = Assert.Single(fixture.Goal.Timeline.Where(evt =>
            evt.Message.StartsWith(AttributedFailureDiagnosisRecord.Marker, StringComparison.Ordinal)));
        Assert.Equal(developer.Id, note.TaskId);
        Assert.Contains("status=no-completed-developer-dispatch", note.Message);
        Assert.Contains("DIAGNOSIS unavailable: No completed Developer dispatch is recorded for this goal.", enriched.Feedback);
        Assert.DoesNotContain("Exception", note.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inquiry failed", enriched.Feedback);

        fixture.Restart();
        var replayed = await Coordinator(fixture.Kernel, (_, _, _, _) => throw new InvalidOperationException("Inquiry must be replayed"))
            .EnrichAsync(fixture.Goal, Route(fixture.Goal));
        Assert.Equal(enriched.Diagnosis, replayed.Diagnosis);
        Assert.Equal(enriched.Feedback, replayed.Feedback);
        Assert.Single(fixture.Goal.Timeline.Where(evt =>
            evt.Message.StartsWith(AttributedFailureDiagnosisRecord.Marker, StringComparison.Ordinal)));
        Assert.Equal(0, launches);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.receipt.json"));
    }

    [Fact]
    public void CompletionTiesUseLaterHistoryAndRoutedDispatchTakesPrecedence()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = new TaskSpec(TaskId.New(), "First", AgentRole.Developer);
        var later = new TaskSpec(TaskId.New(), "Later", AgentRole.Developer);
        var routed = new TaskSpec(TaskId.New(), "Routed", AgentRole.Developer);
        var goal = kernel.CreateGoal("Completion tie", [first, later, routed]);
        var firstDispatch = CompleteAttempt(first, Path.GetTempPath(), started: 1, completed: 5);
        CompleteAttempt(later, Path.GetTempPath(), started: 2, completed: 5);
        var latest = CompleteAttempt(later, Path.GetTempPath(), started: 3, completed: 5);
        routed.SetStatus(WorkTaskStatus.Completed);

        var fallback = Assert.IsType<TaskSpec>(AttributedFailureDiagnosisTarget.Select(goal, routed));
        Assert.Equal(later.Id, fallback.Id);
        Assert.Same(latest, fallback.LastDispatch);

        first.SetStatus(WorkTaskStatus.Completed);
        Assert.Same(first, AttributedFailureDiagnosisTarget.Select(goal, first));
        Assert.Same(firstDispatch, first.LastDispatch);
    }

    internal static AttributedFailureDiagnosisInquiry Inquiry(string root,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runner)
    {
        var reviewer = new AgentDefinition(AgentId.New(), "Review", AgentRole.Reviewer,
            new("provider", "reviewer-model", ModelCapability.Text, SubscriptionMode.ApiKey), Subscription: new("codex-cli"));
        var dispatcher = new InquiryDispatcher(runner, headResolver: _ => Candidate, capturedHeadIsAncestor: (_, _, _) => true);
        return new(dispatcher, [reviewer], root, root, root);
    }

    private static TaskDispatchRecord CompleteAttempt(TaskSpec task, string root, int started, int completed, int exitCode = 0)
    {
        var dispatch = Dispatch(task, root, started);
        task.RecordVerification(new(dispatch.Command, dispatch.WorkingDirectory, exitCode, "worker output", "",
            DateTimeOffset.UnixEpoch.AddSeconds(completed), DispatchAttemptAt: dispatch.DispatchedAt,
            EvidenceSource: VerificationEvidenceSource.Dispatch));
        return dispatch;
    }

    private static TaskDispatchRecord Dispatch(TaskSpec task, string root, int started)
    {
        var directory = Path.Combine(root, $"attempt-{started}");
        var dispatch = new TaskDispatchRecord("codex-cli", "fixture", directory, DateTimeOffset.UnixEpoch.AddSeconds(started),
            BaseCommit: Main, ResultCommit: Candidate);
        task.RecordDispatch(dispatch);
        return dispatch;
    }
}
