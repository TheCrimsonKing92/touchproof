using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorMissingWorktreeRecoveryTests : GoalWorktreeRecoveryTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void CreatedRouteUsesRecoveryInsteadOfOrdinaryCreation(bool stale)
    {
        using var f = new MissingFixture(stale, uniqueCommit: false);
        var workspace = OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo);
        var driver = new ConductorDriver(f.Kernel, workspace, new ConductorDriverTests.FakeAcceptanceVerifier(),
            AgentCatalog.Default().Agents, new WorkerProfileCatalog([]));
        var result = driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        f.AssertRestored();
        Assert.False(File.Exists(Path.Combine(f.Path, "new-main.txt")));
        Assert.Single(GoalOperationJournal.Read(f.Repo, f.Goal.Id).Entries.Where(e =>
            e.Operation == ConductorMissingWorktreeRecovery.Operation && e.Status == GoalOperationStatus.Completed));
        Assert.Null(f.Goal.Tasks.Single().LastProcess);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    public void RecoveryContinuesOrdinaryPreparation(bool stale, bool recordedDispatch)
    {
        using var f = new MissingFixture(stale);
        var coordinator = f.Coordinator();
        var task = f.Goal.Tasks.Single();
        if (recordedDispatch)
            f.Kernel.RecordTaskDispatch(f.Goal.Id, task.Id, new("codex-cli", "fixture", f.Path, DateTimeOffset.UtcNow));
        var prepared = false;
        var integrationObservedRecovery = false;
        DispatchStartOutcome Prepare(Goal goal)
        {
            Assert.True(File.Exists(Path.Combine(f.Path, "unique-goal.txt")));
            var overlap = coordinator.TryBegin(goal, "competing-preparation");
            using var unexpected = overlap.Scope;
            Assert.False(overlap.Acquired);
            Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Live, overlap.LeaseFact?.RecoveryStatus);
            var recovery = Assert.Single(GoalOperationJournal.Read(f.Repo, goal.Id).Entries.Where(e =>
                e.Operation == ConductorMissingWorktreeRecovery.Operation && e.Status == GoalOperationStatus.Completed));
            Assert.Contains(f.Tip, recovery.Detail);
            Assert.True(File.Exists(Path.Combine(f.Path, "config", "acceptance-manifest.json")));
            Assert.True(GoalWorktrees.TryComputeCandidateIdentity(f.Path, f.MainBranch, out var candidate, out var failure), failure);
            Assert.NotNull(candidate);
            var dispatch = WorkerProfileDispatcher.PrepareTask(f.Kernel, goal, task,
                new WorkerProfile("codex-cli", "codex exec --cd {workingDirectory}"),
                Path.Combine(f.Path, "prompts"), f.Path, DateTimeOffset.UtcNow,
                providerName: "OpenAI", modelName: AgentCatalog.OpenAiSubscriptionModelAlias,
                allowPendingRecordedDispatchRefresh: recordedDispatch,
                sandboxOptions: new(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget),
                integrationBranch: f.MainBranch);
            Assert.True(File.Exists(dispatch.PromptPath));
            Assert.Contains(goal.Objective, File.ReadAllText(dispatch.PromptPath));
            var sandboxRoot = Path.Combine(f.Path, ".mcg-sandbox");
            var sandbox = new WorkerSandboxPreparer(new FixtureLabeler()).Prepare(f.Path, sandboxRoot);
            Assert.False(sandbox.RequiresRecovery);
            Assert.True(File.Exists(Path.Combine(f.Path, WorkerSandboxPreparer.MarkerFileName)));
            Assert.True(File.Exists(Path.Combine(sandboxRoot, WorkerSandboxPreparer.MarkerFileName)));
            prepared = true;
            // Controlled launch boundary: readiness is observed without starting a worker.
            return DispatchStartOutcome.Started();
        }
        var driver = Driver(f, coordinator, Prepare, goal =>
        {
            f.AssertRestored();
            integrationObservedRecovery = true;
            return ConductorDriver.IntegrateMainBeforeDeveloperDispatch(f.Repo, goal, f.MainBranch);
        });
        driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.True(prepared);
        Assert.Equal(!recordedDispatch, integrationObservedRecovery);
        Assert.Null(coordinator.ReadFact(f.Goal));
        Assert.Null(task.LastProcess);
    }

    [Xunit.Fact]
    public void PreparationFailureCannotStartWorker()
    {
        using var f = new MissingFixture();
        var driver = Driver(f, f.Coordinator(), _ =>
        {
            // Exercise the normal sandbox preparation seam with a failing collaborator.
            var result = new WorkerSandboxPreparer(new FixtureLabeler(fail: true)).Prepare(f.Path, Path.Combine(f.Path, ".mcg-sandbox"));
            Assert.True(result.RequiresRecovery);
            return DispatchStartOutcome.Deferred("fixture prerequisite generation failed");
        }, goal => ConductorDriver.IntegrateMainBeforeDeveloperDispatch(f.Repo, goal, f.MainBranch));
        var result = driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.IsNotType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Null(f.Goal.Tasks.Single().LastProcess);
        Assert.True(File.Exists(Path.Combine(f.Path, "unique-goal.txt")));
    }

    private static ConductorDriver Driver(MissingFixture f, GoalEvidenceOperationCoordinator coordinator,
        Func<Goal, DispatchStartOutcome> prepare, Func<Goal, DeveloperBranchIntegrationResult> integrate) => new(
        getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
        getRunningPaidWorkerCount: () => 0, createWorkspace: _ => throw new InvalidOperationException("unexpected creation"),
        dispatchAndStart: prepare, startRecordedDispatches: prepare, buildServerShutdown: null,
        runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
        runAdvisorySemanticAcceptance: null, retryTask: null, recordTaskNote: null,
        recordCriterionRetryFeedback: null, clearCriterionRetryFeedback: null,
        rebaseOntoMain: _ => throw new InvalidOperationException("unexpected rebase"),
        land: (_, _) => throw new InvalidOperationException("unexpected landing"), afterSuccessfulLanding: null,
        record: _ => { }, cleanup: _ => throw new InvalidOperationException("unexpected cleanup"),
        writeEscalation: (_, _, _) => { }, classifyChangeRisk: _ => null,
        integrateMainBeforeDeveloperDispatch: integrate,
        prepareMissingWorkspace: goal => ConductorMissingWorktreeRecovery.Prepare(f.Repo, goal, coordinator,
            () => f.Kernel.Goals.ToArray(), _ => false));

    private sealed class FixtureLabeler(bool fail = false) : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(Directory.Exists(path), false, false, Medium: true);
        public bool SetIntegrity(string path, string level, bool recursive) => !fail;
    }
}
