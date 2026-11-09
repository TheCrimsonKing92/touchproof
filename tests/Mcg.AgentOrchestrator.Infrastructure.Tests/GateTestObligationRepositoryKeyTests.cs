using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: parser inputs, in-memory kernels and fixture-owned home receipts.
public sealed class GateTestObligationRepositoryKeyTests
{
    private const string Request = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"repository-test","availability":"post-implementation","owner":"acceptance-gate","tests":["record.TestReadInvalidFixtures"],"needed":"candidate repository receipt","reason":"candidate required","repository":"crosswalk"}
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("default")]
    public void LegacyAndHomeKeys_WithHomeReceipt_HaveIdenticalEvaluation(string? key)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt([("AlphaTests.Row", "Passed")]);
        // Literal pre-change stored shape: there is no repository member to deserialize.
        var legacy = JsonSerializer.Deserialize<GateTestObligation>("""
            {"Id":"legacy","BriefVersion":1,"Selectors":["AlphaTests"],"RequestingTaskId":null,
             "Question":"home receipt","RecordedAt":"1970-01-01T00:00:00+00:00"}
            """)!;
        Assert.Null(legacy.RepositoryKey);
        var baseline = Evaluate(fixture, legacy);
        var result = Evaluate(fixture, legacy with { RepositoryKey = key });

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, baseline.Outcome);
        Assert.Equal(baseline.Outcome, result.Outcome);
        Assert.Equal(baseline.Selectors, result.Selectors);
        Assert.Equal(id, result.SourceId);
        Assert.Equal(baseline.SourceId, result.SourceId);
        Assert.Null(result.PendingDetail);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(legacy));
        Assert.False(json.RootElement.TryGetProperty("RepositoryKey", out _));
    }

    [Theory]
    [InlineData("crosswalk")]
    [InlineData(" Crosswalk ")]
    public void RequestRepository_StringKey_PreservesWireValueAndRoundTrips(string key)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(Request.Replace("crosswalk", key), AgentRole.Planner);
        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        var payload = Assert.Single(parsed.Directives).PlannerEvidence!;
        Assert.Equal(key, payload.Repository);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal(key, json.RootElement.GetProperty("repository").GetString());
        Assert.Equal(key, JsonSerializer.Deserialize<PlannerEvidencePayload>(json.RootElement.GetRawText())!.Repository);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void RequestRepository_NonStringKey_RejectsRequest(string value)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            Request.Replace("\"repository\":\"crosswalk\"", "\"repository\":" + value), AgentRole.Planner);
        Assert.True(parsed.IsMalformed);
        Assert.Contains("repository", parsed.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(parsed.Directives);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestRepository_OmittedOrNull_PreservesLegacyPayload(bool explicitNull)
    {
        var text = explicitNull ? Request.Replace("\"repository\":\"crosswalk\"", "\"repository\":null")
            : Request.Replace(",\"repository\":\"crosswalk\"", "");
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(text, AgentRole.Planner);
        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        var payload = Assert.Single(parsed.Directives).PlannerEvidence!;
        Assert.Null(payload.Repository);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.False(json.RootElement.TryGetProperty("repository", out _));
    }

    [Fact]
    public void SameRequestIdentity_DifferentRepository_RejectsConflictingBatch()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(
            Request + "\n" + Request.Replace("crosswalk", "default"), AgentRole.Planner);
        Assert.True(parsed.IsMalformed);
        Assert.Empty(parsed.Directives);
    }

    [Fact]
    public void TypedPlannerCompletion_RepositoryRequest_PersistsKeyOnObligation()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Repository evidence", [planner, new(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Repository evidence",
            ["Acceptance executes repository checks."], VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var now = DateTimeOffset.UnixEpoch;
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner", "plan", "C:\\repo", now,
            BriefVersion: goal.AuthoritativeBrief.Version, BriefSnapshot: goal.Objective));
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(Request, AgentRole.Planner);
        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        kernel.RecordDispatchExecutionResult(goal.Id, planner.Id, new TaskVerificationRecord(
            "plan", "C:\\repo", 0, Request, "", now, WorkerResultPresent: true,
            DispatchAttemptAt: now, PlannerEvidenceBatch: parsed));

        var obligation = Assert.Single(goal.GateTestObligations);
        Assert.Equal("crosswalk", obligation.RepositoryKey);
        Assert.Equal("crosswalk", obligation.PlannerEvidence!.Repository);
        Assert.NotNull(obligation.PlannerIdentity);
        var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(kernel.ExportSnapshot()))!;
        Assert.Equal("crosswalk", Assert.Single(AgentOrchestratorKernel.FromSnapshot(snapshot)
            .GetGoal(goal.Id).GateTestObligations).RepositoryKey);
    }

    private static GateTestObligationEvaluator.Result Evaluate(ProspectiveEvidenceGateFixture fixture, GateTestObligation obligation)
    {
        var snapshot = fixture.Kernel.ExportSnapshot();
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with { GateTestObligations = [obligation] }]
        });
        return Assert.Single(GateTestObligationEvaluator.Evaluate(kernel.GetGoal(fixture.Goal.Id),
            fixture.OrchestratorDirectory, fixture.CandidateSha,
            currentRepositoryTuple: () => throw new InvalidOperationException("Home evaluation must not compute a tuple")));
    }
}
