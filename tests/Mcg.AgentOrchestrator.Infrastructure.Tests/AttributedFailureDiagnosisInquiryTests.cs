using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;
using static AttributedFailureDiagnosisCoordinatorTests;

// Parallel-safe: unique artifact directories, in-memory goals, fake process and git seams.
public sealed class AttributedFailureDiagnosisInquiryTests
{
    [Theory]
    [InlineData("codex-cli", ProviderKind.OpenAICodexCli, "--sandbox read-only")]
    [InlineData("claude-cli", ProviderKind.AnthropicClaudeCli, "--permission-mode plan")]
    public async Task ReviewerCommandIsFreshReadOnlyAndReceivesCandidateFailureContext(string profile, ProviderKind provider, string capability)
    {
        using var fixture = new SoloCorrectionTestFixture();
        var route = Route(fixture.Goal, detail: "reproduced Tests.Fails: first message\r\nstack trace\r\nfailed Missing.Fails: other first\ncontinuation",
            tests: ["Absent.Fails", "Missing.Fails", "Tests.Fails"]);
        WorkerProcessRunRequest? captured = null;
        var dispatcher = new InquiryDispatcher((request, _) =>
        {
            captured = request;
            return Task.FromResult(new WorkerProcessRunResult(0, "DIAGNOSIS_STATUS: cause-found\nCause at src/Thing.cs:1; fix; invariant.", ""));
        }, headResolver: _ => Candidate, capturedHeadIsAncestor: (_, _, _) => true);
        var reviewer = new AgentDefinition(AgentId.New(), "Review", AgentRole.Reviewer,
            new("provider", "reviewer-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            Subscription: new(profile, "reviewer-model", "high"));
        var inquiry = new AttributedFailureDiagnosisInquiry(dispatcher, [reviewer], fixture.Root, fixture.Root, fixture.Root);
        var outcome = await inquiry.RunAsync(fixture.Goal, route, AttributedFailureDiagnosisCoordinator.DefaultTimeout, CancellationToken.None);
        Assert.True(outcome.CauseFound);
        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromSeconds(120), captured.Timeout);
        Assert.Contains(capability, captured.Command);
        Assert.Contains("--model 'reviewer-model'", captured.Command);
        foreach (var forbidden in new[] { "--resume", " resume ", "workspace-write", "danger-full-access", "acceptEdits", "bypassPermissions" })
            Assert.DoesNotContain(forbidden, captured.Command);
        Assert.Contains("Tests.Fails: first message", captured.StandardInput!);
        Assert.Contains("Missing.Fails: other first", captured.StandardInput!);
        Assert.Contains("Absent.Fails: absent", captured.StandardInput!);
        Assert.Contains(route.Failure!.ReproductionDetail!, captured.StandardInput!);
        Assert.Contains(Candidate, captured.StandardInput!);
        Assert.Contains(fixture.Goal.Objective, captured.StandardInput!);
        Assert.Contains("candidate-relative file path and line, smallest fix, and what must stay unchanged", captured.StandardInput!);
        Assert.Contains("Do not write files", captured.StandardInput!);
        var receipt = Assert.Single(Directory.GetFiles(fixture.Root, "*.receipt.json"));
        var json = File.ReadAllText(receipt);
        Assert.Contains("FreshExec", json);
        Assert.Equal(provider, inquiry.ResolveAgent(fixture.Goal)!.ProviderKind);
    }

    [Theory]
    [InlineData(1, false, "failed")]
    [InlineData(0, true, "timed-out")]
    [InlineData(0, false, "no-cause")]
    public async Task ProcessFailureTimeoutAndUnusableOutputBecomeUnavailable(int exit, bool timedOut, string status)
    {
        using var fixture = new SoloCorrectionTestFixture();
        var dispatcher = new InquiryDispatcher((_, _) => Task.FromResult(new WorkerProcessRunResult(exit, "unparseable", "", timedOut)),
            headResolver: _ => Candidate, capturedHeadIsAncestor: (_, _, _) => true);
        var reviewer = new AgentDefinition(AgentId.New(), "Review", AgentRole.Reviewer,
            new("provider", "reviewer-model", ModelCapability.Text, SubscriptionMode.ApiKey), Subscription: new("codex-cli"));
        var inquiry = new AttributedFailureDiagnosisInquiry(dispatcher, [reviewer], fixture.Root, fixture.Root, fixture.Root);
        var outcome = await inquiry.RunAsync(fixture.Goal, Route(fixture.Goal), TimeSpan.FromSeconds(120), CancellationToken.None);
        Assert.Equal(status, outcome.Status);
        Assert.False(outcome.CauseFound);
    }

    [Fact]
    public async Task MissingReviewerDoesNotUseDeveloperModelOrInvokeProcess()
    {
        using var fixture = new SoloCorrectionTestFixture();
        var calls = 0;
        var dispatcher = new InquiryDispatcher((_, _) => { calls++; throw new Exception("must not start"); }, headResolver: _ => Candidate);
        var inquiry = new AttributedFailureDiagnosisInquiry(dispatcher, [], fixture.Root, fixture.Root, fixture.Root);
        var outcome = await inquiry.RunAsync(fixture.Goal, Route(fixture.Goal), TimeSpan.FromSeconds(120), CancellationToken.None);
        Assert.Equal("failed", outcome.Status);
        Assert.Contains("Reviewer", outcome.Text);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void LatestReviewerDispatchSuppliesAnsweringAgent()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("reviewed candidate", [new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
        kernel.ActivateGoal(goal.Id, ConductorDriverTests.DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new("claude-cli", "review", Path.GetTempPath(), DateTimeOffset.UnixEpoch,
            ModelName: "recorded-reviewer", ReasoningEffort: "high", WorkerProviderKind: ProviderKind.AnthropicClaudeCli));
        var inquiry = new AttributedFailureDiagnosisInquiry(new InquiryDispatcher(), [], "unused", "unused", "unused");
        var selected = inquiry.ResolveAgent(goal);
        Assert.NotNull(selected);
        Assert.Equal(ProviderKind.AnthropicClaudeCli, selected.ProviderKind);
        Assert.Equal("recorded-reviewer", selected.ModelName);
        Assert.Equal("high", selected.ReasoningEffort);
    }

    [Fact]
    public void MissingReproductionDetailIsExplicitAndLastStatusLineWins()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var route = Route(goal, detail: null);
        Assert.Contains("Reproduction detail:" + Environment.NewLine + "absent", AttributedFailureDiagnosisPrompt.Build(goal, route.Failure!));
        var outcome = AttributedFailureDiagnosisPrompt.Parse(route,
            "DIAGNOSIS_STATUS: cause-found\nprompt echo\nDIAGNOSIS_STATUS: no-cause\nNo support for a cause");
        Assert.Equal("no-cause", outcome.Status);
    }
}
