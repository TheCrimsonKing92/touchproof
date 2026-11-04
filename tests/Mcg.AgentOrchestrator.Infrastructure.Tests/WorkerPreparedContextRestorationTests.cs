using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its repository, worktrees and dispatch state; no live worker is launched.
public sealed class WorkerPreparedContextRestorationTests : GoalWorktreeRecoveryTestBase
{
    private static readonly WorkerSandboxOptions Sandbox = new(false,
        WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    [Xunit.Theory]
    [Xunit.InlineData("OpenAI")]
    [Xunit.InlineData("Anthropic")]
    public void CompleteUntypedStartPreservesPreparedInputs(string provider)
    {
        using var f = new MissingFixture();
        Assert.True(f.Recover().Succeeded);
        var task = Prepare(f, provider);
        var dispatch = task.LastDispatch!;
        Assert.Null(dispatch.ContextPackageReceipt);
        // A retained prompt is an input, not an invitation to rebuild its brief.
        File.AppendAllText(dispatch.PromptPath!, "\nRetained fixture prompt marker.\n");
        var before = SnapshotInputs(f.Path, dispatch.PromptPath!);
        using var promptLease = new FileStream(dispatch.PromptPath!, FileMode.Open, FileAccess.Read, FileShare.Read);

        var stop = Assert.Throws<InvalidOperationException>(() => new GoalDispatchOperations().StartDispatches(
            f.Kernel, OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo), f.Goal,
            refreshBeforeStart: false, runner: new BackgroundDispatchRunner(disableProcessStart: true),
            sandboxOptions: Sandbox));

        Assert.Contains("process start is disabled", stop.Message, StringComparison.OrdinalIgnoreCase);
        AssertInputsEqual(before, SnapshotInputs(f.Path, dispatch.PromptPath!));
        Assert.Same(dispatch, task.LastDispatch);
        Assert.Single(task.DispatchHistory);
        Assert.Null(task.LastProcess);
    }

    [Xunit.Fact]
    public void PromptlessDirectStartReachesProcessBoundaryWithoutPreparation()
    {
        using var f = new MissingFixture();
        Assert.True(f.Recover().Succeeded);
        var task = f.Goal.Tasks.Single();
        // StartDispatches requires retry admission even for a promptless direct record.
        // Supply that prerequisite so the assertion exercises context restoration and start.
        var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            f.Goal.Id.Value, task.Id.Value, task.RequiredRole, "OpenAI",
            AgentCatalog.OpenAiSubscriptionModelAlias, PaidRouteClassification.Unknown,
            f.Tip, "criteria", [], [], [], [], f.Tip, f.MainBranch));
        f.Kernel.RecordTaskDispatch(f.Goal.Id, task.Id, new("fixture", "Write-Output safe", f.Path,
            DateTimeOffset.UtcNow, RetryContextFingerprint: fingerprint));
        var dispatch = task.LastDispatch!;
        Assert.Null(dispatch.PromptPath);
        Assert.Null(dispatch.ContextPackageReceipt);
        Assert.False(Directory.Exists(Path.Combine(f.Path, ".orchestrator-context")));

        var stop = Assert.Throws<InvalidOperationException>(() => new GoalDispatchOperations().StartDispatches(
            f.Kernel, OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo), f.Goal,
            refreshBeforeStart: false, runner: new BackgroundDispatchRunner(disableProcessStart: true),
            sandboxOptions: Sandbox));

        Assert.Contains("process start is disabled", stop.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(dispatch, task.LastDispatch);
        Assert.Equal(fingerprint, task.LastDispatch!.RetryContextFingerprint);
        Assert.Single(task.DispatchHistory);
        Assert.False(Directory.Exists(Path.Combine(f.Path, ".orchestrator-context")));
        Assert.Null(task.LastProcess);
    }

    [Xunit.Theory]
    [Xunit.InlineData("prompt")]
    [Xunit.InlineData("current-task.md")]
    [Xunit.InlineData("objective.md")]
    [Xunit.InlineData("digest.md")]
    [Xunit.InlineData("artifact-registry.json")]
    public void MissingUntypedInputIsRestoredWithoutReplacingAttempt(string input)
    {
        using var f = new MissingFixture();
        Assert.True(f.Recover().Succeeded);
        var task = Prepare(f);
        var dispatch = task.LastDispatch!;
        var path = input == "prompt" ? dispatch.PromptPath! :
            Path.Combine(f.Path, ".orchestrator-context", f.Goal.Id.Value, input);
        Assert.True(File.Exists(path));
        File.Delete(path);

        Restore(f, task);

        Assert.True(File.Exists(path));
        Assert.Contains(f.Goal.Objective, File.ReadAllText(dispatch.PromptPath!));
        Assert.Same(dispatch, task.LastDispatch);
        Assert.Single(task.DispatchHistory);
        Assert.Null(task.LastProcess);
        f.AssertRestored();
    }

    [Xunit.Fact]
    public void MissingHandoffIsRestoredForUntypedReservation()
    {
        using var f = new MissingFixture();
        Assert.True(f.Recover().Succeeded);
        var prior = f.Kernel.AddTask(f.Goal.Id, AgentRole.Planner, "Plan fixture change",
            AgentCatalog.Default().Agents, beforeRole: AgentRole.Developer);
        f.Kernel.ReportTaskProgress(f.Goal.Id, prior.Id, WorkTaskStatus.Completed, "Fixture plan complete.");
        f.Kernel.RecordTaskVerification(f.Goal.Id, prior.Id, new TaskVerificationRecord(
            "fixture plan", f.Path, 0, "Preserve the goal tip.", string.Empty, DateTimeOffset.UtcNow));
        var task = f.Goal.Tasks.Single(candidate => candidate.RequiredRole == AgentRole.Developer);
        Prepare(f);
        var dispatch = task.LastDispatch;
        var handoff = Path.Combine(f.Path, ".orchestrator-handoff.md");
        var expected = File.ReadAllText(handoff);
        File.Delete(handoff);

        Restore(f, task);

        Assert.Equal(expected, File.ReadAllText(handoff));
        Assert.Same(dispatch, task.LastDispatch);
        Assert.Single(task.DispatchHistory);
        Assert.Null(task.LastProcess);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void AbsentWorkspaceIsNeverCreatedByContextRestoration(bool promptless)
    {
        using var f = new MissingFixture();
        Assert.True(f.Recover().Succeeded);
        var task = promptless ? f.Goal.Tasks.Single() : Prepare(f);
        if (promptless)
            f.Kernel.RecordTaskDispatch(f.Goal.Id, task.Id, new("fixture", "Write-Output safe", f.Path, DateTimeOffset.UtcNow));
        var dispatch = task.LastDispatch;
        DeleteDirectory(f.Path);

        var refusal = Assert.Throws<InvalidOperationException>(() => Restore(f, task));

        Assert.Contains(promptless ? "Prepared prompt path is unavailable" : "requires an existing workspace", refusal.Message);
        Assert.False(Directory.Exists(f.Path));
        Assert.Same(dispatch, task.LastDispatch);
        Assert.Null(task.LastProcess);
        f.AssertTip();
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void AnotherGoalsWorktreeIsNeverPopulatedByContextRestoration(bool trailingSeparator)
    {
        using var f = new MissingFixture();
        var foreign = GoalWorktrees.Ensure(f.Repo, GoalId.New());
        var task = f.Goal.Tasks.Single();
        var prompt = Path.Combine(f.Repo, "missing-prompts", "fixture.md");
        f.Kernel.RecordTaskDispatch(f.Goal.Id, task.Id, new("fixture", "Write-Output safe",
            trailingSeparator ? foreign + Path.DirectorySeparatorChar : foreign,
            DateTimeOffset.UtcNow, PromptPath: prompt));
        var registrations = RunGitOutput(f.Repo, "worktree", "list", "--porcelain");

        var refusal = Assert.Throws<InvalidOperationException>(() => Restore(f, task));

        Assert.Contains("requires this goal's resolved worktree", refusal.Message);
        Assert.False(File.Exists(prompt));
        Assert.False(Directory.Exists(Path.Combine(foreign, ".orchestrator-context")));
        Assert.Equal(registrations, RunGitOutput(f.Repo, "worktree", "list", "--porcelain"));
        Assert.False(Directory.Exists(f.Path));
        f.AssertTip();
    }

    private static TaskSpec Prepare(MissingFixture f, string provider = "OpenAI")
    {
        var task = f.Goal.Tasks.Single(candidate => candidate.RequiredRole == AgentRole.Developer);
        WorkerProfileDispatcher.PrepareTask(f.Kernel, f.Goal, task,
            new WorkerProfile("codex-cli", "codex exec --cd {workingDirectory}"),
            OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo).PromptDirectory, f.Path, DateTimeOffset.UtcNow,
            providerName: provider, modelName: AgentCatalog.OpenAiSubscriptionModelAlias,
            sandboxOptions: Sandbox, integrationBranch: f.MainBranch);
        Assert.Null(task.LastDispatch!.ContextPackageReceipt);
        return task;
    }

    private static void Restore(MissingFixture f, TaskSpec task) =>
        WorkerProfileDispatcher.RestorePreparedContextIfMissing(f.Kernel, f.Goal, task, f.MainBranch, Sandbox);

    private static Dictionary<string, byte[]> SnapshotInputs(string worktree, string prompt) =>
        Directory.GetFiles(Path.Combine(worktree, ".orchestrator-context"), "*", SearchOption.AllDirectories)
            .Append(prompt).ToDictionary(path => path, File.ReadAllBytes);

    private static void AssertInputsEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, actual[path]);
    }
}
