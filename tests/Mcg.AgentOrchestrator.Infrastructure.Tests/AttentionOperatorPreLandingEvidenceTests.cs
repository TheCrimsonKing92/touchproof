using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel safe: each integration test owns its repository or database and captures console output per flow.
public sealed class AttentionOperatorPreLandingEvidenceTests : IClassFixture<IsolatedDotnetRootFixture>
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NextCandidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Criterion = "The operator observes the candidate before publication.";
    private const string Section = "operatorPreLandingEvidence";
    private static readonly DateTimeOffset At = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("operator observation", "\"operator observation\"")]
    [InlineData("inspection", "inspection")]
    public void BoundObligationWithoutAcceptanceRun_ListsRowWithFullShaAndExactCommand(
        string scope, string commandScope)
    {
        var (kernel, goal) = CreateDeclaredGoal();
        var obligation = Map(kernel, goal, Candidate, scope);
        var row = ReadRow(Render(goal));

        Assert.Equal(new[] { "goalId", "obligationId", "criterionNumber", "requiredScope", "candidateSha", "command" },
            row.EnumerateObject().Select(property => property.Name));
        Assert.Equal(goal.Id.Value, row.GetProperty("goalId").GetString());
        Assert.Equal(obligation.Id, row.GetProperty("obligationId").GetString());
        Assert.Equal(1, row.GetProperty("criterionNumber").GetInt32());
        Assert.Equal(scope, row.GetProperty("requiredScope").GetString());
        Assert.Equal(Candidate, row.GetProperty("candidateSha").GetString());
        Assert.Equal($"criterion-evidence-record --goal {goal.Id.Value} {obligation.Id} operator " +
            $"{Candidate} <receipt-id> {commandScope} <passed|failed> <detail>", row.GetProperty("command").GetString());
    }

    [Fact]
    public void Rebind_RowCarriesNewShaAndOldShaIsGone()
    {
        var (kernel, goal) = CreateDeclaredGoal();
        Map(kernel, goal, Candidate);
        Assert.Equal(Candidate, ReadRow(Render(goal)).GetProperty("candidateSha").GetString());

        Map(kernel, goal, NextCandidate);
        var row = ReadRow(Render(goal));
        Assert.Equal(NextCandidate, row.GetProperty("candidateSha").GetString());
        Assert.Contains($" operator {NextCandidate} <receipt-id> ", row.GetProperty("command").GetString());
        Assert.DoesNotContain(Candidate, row.GetRawText());
    }

    [Fact]
    public async Task SqliteRoute_GoalWithoutWait_ListsRowInFullAttention()
    {
        var (kernel, goal) = CreateDeclaredGoal();
        Map(kernel, goal, Candidate);
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            Assert.Empty(await repository.ListOpenHumanInputRequestsAsync());

            var full = AsyncLocalConsoleRouter.Capture(() =>
                CliAttentionJsonQueryCommand.Execute(["attention", "--json"], repository, workspace, new FixedTime()));
            Assert.Equal(goal.Id.Value, ReadRow(full).GetProperty("goalId").GetString());
            Assert.Equal(Candidate, ReadRow(full).GetProperty("candidateSha").GetString());

            var summary = AsyncLocalConsoleRouter.Capture(() =>
                CliAttentionJsonQueryCommand.Execute(["attention", "list", "--summary", "--json"],
                    repository, workspace, new FixedTime()));
            Assert.DoesNotContain($"\"{Section}\"", summary);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PassingReceipt_RemovesRow_AndAbsentReceiptStillBlocksCompletionAndMerge()
    {
        using var fixture = await PostLandingObligationTests.Fixture.Create(includePreLandingOperator: true);
        var gate = Assert.Single(fixture.Goal.CriterionEvidenceObligations,
            item => item.Owner == CriterionEvidenceOwner.Acceptance);
        fixture.Kernel.RecordCriterionEvidence(fixture.Goal.Id, gate.Id, gate.Owner, fixture.Candidate,
            "gate-receipt", gate.RequiredScope, true, "Acceptance passed.");
        var control = Assert.Single(fixture.Goal.GetLandingPrerequisiteObligations(fixture.Candidate));
        control = fixture.Kernel.MapCriterionEvidenceOwner(fixture.Goal.Id, control.CriterionIndex,
            control.CriterionVersion, CriterionEvidenceOwner.Operator, "operator", expectedCandidateSha: fixture.Candidate);
        Assert.Equal(control.Id, ReadRow(Render(fixture.Goal)).GetProperty("obligationId").GetString());

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            fixture.Kernel.CompleteGoal(fixture.Goal.Id, "Attempt completion."));
        Assert.Contains("criterion evidence obligations remain outstanding", refusal.Message);
        Assert.Contains(control.Id, refusal.Message);
        await fixture.Repository.SaveAsync(fixture.Kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = fixture.Goal;
        var merge = AsyncLocalConsoleRouter.Capture(() => AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
            ["workspace", "merge", fixture.Goal.Id.Value], fixture.Repository, fixture.Workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));
        Assert.Contains("Workspace merge blocked by outstanding criterion evidence", merge);
        Assert.Contains(control.Id, merge);
        Assert.Equal(fixture.MainBefore, LandingExecutorTests.ReadGit(fixture.Root, "rev-parse", "main"));

        fixture.Kernel.RecordCriterionEvidence(fixture.Goal.Id, control.Id, control.Owner, fixture.Candidate,
            "failed-check", control.RequiredScope, false, "Control failed.");
        Assert.Equal(fixture.Candidate, ReadRow(Render(fixture.Goal)).GetProperty("candidateSha").GetString());
        fixture.Kernel.RecordCriterionEvidence(fixture.Goal.Id, control.Id, control.Owner, fixture.Candidate,
            "passed-check", control.RequiredScope, true, "Control passed.");
        Assert.DoesNotContain($"\"{Section}\"", Render(fixture.Goal));
    }

    [Fact]
    public void NoOperatorPreLandingObligation_OutputIsByteIdentical()
    {
        // Pre-change envelope pinned by CliAttentionAgentReadEnvelopeContractTests.
        const string baseline = """{"schema":"mcg.attention","schemaVersion":1,"generatedAt":"2026-10-06T00:00:00.0000000Z","payload":{"waits":[],"gateEvidence":[],"items":[],"providerHolds":[],"ownerQuestionHolds":[]}}""";
        var kernel = new AgentOrchestratorKernel();
        Assert.Equal(baseline, Render(kernel.CreateGoal(GoalId.New(), "Ordinary goal")));

        var (unboundKernel, unboundGoal) = CreateDeclaredGoal();
        Assert.Equal(baseline, Render(unboundGoal));
        unboundKernel.MapCriterionEvidenceOwner(unboundGoal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "operator", expectedCandidateSha: Candidate);
        Assert.Equal(baseline, Render(unboundGoal));
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void VerifiedWorkerRound_BindsAndRebindsOutstandingEvidence(AgentRole role)
    {
        var task = new TaskSpec(TaskId.New(), "Prepare the candidate", role);
        var (kernel, goal) = CreateDeclaredGoal(task);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        Assert.Null(Assert.Single(goal.CriterionEvidenceObligations).ExpectedCandidateSha);
        CloseWorkerRound(kernel, goal, task, new string('c', 40), Candidate, At);
        Assert.Equal(Candidate, ReadRow(Render(goal)).GetProperty("candidateSha").GetString());

        kernel.RetryTask(goal.Id, task.Id, "Prepare the next candidate.");
        CloseWorkerRound(kernel, goal, task, Candidate, NextCandidate, At.AddMinutes(1));
        var row = ReadRow(Render(goal));
        Assert.Equal(NextCandidate, row.GetProperty("candidateSha").GetString());
        Assert.DoesNotContain(Candidate, row.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerBoundEvidence_AllowsLaterReceiptUntilExplicitlyMapped(bool map)
    {
        var task = new TaskSpec(TaskId.New(), "Prepare the candidate", AgentRole.Developer);
        var (kernel, goal) = CreateDeclaredGoal(task);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        CloseWorkerRound(kernel, goal, task, new string('c', 40), Candidate, At);
        var obligation = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(Candidate, obligation.ExpectedCandidateSha);
        if (map)
        {
            Map(kernel, goal, Candidate);
            Assert.Throws<InvalidOperationException>(() => RecordNextEvidence());
            Assert.Equal(Candidate, ReadRow(Render(goal)).GetProperty("candidateSha").GetString());
        }
        else
        {
            Assert.True(RecordNextEvidence().HasSatisfiedEvidenceFor(NextCandidate));
            Assert.DoesNotContain($"\"{Section}\"", Render(goal));
        }

        CriterionEvidenceObligation RecordNextEvidence() => kernel.RecordCriterionEvidence(goal.Id, obligation.Id,
            obligation.Owner, NextCandidate, "later-check", obligation.RequiredScope, true, "Control passed.");
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateDeclaredGoal(params TaskSpec[] tasks)
    {
        var kernel = new AgentOrchestratorKernel(new FixedTime());
        var goal = tasks.Length == 0
            ? kernel.CreateGoal(GoalId.New(), Criterion) : kernel.CreateGoal(Criterion, tasks);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(Criterion, [Criterion], VerificationClass.TestVerifiable, [], [])
        {
            OperatorOwnedAcceptanceCriteria = [Criterion]
        });
        return (kernel, goal);
    }

    private static CriterionEvidenceObligation Map(AgentOrchestratorKernel kernel, Goal goal, string sha,
        string scope = "operator observation") => kernel.MapCriterionEvidenceOwner(
            goal.Id, 0, 1, CriterionEvidenceOwner.Operator, "operator", scope, expectedCandidateSha: sha);

    private static string Render(Goal goal) => AttentionAgentReadView.Render([], [], new FixedTime(), goals: [goal]);

    private static JsonElement ReadRow(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Assert.Single(document.RootElement.GetProperty("payload").GetProperty(Section).EnumerateArray()).Clone();
    }

    private static void CloseWorkerRound(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        string baseCommit, string resultCommit, DateTimeOffset started)
    {
        const string directory = @"C:\fixture";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "prepare", directory, started, BaseCommit: baseCommit));
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit);
        var output = RoleReportEvidenceClosureTests.DeveloperOutput("deferred - CandidateTests");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "prepare", directory, 0, output, "", started.AddSeconds(1),
            WorkerResultPresent: true, HasCommittedChanges: true, DispatchStartedAt: started));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
    }

    private sealed class FixedTime : TimeProvider, IClock
    {
        public DateTimeOffset UtcNow => At;
        public override DateTimeOffset GetUtcNow() => At;
    }
}
