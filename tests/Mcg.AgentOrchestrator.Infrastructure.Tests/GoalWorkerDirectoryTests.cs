using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its repository, state database and prompt directory;
// console capture is async-local, and the dispatch runner cannot launch a worker.
public sealed class GoalWorkerDirectoryTests : GoalWorktreeRecoveryTestBase
{
    private static readonly WorkerSandboxOptions DisabledSandbox = new(
        false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Resolve_RemovedWorktree_ThrowsTypedRefusal(bool stale)
    {
        using var f = new MissingFixture(stale);
        AssertMissing(f);
        var refusal = Assert.Throws<MissingGoalWorktreeException>(() =>
            GoalWorkerDirectory.Resolve(OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo), f.Goal.Id));
        AssertRefusal(f, refusal);
        AssertNoDispatch(f.Goal);
    }

    [Xunit.Fact]
    public void Resolve_ExistingWorktree_ReturnsGoalPath()
    {
        using var f = new MissingFixture();
        AssertMissing(f);
        Assert.True(f.Recover().Succeeded);
        f.AssertRestored();
        Assert.Equal(f.Path, GoalWorkerDirectory.Resolve(
            OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo), f.Goal.Id));
        AssertNoDispatch(f.Goal);
    }

    [Xunit.Fact]
    public void Resolve_NonGitDirectory_ReturnsExecutionDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.False(GoalWorktrees.IsGitWorkTree(root), "Non-git control must be outside a Git work tree.");
            var workspace = OrchestratorWorkspace.ForDirectory(root, root);
            Assert.Equal(root, GoalWorkerDirectory.Resolve(workspace, GoalId.New()));
        }
        finally { DeleteDirectory(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("dispatch", false)]
    [Xunit.InlineData("dispatch", true)]
    [Xunit.InlineData("worker-dispatch", false)]
    [Xunit.InlineData("worker-dispatch", true)]
    public void Cli_RemovedWorktree_ExitsWithPlainErrorBeforeRecording(string verb, bool stale)
    {
        using var f = new MissingFixture(stale);
        AssertMissing(f);
        var workspace = OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo);
        var refusal = Assert.Throws<MissingGoalWorktreeException>(() =>
            GoalWorkerDirectory.Resolve(workspace, f.Goal.Id));
        AssertRefusal(f, refusal);
        var context = Context(f.Kernel, workspace, f.Goal);
        var error = CaptureConsoleError(() =>
        {
            var exit = Assert.Throws<CliExitException>(() =>
                CliCommandHandlers.Execute(Command(verb), context));
            Assert.Equal(1, exit.ExitCode);
        });
        Assert.Equal($"Error: {refusal.Message}{Environment.NewLine}", error);
        AssertNoDispatch(f.Goal);
        AssertMissing(f);
        Assert.False(Directory.Exists(workspace.PromptDirectory));
    }

    [Xunit.Theory]
    [Xunit.InlineData("dispatch")]
    [Xunit.InlineData("worker-dispatch")]
    public void Cli_ExistingWorktree_RecordsGoalDirectoryWithoutLaunching(string verb)
    {
        using var f = new MissingFixture();
        AssertMissing(f);
        Assert.True(f.Recover().Succeeded);
        f.AssertRestored();
        var context = Context(f.Kernel, OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo), f.Goal);
        CaptureConsole(() => Assert.True(CliCommandHandlers.Execute(Command(verb), context)));
        AssertSafeDispatch(f.Goal, f.Path, f.Repo);
    }

    [Xunit.Theory]
    [Xunit.InlineData("dispatch")]
    [Xunit.InlineData("worker-dispatch")]
    public void Cli_NonGitDirectory_RecordsExecutionDirectoryWithoutLaunching(string verb)
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.False(GoalWorktrees.IsGitWorkTree(root), "Non-git control must be outside a Git work tree.");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Non-git dispatch control", [new(TaskId.New(), "Prepare work", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["Retain non-git working directory"], VerificationClass.TestVerifiable, [], []));
            var context = Context(kernel, OrchestratorWorkspace.ForDirectory(root, root), goal);
            CaptureConsole(() => Assert.True(CliCommandHandlers.Execute(Command(verb), context)));
            var task = Assert.Single(goal.Tasks);
            Assert.Equal(root, Assert.Single(task.DispatchHistory).WorkingDirectory);
            Assert.Null(task.LastProcess);
        }
        finally { DeleteDirectory(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Conductor_RemovedWorktree_RecoversBeforeRealStartPreparation(bool stale)
    {
        using var f = new MissingFixture(stale);
        AssertMissing(f);
        var workspace = PrepareStartWorkspace(f);
        var prepared = false;
        DispatchStartOutcome Prepare(Goal goal)
        {
            Assert.Equal(f.Path, GoalWorktrees.TryResolve(f.Repo, goal.Id));
            Assert.True(File.Exists(Path.Combine(f.Path, "unique-goal.txt")));
            var stop = Assert.Throws<InvalidOperationException>(() => Start(f, workspace, goal));
            Assert.Contains("process start is disabled", stop.Message, StringComparison.OrdinalIgnoreCase);
            AssertSafeDispatch(goal, f.Path, f.Repo);
            prepared = true;
            return DispatchStartOutcome.Started();
        }
        var coordinator = f.Coordinator();
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => throw new InvalidOperationException("Unexpected ordinary creation"),
            dispatchAndStart: Prepare, startRecordedDispatches: Prepare, buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null, retryTask: null, recordTaskNote: null,
            recordCriterionRetryFeedback: null, clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => throw new InvalidOperationException("Unexpected rebase"),
            land: (_, _) => throw new InvalidOperationException("Unexpected landing"), afterSuccessfulLanding: null,
            record: _ => { }, cleanup: _ => throw new InvalidOperationException("Unexpected cleanup"),
            writeEscalation: (_, _, _) => { }, classifyChangeRisk: _ => null,
            integrateMainBeforeDeveloperDispatch: goal =>
            {
                f.AssertRestored();
                return ConductorDriver.IntegrateMainBeforeDeveloperDispatch(f.Repo, goal, f.MainBranch);
            },
            prepareMissingWorkspace: goal => ConductorMissingWorktreeRecovery.Prepare(
                f.Repo, goal, coordinator, () => f.Kernel.Goals.ToArray(), _ => false));

        driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(prepared, "Conductor did not reach the real start-preparation callback.");
        AssertSafeDispatch(f.Goal, f.Path, f.Repo);
        Assert.Single(GoalOperationJournal.Read(f.Repo, f.Goal.Id).Entries.Where(entry =>
            entry.Operation == ConductorMissingWorktreeRecovery.Operation && entry.Status == GoalOperationStatus.Completed));
    }

    [Xunit.Fact]
    public void Start_WorktreeDisappearsAfterRecovery_RefusesBeforeRecording()
    {
        using var f = new MissingFixture();
        AssertMissing(f);
        var workspace = PrepareStartWorkspace(f);
        using var ownership = ConductorMissingWorktreeRecovery.Prepare(
            f.Repo, f.Goal, f.Coordinator(), () => f.Kernel.Goals.ToArray(), _ => false);
        f.AssertRestored();
        DeleteDirectory(f.Path);
        AssertMissing(f);

        var refusal = Assert.Throws<MissingGoalWorktreeException>(() => Start(f, workspace, f.Goal));

        AssertRefusal(f, refusal);
        AssertNoDispatch(f.Goal);
    }

    private static CliExecutionContext Context(
        AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, Goal goal) =>
        new(kernel, workspace, new InMemoryModelProviderRegistry([]),
            AgentCatalog.Default().Agents, new WorkerProfileCatalog([]), goal);

    // These CLI verbs prepare records only; neither has a worker launch operation.
    private static string[] Command(string verb) => [verb, "1", "fixture", "Write-Output fixture"];

    private static OrchestratorWorkspace PrepareStartWorkspace(MissingFixture f)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo) with { IntegrationBranch = f.MainBranch };
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        return workspace;
    }

    private static void Start(MissingFixture f, OrchestratorWorkspace workspace, Goal goal) =>
        new GoalDispatchOperations(resolveHome: _ => OrchestratorHome.ForInstallRoot(f.Repo)).StartSubscriptionReadyTasks(
            f.Kernel, workspace, goal, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
            runner: new BackgroundDispatchRunner(disableProcessStart: true,
                startProcess: _ => throw new InvalidOperationException("Unexpected worker launch")),
            sandboxOptions: DisabledSandbox, commandExists: _ => true,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);

    private static void AssertMissing(MissingFixture f)
    {
        Assert.True(GoalWorktrees.IsGitWorkTree(f.Repo));
        Assert.False(Directory.Exists(f.Path));
        Assert.Null(GoalWorktrees.TryResolve(f.Repo, f.Goal.Id));
        Assert.Equal(f.Path, GoalWorktrees.WorktreePath(f.Repo, f.Goal.Id));
    }

    private static void AssertRefusal(MissingFixture f, MissingGoalWorktreeException refusal)
    {
        Assert.Equal(f.Goal.Id, refusal.GoalId);
        Assert.Equal(f.Path, refusal.ExpectedWorktreePath);
        Assert.Contains(f.Goal.Id.Value, refusal.Message);
        Assert.Contains(f.Path, refusal.Message);
    }

    private static void AssertNoDispatch(Goal goal)
    {
        var task = Assert.Single(goal.Tasks);
        Assert.Empty(task.DispatchHistory);
        Assert.Null(task.LastDispatch);
        Assert.Null(task.LastProcess);
    }

    private static void AssertSafeDispatch(Goal goal, string expected, string repo)
    {
        var task = Assert.Single(goal.Tasks);
        Assert.Equal(expected, Assert.Single(task.DispatchHistory).WorkingDirectory);
        Assert.Equal(expected, task.LastDispatch!.WorkingDirectory);
        Assert.All(task.DispatchHistory, dispatch => Assert.NotEqual(repo, dispatch.WorkingDirectory));
        Assert.Null(task.LastProcess);
    }
}
