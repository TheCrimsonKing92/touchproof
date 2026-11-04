using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Application;
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

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RecordedReservation_RestoresDeletedContextWithoutReplacingAttempt(bool stale)
    {
        using var f = new MissingFixture();
        Assert.True(f.Recover().Succeeded);
        var workspace = OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo);
        var task = f.Goal.Tasks.Single();
        var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        WorkerProfileDispatcher.PrepareTask(f.Kernel, f.Goal, task,
            new WorkerProfile("codex-cli", "codex exec --cd {workingDirectory}"),
            workspace.PromptDirectory, f.Path, DateTimeOffset.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias,
            paidRoute: PaidRouteClassification.Paid, sandboxOptions: sandbox, integrationBranch: f.MainBranch);
        var original = task.LastDispatch!;
        var contextReceipt = Assert.IsType<WorkerContextPackageReceipt>(original.ContextPackageReceipt);
        var fingerprint = Assert.IsType<RetryContextFingerprint>(original.RetryContextFingerprint);
        var at = original.DispatchedAt;
        f.Kernel.ApplyPreparedRetryAdmission(f.Goal.Id, task.Id, RetryAdmissionPolicy.Evaluate(
            task, fingerprint, original.PaidRoute, RetryCause.NewSourceFinding, at, at,
            reservationOwnerId: "fixture-reservation", reservationLeaseExpiresAt: DateTimeOffset.UnixEpoch));
        var reservation = Assert.Single(task.RetryAdmissionHistory);
        var operations = new GoalDispatchOperations();
        Assert.False(operations.ShouldRefreshPreparedDispatchBeforeStart(task, refreshBeforeStart: true));
        var mandatory = contextReceipt.Sections
            .Where(section => section.DeliveryMode == ContextDeliveryMode.MandatoryFile).ToArray();
        Assert.NotEmpty(mandatory);
        Assert.All(mandatory, section => Assert.True(File.Exists(Path.Combine(f.Path, section.MandatoryRelativePath!))));
        DeleteDirectory(f.Path);
        if (!stale) RunGit(f.Repo, "worktree", "remove", "--force", f.Path);
        Assert.All(mandatory, section => Assert.False(File.Exists(Path.Combine(f.Path, section.MandatoryRelativePath!))));
        var reachedStart = false;
        var driver = Driver(f, f.Coordinator(), goal =>
        {
            f.AssertRestored();
            Assert.False(operations.ShouldRefreshPreparedDispatchBeforeStart(task, refreshBeforeStart: true));
            // The real recorded-start path must restore artifacts itself. Stop at the
            // controlled process boundary; never launch a subscription CLI in this fixture.
            var stop = Assert.Throws<InvalidOperationException>(() => operations.StartDispatches(
                f.Kernel, workspace, goal, runner: new BackgroundDispatchRunner(disableProcessStart: true),
                sandboxOptions: sandbox));
            Assert.Contains("process start is disabled", stop.Message, StringComparison.OrdinalIgnoreCase);
            reachedStart = true;
            return DispatchStartOutcome.Started();
        }, _ => throw new InvalidOperationException("recorded dispatch must retain its integration identity"));

        driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(reachedStart);
        var restored = task.LastDispatch!;
        Assert.Single(task.DispatchHistory);
        Assert.Equal(at, restored.DispatchedAt);
        Assert.Equal(original.Command, restored.Command);
        Assert.Equal(original.PromptPath, restored.PromptPath);
        Assert.Equal(original.RetryContextFingerprint, restored.RetryContextFingerprint);
        Assert.Equal(original.AssignedAgentId, restored.AssignedAgentId);
        Assert.Equal(original.ConductorRoutingRevision, restored.ConductorRoutingRevision);
        Assert.Contains(task.RetryAdmissionHistory, receipt => receipt == reservation);
        Assert.Contains(task.RetryAdmissionHistory, receipt => receipt.Decision == RetryAdmissionDecision.ResumedReservation);
        Assert.All(task.RetryAdmissionHistory, receipt =>
        {
            Assert.Equal(at, receipt.LinkedDispatchAt);
            Assert.Equal(fingerprint, receipt.Fingerprint);
        });
        var restoredSections = restored.ContextPackageReceipt!.Sections
            .Where(section => section.MandatoryRelativePath is not null).ToArray();
        Assert.NotEmpty(restoredSections);
        Assert.All(restoredSections, section => Assert.Equal(section.ContentHash,
            WorkerContextArtifact.Hash(File.ReadAllBytes(Path.Combine(f.Path, section.MandatoryRelativePath!)))));
        var descriptors = restoredSections.Where(section => section.DeliveryMode == ContextDeliveryMode.MandatoryFile)
            .Select(section => new MandatoryContextFileDescriptor(section.LogicalIdentity, section.MandatoryRelativePath!,
                section.ContentHash, section.ContractVersion, task.RequiredRole, section.RoleVisibility)).ToArray();
        Assert.NotEmpty(descriptors);
        using var leases = DispatchProcessHost.AcquireMandatoryContextFileLeases(new(
            restored.Command, f.Path, "unused.out", "unused.err", "unused.exit", null,
            DisableSharedCompilation: true, MandatoryContextFiles: descriptors));
        var prompt = File.ReadAllText(restored.PromptPath!);
        Assert.All(descriptors, descriptor => Assert.Contains(descriptor.Sha256, prompt));
        Assert.Null(task.LastProcess);
        f.AssertRestored();
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void UntypedReservation_RestoresContextAndHandoffWithoutReplacingAttempt(bool stale)
    {
        using var f = new MissingFixture();
        Assert.True(f.Recover().Succeeded);
        var workspace = OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo);
        var task = f.Goal.Tasks.Single();
        var prior = f.Kernel.AddTask(f.Goal.Id, AgentRole.Planner, "Plan the fixture change",
            AgentCatalog.Default().Agents, beforeRole: AgentRole.Developer);
        f.Kernel.ReportTaskProgress(f.Goal.Id, prior.Id, WorkTaskStatus.Completed, "Fixture plan complete.");
        f.Kernel.RecordTaskVerification(f.Goal.Id, prior.Id, new TaskVerificationRecord(
            "fixture plan", f.Path, 0, "Preserve the unique goal commit.", string.Empty, DateTimeOffset.UtcNow));
        Assert.Equal(WorkTaskStatus.Completed, prior.Status);
        var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        WorkerProfileDispatcher.PrepareTask(f.Kernel, f.Goal, task,
            new WorkerProfile("codex-cli", "codex exec --cd {workingDirectory}"),
            workspace.PromptDirectory, f.Path, DateTimeOffset.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSubscriptionModelAlias,
            paidRoute: PaidRouteClassification.Paid, sandboxOptions: sandbox, integrationBranch: f.MainBranch);
        var original = task.LastDispatch!;
        Assert.Null(original.ContextPackageReceipt); // Exercise the legacy alias, not the typed route.
        var contextDirectory = Path.Combine(f.Path, ".orchestrator-context", f.Goal.Id.Value);
        var handoffPath = Path.Combine(f.Path, ".orchestrator-handoff.md");
        var expectedHandoff = File.ReadAllText(handoffPath);
        Assert.True(File.Exists(Path.Combine(contextDirectory, "current-task.md")));
        var fingerprint = Assert.IsType<RetryContextFingerprint>(original.RetryContextFingerprint);
        var at = original.DispatchedAt;
        f.Kernel.ApplyPreparedRetryAdmission(f.Goal.Id, task.Id, RetryAdmissionPolicy.Evaluate(
            task, fingerprint, original.PaidRoute, RetryCause.NewSourceFinding, at, at,
            reservationOwnerId: "fixture-untyped-reservation", reservationLeaseExpiresAt: DateTimeOffset.UnixEpoch));
        var reservation = Assert.Single(task.RetryAdmissionHistory);
        var operations = new GoalDispatchOperations();
        Assert.False(operations.ShouldRefreshPreparedDispatchBeforeStart(task, refreshBeforeStart: true));
        DeleteDirectory(f.Path);
        if (!stale) RunGit(f.Repo, "worktree", "remove", "--force", f.Path);
        Assert.False(Directory.Exists(contextDirectory));
        Assert.False(File.Exists(handoffPath));
        var reachedStart = false;
        var driver = Driver(f, f.Coordinator(), goal =>
        {
            f.AssertRestored();
            Assert.False(operations.ShouldRefreshPreparedDispatchBeforeStart(task, refreshBeforeStart: true));
            var stop = Assert.Throws<InvalidOperationException>(() => operations.StartDispatches(
                f.Kernel, workspace, goal, runner: new BackgroundDispatchRunner(disableProcessStart: true),
                sandboxOptions: sandbox));
            Assert.Contains("process start is disabled", stop.Message, StringComparison.OrdinalIgnoreCase);
            // The real start path restored both inputs before reaching its disabled process boundary.
            Assert.True(Directory.Exists(contextDirectory));
            Assert.Contains(task.Description, File.ReadAllText(Path.Combine(contextDirectory, "current-task.md")));
            Assert.Contains(f.Goal.Objective, File.ReadAllText(Path.Combine(contextDirectory, "objective.md")));
            Assert.Equal(expectedHandoff, File.ReadAllText(handoffPath));
            Assert.Contains(f.Goal.Objective, File.ReadAllText(original.PromptPath!));
            reachedStart = true;
            return DispatchStartOutcome.Started();
        }, _ => throw new InvalidOperationException("recorded dispatch must retain its integration identity"));

        driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(reachedStart);
        Assert.Same(original, task.LastDispatch);
        Assert.Same(original, Assert.Single(task.DispatchHistory));
        Assert.Null(task.LastDispatch!.ContextPackageReceipt);
        Assert.Equal(at, task.LastDispatch.DispatchedAt);
        Assert.Equal(fingerprint, task.LastDispatch.RetryContextFingerprint);
        Assert.Contains(task.RetryAdmissionHistory, receipt => receipt == reservation);
        Assert.Contains(task.RetryAdmissionHistory, receipt => receipt.Decision == RetryAdmissionDecision.ResumedReservation);
        Assert.All(task.RetryAdmissionHistory, receipt =>
        {
            Assert.Equal(at, receipt.LinkedDispatchAt);
            Assert.Equal(fingerprint, receipt.Fingerprint);
        });
        Assert.Null(task.LastProcess);
        f.AssertRestored();
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
