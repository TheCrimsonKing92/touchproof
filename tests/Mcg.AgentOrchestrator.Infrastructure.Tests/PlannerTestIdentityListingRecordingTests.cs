using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its kernel and injects instance sources only.
public sealed class PlannerTestIdentityListingRecordingTests
{
    private const string Identity = "process-inspector::tree_cli::pid_json_lists_spawned_child";
    private const string Name = "pid_json_lists_spawned_child";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnmatchedIdentity_BothPaths_RejectBeforeObligationsOrWaits(bool legacy)
    {
        var fixture = new Fixture();
        fixture.Record(Directive(["process-inspector::tree_cli.pid_json_lists_spawned_child"]), legacy);

        AssertRejected(fixture);
        Assert.Contains("process-inspector::tree_cli.pid_json_lists_spawned_child",
            fixture.Planner.LastVerification!.OrchestratorFailureReason!);
        Assert.Equal(1, fixture.ListingCalls);
    }

    [Theory]
    [InlineData(false, Identity)]
    [InlineData(true, Identity)]
    [InlineData(false, Name)]
    [InlineData(true, Name)]
    public void MatchingIdentityOrName_BothPaths_RecordsOriginalSelector(bool legacy, string selector)
    {
        var fixture = new Fixture();
        fixture.Record(Directive([selector]), legacy);

        Assert.Equal([selector], Assert.Single(fixture.Goal.GateTestObligations).Selectors);
        Assert.Empty(fixture.Kernel.ExportSnapshot().HumanInputRequests);
        Assert.NotEqual(WorkTaskStatus.Failed, fixture.Planner.Status);
        Assert.Equal(1, fixture.ListingCalls);
    }

    [Fact]
    public void LateUnmatchedIdentity_Batch_RecordsNoNewObligation()
    {
        var fixture = new Fixture();
        fixture.Record(Directive([Identity], "valid") + "\n" + Directive(["missing"], "invalid"));

        AssertRejected(fixture);
        Assert.Equal(1, fixture.ListingCalls);
    }

    [Fact]
    public void MultipleValidDirectives_Batch_UsesOneSnapshot()
    {
        var fixture = new Fixture();
        fixture.Record(Directive([Identity], "first") + "\n" + Directive([Name], "second"));

        Assert.Equal(2, fixture.Goal.GateTestObligations.Count);
        Assert.Equal(new[] { Identity, Name }, fixture.Goal.GateTestObligations.SelectMany(item => item.Selectors));
        Assert.Equal(1, fixture.ListingCalls);
    }

    [Fact]
    public void NextPlanningRound_ChangedListing_UsesFreshSnapshot()
    {
        var fixture = new Fixture();
        var batch = AgentOutputDirectives.ParseHumanInputRequest(Directive([Identity]), AgentRole.Planner);
        Assert.Null(fixture.Kernel.RecordPlannerEvidenceBatch(fixture.Goal, fixture.Planner, batch,
            fixture.Goal.AuthoritativeBrief.Version, out _));
        fixture.Kernel.SetTestIdentityListingSource((_, _) => [new("other", "other")]);
        var next = AgentOutputDirectives.ParseHumanInputRequest(Directive([Identity], "next"), AgentRole.Planner);

        Assert.Contains("replan", fixture.Kernel.RecordPlannerEvidenceBatch(fixture.Goal, fixture.Planner, next,
            fixture.Goal.AuthoritativeBrief.Version, out _)!);
        Assert.Equal([Identity], Assert.Single(fixture.Goal.GateTestObligations).Selectors);
    }

    [Fact]
    public void Snapshot_Batch_DoesNotHoldRequestLock()
    {
        var kernel = new AgentOrchestratorKernel();
        var requestLock = new object();
        var task = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Lock scope", [task]);
        bool? lockHeld = null;
        var recorder = new PlannerEvidenceRecorder(kernel, requestLock, new SystemClock(), (_, _, _, _) => { },
            (_, _) => [], (_, _) => { lockHeld = Monitor.IsEntered(requestLock); return []; });

        Assert.Null(recorder.Record(goal, task,
            AgentOutputDirectives.ParseHumanInputRequest(Directive([Identity]), AgentRole.Planner), null, out _));
        Assert.NotNull(lockHeld);
        Assert.False(lockHeld.Value);
    }

    private static void AssertRejected(Fixture fixture)
    {
        Assert.Equal(WorkTaskStatus.Failed, fixture.Planner.Status);
        Assert.Equal("planner-output-contract-rejected", fixture.Planner.LastVerification!.CompletionVerdictRule);
        Assert.False(fixture.Planner.LastVerification.CompletionVerdictVerifiedSuccess);
        Assert.Contains("replan", fixture.Planner.LastVerification.OrchestratorFailureReason!);
        Assert.Empty(fixture.Goal.GateTestObligations);
        Assert.Empty(fixture.Kernel.ExportSnapshot().HumanInputRequests);
    }

    private static string Directive(string[] selectors, string key = "gate") =>
        "PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(new
        {
            criterion_index = 1, evidence_key = key, availability = "post-implementation", owner = "acceptance-gate",
            tests = selectors, needed = "candidate receipt", reason = "Acceptance executes the candidate"
        });

    private sealed class Fixture
    {
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal TaskSpec Planner { get; } = new(TaskId.New(), "Plan", AgentRole.Planner);
        internal Goal Goal { get; }
        internal int ListingCalls { get; private set; }

        internal Fixture()
        {
            Kernel.SetDeclaredGateCheckSource((_, _) => [new("tests", _ => false, "junit-command", "cargo")]);
            Kernel.SetTestIdentityListingSource((_, _) =>
            {
                ListingCalls++;
                return [new(Identity, Name)];
            });
            Goal = Kernel.CreateGoal("Validate planned identities", [Planner]);
            Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Validate selectors",
                ["Acceptance executes candidate checks."], VerificationClass.TestVerifiable, [], []));
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Kernel.RecordTaskDispatch(Goal.Id, Planner.Id, new TaskDispatchRecord("planner", "plan", ".", DateTimeOffset.UtcNow));
        }

        internal void Record(string output, bool legacy = false)
        {
            var parsed = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);
            Assert.False(parsed.IsMalformed, parsed.Diagnostic);
            Assert.NotEmpty(parsed.Directives);
            var directive = legacy ? Assert.Single(parsed.Directives) : null;
            var fullOutput = output + "\nWORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: not-run\ncommit: none\nblockers: none\nmodel_fit: adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";
            Kernel.RecordDispatchExecutionResult(Goal.Id, Planner.Id, new TaskVerificationRecord(
                "plan", ".", 0, fullOutput, "", DateTimeOffset.UtcNow, WorkerResultPresent: true,
                HumanInputQuestion: directive?.Question, HumanInputKind: directive?.Kind,
                HumanInputEvidenceOwner: directive?.EvidenceOwner,
                DispatchAttemptAt: Planner.LastDispatch!.DispatchedAt,
                PlannerEvidenceBatch: legacy ? new(null, null) : parsed));
        }
    }
}
