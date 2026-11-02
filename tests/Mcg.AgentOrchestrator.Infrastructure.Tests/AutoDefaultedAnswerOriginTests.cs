using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: kernels, injected clocks, console captures and temporary databases are test-owned.
public sealed class AutoDefaultedAnswerOriginTests
{
    private const string DefaultAnswer = "Use proposed scope.";

    [Fact]
    public void Sweep_StaleRequestAndYoungerSibling_RecordAutoDefaultedAnswers()
    {
        var clock = new Clock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Default stale clarification");
        var request = kernel.RequestHumanInput(goal.Id, null, "Use proposed scope?",
            suggestedDefaultAnswer: DefaultAnswer);
        var snapshot = kernel.ExportSnapshot();
        var siblingId = HumanInputRequestId.New();
        // Reproduce a stored duplicate; normal request creation deduplicates it. The younger
        // request is below the sweep threshold and completes only through sibling resolution.
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            HumanInputRequests = [.. snapshot.HumanInputRequests,
                snapshot.HumanInputRequests.Single() with
                {
                    Id = siblingId.Value, RequestedAt = clock.UtcNow.AddHours(23)
                }]
        }, clock);
        clock.Advance(TimeSpan.FromHours(25));

        var result = Assert.Single(kernel.SweepStaleHumanWaits(TimeSpan.FromHours(24)));

        Assert.Equal(request.Id, result.RequestId);
        Assert.Equal(HumanWaitPolicyResolution.Defaulted, result.Resolution);
        foreach (var id in new[] { request.Id, siblingId })
        {
            var completed = kernel.GetHumanInputRequest(id);
            var answer = Assert.IsType<HumanInputAnswerRecord>(completed.AuthoritativeAnswer);
            Assert.True(completed.IsCompleted);
            Assert.False(completed.WasDismissed);
            Assert.True(answer.IsAutoDefaulted);
            Assert.Equal(HumanInputAnswerOrigin.Operator, answer.Origin);
            Assert.Equal(DefaultAnswer, answer.Text);
            Assert.Equal(clock.UtcNow, answer.AnsweredAt);
        }
        Assert.Equal(request.Id, kernel.GetHumanInputRequest(siblingId).SupersededByRequestId);
    }

    [Theory]
    [InlineData(HumanInputAnswerOrigin.Operator)]
    [InlineData(HumanInputAnswerOrigin.Worker)]
    public void SubmitHumanInput_ExplicitAnswer_IsNotAutoDefaulted(HumanInputAnswerOrigin origin)
    {
        var kernel = new AgentOrchestratorKernel(new Clock());
        var goal = kernel.CreateGoal("Explicit clarification");
        var request = kernel.RequestHumanInput(goal.Id, null, "Use proposed scope?",
            suggestedDefaultAnswer: DefaultAnswer);

        kernel.SubmitHumanInput(request.Id, DefaultAnswer, origin: origin);

        var answer = Assert.IsType<HumanInputAnswerRecord>(request.AuthoritativeAnswer);
        Assert.False(answer.IsAutoDefaulted);
        Assert.Equal(origin, answer.Origin);
        Assert.Equal(DefaultAnswer, answer.Text);
    }

    [Fact]
    public async Task Snapshot_AutoDefaultedAnswer_RoundTripsThroughStateRepository()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel(new Clock());
            var goal = kernel.CreateGoal("Persist automatic answer");
            var request = kernel.RequestHumanInput(goal.Id, null, "Use proposed scope?",
                suggestedDefaultAnswer: DefaultAnswer);
            Assert.Single(kernel.SweepStaleHumanWaits(TimeSpan.Zero));
            Assert.True(request.AuthoritativeAnswer!.IsAutoDefaulted);
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var repository = new SqliteOrchestratorStateRepository(db);

            await repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()));
            var restored = await repository.LoadAsync();

            var answer = Assert.IsType<HumanInputAnswerRecord>(restored.GetHumanInputRequest(request.Id).AuthoritativeAnswer);
            Assert.True(answer.IsAutoDefaulted);
            Assert.Equal(request.AuthoritativeAnswer, answer);
            Assert.Equal(DefaultAnswer, restored.GetHumanInputRequest(request.Id).Answer);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void StoredFlag_PreviousAnswerShapeReadsIt_AndLegacyAnswersDefaultToFalse()
    {
        // Same enum-name converter and default unmapped-property handling as the state store.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var kernel = new AgentOrchestratorKernel(new Clock());
        var goal = kernel.CreateGoal("Previous build compatibility");
        var request = kernel.RequestHumanInput(goal.Id, null, "Use proposed scope?",
            suggestedDefaultAnswer: DefaultAnswer);
        Assert.Single(kernel.SweepStaleHumanWaits(TimeSpan.Zero));
        var answer = Assert.IsType<HumanInputAnswerRecord>(request.AuthoritativeAnswer);
        var json = JsonSerializer.Serialize(answer, options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Operator", document.RootElement.GetProperty("Origin").GetString());
        Assert.True(document.RootElement.GetProperty("IsAutoDefaulted").GetBoolean());

        var previous = Assert.IsType<PreviousAnswer>(JsonSerializer.Deserialize<PreviousAnswer>(json, options));

        Assert.Equal(answer.Id, previous.Id);
        Assert.Equal(answer.Text, previous.Text);
        Assert.Equal(answer.AnsweredAt, previous.AnsweredAt);
        Assert.Equal(HumanInputAnswerOrigin.Operator, previous.Origin);
        Assert.Equal(answer.BriefVersion, previous.BriefVersion);
        var legacy = Assert.IsType<HumanInputAnswerRecord>(JsonSerializer.Deserialize<HumanInputAnswerRecord>(
            JsonSerializer.Serialize(previous, options), options));
        Assert.False(legacy.IsAutoDefaulted);
        Assert.Equal(HumanInputAnswerOrigin.Operator, legacy.Origin);
        Assert.Equal(DefaultAnswer, legacy.Text);
    }

    [Fact]
    public void Rebinding_IgnoresAutoDefault_ButUsesIdenticalOperatorAnswer()
    {
        const string rename = "OldNameTests becomes NewNameTests";
        var kernel = new AgentOrchestratorKernel(new Clock());
        var goal = kernel.CreateGoal("Rename gate selector");
        kernel = GateTestObligationIntakeTests.Seed(kernel, goal, ["OldNameTests"]);
        goal = kernel.GetGoal(goal.Id);
        var obligation = Assert.Single(goal.GateTestObligations);
        var automatic = kernel.RequestHumanInput(goal.Id, null, "Suggested rename?", suggestedDefaultAnswer: rename);
        Assert.Single(kernel.SweepStaleHumanWaits(TimeSpan.Zero));
        Assert.Equal(HumanInputAnswerOrigin.Operator, automatic.AuthoritativeAnswer!.Origin);
        Assert.Equal(obligation.BriefVersion, automatic.AuthoritativeAnswer.BriefVersion);
        Assert.Single(AnsweredSelectorRebinding.Parse(automatic.Answer));

        Assert.Empty(AnsweredSelectorRebinding.For(goal, obligation, kernel.HumanInputRequests));

        var ruling = kernel.RequestHumanInput(goal.Id, null, "Operator rename?");
        kernel.SubmitHumanInput(ruling.Id, rename);
        Assert.Equal(automatic.Answer, ruling.Answer);
        var rebind = Assert.Single(AnsweredSelectorRebinding.For(goal, obligation, kernel.HumanInputRequests));
        Assert.Equal("OldNameTests", rebind.OldClass);
        Assert.Equal("NewNameTests", rebind.NewClass);
        Assert.Equal(ruling.Id.Value, rebind.RequestId);
        Assert.Equal(ruling.AuthoritativeAnswer!.Id, rebind.AnswerId);
    }

    [Fact]
    public void StagnationReset_IgnoresAutoDefault_ButUsesOperatorRuling()
    {
        const string stableId = "finding-provenance";
        var clock = new Clock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Finding reset provenance");
        var prefix = FindingStagnationDecision.FingerprintPrefix(goal.Id, stableId);
        var automatic = kernel.RequestHumanInputDeduplicated(goal.Id, null, "Suggested ruling?",
            suggestedDefaultAnswer: DefaultAnswer, questionFingerprint: prefix + "automatic").Request;
        Assert.Single(kernel.SweepStaleHumanWaits(TimeSpan.Zero));
        Assert.True(automatic.AuthoritativeAnswer!.IsAutoDefaulted);

        Assert.Equal(DateTimeOffset.MinValue, FindingStagnationTracker.GetResetAt(goal, kernel.HumanInputRequests, stableId));

        clock.Advance(TimeSpan.FromMinutes(1));
        var ruling = kernel.RequestHumanInputDeduplicated(goal.Id, null, "Operator ruling?",
            questionFingerprint: prefix + "operator").Request;
        kernel.SubmitHumanInput(ruling.Id, DefaultAnswer);
        Assert.Equal(clock.UtcNow, FindingStagnationTracker.GetResetAt(goal, kernel.HumanInputRequests, stableId));
    }

    [Fact]
    public void Listing_AutoDefaultAndOperatorCorrection_ShowTheirCurrentResolution()
    {
        var clock = new Clock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("List automatic answer");
        var request = kernel.RequestHumanInput(goal.Id, null, "Use proposed scope?",
            suggestedDefaultAnswer: DefaultAnswer);
        Assert.Single(kernel.SweepStaleHumanWaits(TimeSpan.Zero));

        var text = AsyncLocalConsoleRouter.Capture(() => HumanWaitsTextView.Print([request], clock.UtcNow, includeHistory: true));

        Assert.Contains("status: auto-defaulted", text);
        Assert.Contains("resolution: " + DefaultAnswer, text);
        Assert.DoesNotContain("status: answered", text);
        kernel.SupersedeHumanInput(goal.Id, request.Id, "Use corrected scope.", HumanInputAnswerOrigin.Operator);
        Assert.False(request.AuthoritativeAnswer!.IsAutoDefaulted);
        Assert.True(request.AnswerHistory[0].IsAutoDefaulted);
        text = AsyncLocalConsoleRouter.Capture(() => HumanWaitsTextView.Print([request], clock.UtcNow, includeHistory: true));
        Assert.Contains("status: answered", text);
        Assert.Contains("resolution: Use corrected scope.", text);
        Assert.DoesNotContain("status: auto-defaulted", text);
    }

    // Exact pre-change constructor shape, intentionally without the new stored flag.
    private sealed record PreviousAnswer(string Id, string Text, DateTimeOffset AnsweredAt,
        HumanInputAnswerOrigin Origin = HumanInputAnswerOrigin.Operator,
        string? SupersededByAnswerId = null, int? BriefVersion = null)
    {
        public bool IsRetracted => SupersededByAnswerId is not null;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        internal void Advance(TimeSpan by) => UtcNow += by;
    }
}
