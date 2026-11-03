using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: git, result artifacts, prompts and kernel state use one isolated fixture root.
public sealed class GateTestSelectorDiagnosticContextTests
{
    private const string Prediction = "MergeTrainReproductionThirdMemberTests";
    private const string InvalidEvidence = "MergeTrainReproductionInvalidEvidenceTests.InvalidFirstMemberDoesNotDiscardAValidThirdMemberFailure";
    private const string Attribution = "MergeTrainReproductionAttributionTests.IsolatedReproductionRemovesAndImplicatesTheActualMember";

    [Theory]
    [InlineData(AgentRole.Tester, true)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Reviewer, false)]
    public void HistoricalMismatchReachesPreparedRoleContext(AgentRole role, bool typed)
    {
        using var f = new Fixture(role);
        var before = GateTestObligationEvaluator.Evaluate(f.Goal, f.OrchestratorDirectory, f.Candidate);
        var selectedBefore = f.Task.PreReviewEvidenceReceipt;
        var calls = 0;
        var prepared = WorkerProfileDispatcher.PrepareTask(f.Kernel, f.Goal, f.Task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"), Path.Combine(f.Root, "prompts"), f.Root, DateTimeOffset.UtcNow,
            providerName: typed ? "OpenAI" : null, modelName: typed ? AgentCatalog.OpenAiSolSubscriptionModelAlias : null,
            reviewerScopeChangedFiles: [], reviewerScopeMergeBase: f.Candidate, reviewerScopeTotalChangedFileCount: 0,
            reviewerMergeTreeClean: true, reviewerMergeTreeConflictPaths: [], reviewerMergeTreeTotalConflictPathCount: 0,
            preparationFindingsResolver: (goal, candidate) =>
            {
                calls++;
                Assert.Equal(f.Candidate, candidate);
                return GateTestSelectorDiagnosticPreparation.Build(goal, candidate, f.Root, f.OrchestratorDirectory, f.Kernel.HumanInputRequests);
            });
        Assert.Equal(1, calls);
        Assert.Equal(WorkTaskStatus.Assigned, f.Task.Status);
        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.Contains(GateTestSelectorDiagnostic.Marker, prompt);
        Assert.Contains(Prediction, prompt);
        Assert.Contains(InvalidEvidence, prompt);
        Assert.Contains(Attribution, prompt);
        Assert.Contains("advisory identity projection", prompt);
        if (typed)
        {
            Assert.Contains(prepared.Task.LastDispatch!.ContextPackageReceipt!.Sections,
                section => section.LogicalIdentity == "context/subscription-preflight.md" &&
                    section.DeliveryMode == ContextDeliveryMode.InlineFull);
        }
        var contextRoot = Path.Combine(f.Root, ".orchestrator-context", f.Goal.Id.Value);
        var findings = File.ReadAllText(Path.Combine(contextRoot, "subscription-preflight.md"));
        Assert.Contains(Prediction, findings);
        Assert.Contains(InvalidEvidence, findings);
        Assert.Contains(Attribution, findings);
        Assert.Contains("\"Status\":\"Unresolved\"", findings);
        Assert.Contains("Selector naming diagnostic", File.ReadAllText(Path.Combine(contextRoot, "current-task.md")));
        Assert.Contains("subscription-preflight.md", prompt);
        Assert.Equal(new[] { Prediction }, Assert.Single(f.Goal.GateTestObligations).Selectors);
        Assert.Equal(before.Select(item => item.Outcome),
            GateTestObligationEvaluator.Evaluate(f.Goal, f.OrchestratorDirectory, f.Candidate).Select(item => item.Outcome));
        Assert.Same(selectedBefore, f.Task.PreReviewEvidenceReceipt);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Fact]
    public void ReadyBatchForwardsDiagnosticWithoutCreatingEvidenceSelection()
    {
        using var f = new Fixture(AgentRole.Tester);
        var calls = 0;
        var prepared = WorkerProfileDispatcher.PrepareReadyTasks(f.Kernel, f.Goal,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"), Path.Combine(f.Root, "prompts"), f.Root,
            DateTimeOffset.UtcNow, preparationFindingsResolver: (goal, candidate) =>
            {
                calls++;
                return GateTestSelectorDiagnosticPreparation.Build(goal, candidate, f.Root, f.OrchestratorDirectory, []);
            });
        Assert.Equal(1, calls);
        Assert.Contains(InvalidEvidence, File.ReadAllText(Assert.Single(prepared).PromptPath));
        Assert.Null(f.Task.PreReviewEvidenceReceipt);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Fact]
    public void PreparationRefreshDoesNotReusePreviousCandidateOrBrief()
    {
        using var f = new Fixture(AgentRole.Tester);
        var current = string.Join("\n", GateTestSelectorDiagnosticPreparation.Build(f.Goal, f.Candidate,
            f.Root, f.OrchestratorDirectory, []));
        Assert.Contains(InvalidEvidence, current);
        var changed = string.Join("\n", GateTestSelectorDiagnosticPreparation.Build(f.Goal, new string('b', 40),
            f.Root, f.OrchestratorDirectory, []));
        Assert.DoesNotContain(InvalidEvidence, changed);
        Assert.Contains("unknown", changed);
        Assert.Contains("\"Status\":\"Unknown\"", changed);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-selector-context", Guid.NewGuid().ToString("N"));
        internal string OrchestratorDirectory => Path.Combine(Root, ".orchestrator");
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal TaskSpec Task { get; }
        internal string Candidate { get; }

        internal Fixture(AgentRole role)
        {
            Directory.CreateDirectory(Root);
            Assert.Equal(0, GitCli.Run(Root, "init", "--initial-branch=main").ExitCode);
            File.WriteAllText(Path.Combine(Root, "fixture.txt"), "diagnostic context");
            Assert.Equal(0, GitCli.Run(Root, "add", "fixture.txt").ExitCode);
            Assert.Equal(0, GitCli.Run(Root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--quiet", "-m", "fixture").ExitCode);
            Candidate = GitCli.Run(Root, "rev-parse", "HEAD").Output.Trim();
            var kernel = new AgentOrchestratorKernel();
            var developer = new TaskSpec(TaskId.New(), "Implement the candidate", AgentRole.Developer);
            var target = new TaskSpec(TaskId.New(), "Inspect candidate evidence", role);
            var goal = kernel.CreateGoal("Surface predicted selector mismatch", [developer, target]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord("fixture", Root, 0,
                $"WORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: deferred - {InvalidEvidence}, {Attribution}\n" +
                $"commit: {Candidate}\nblockers: none\nassigned_scope_complete: true\nmodel_fit: adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT", "", DateTimeOffset.UtcNow));
            Assert.Equal(WorkTaskStatus.Completed, developer.Status);
            Kernel = GateTestObligationIntakeTests.Seed(kernel, goal, [Prediction]);
            Goal = Kernel.GetGoal(goal.Id);
            Task = Goal.Tasks.Single(task => task.RequiredRole == role);
            var directory = Path.Combine(OrchestratorDirectory, "acceptance-gate-attempts", Goal.Id.Value);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "identities.trx"),
                $"<TestRun><Results><UnitTestResult testName=\"{InvalidEvidence}\" outcome=\"Passed\"/>" +
                $"<UnitTestResult testName=\"{Attribution}\" outcome=\"Passed\"/></Results></TestRun>");
            File.WriteAllText(Path.Combine(directory, "fixture.attempt.json"), JsonSerializer.Serialize(new
            {
                attemptId = "fixture", goalId = Goal.Id.Value, branchHeadSha = Candidate,
                kind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
                outcome = ConductorParallelAcceptanceAttemptOutcome.Passed, completedAt = DateTimeOffset.UtcNow,
                briefVersion = Goal.AuthoritativeBrief.Version, testResultPaths = new[] { "identities.trx" }
            }));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
