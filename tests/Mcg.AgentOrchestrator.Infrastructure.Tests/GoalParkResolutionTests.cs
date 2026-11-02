using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class GoalParkResolutionTests
{
    [Theory]
    [InlineData("operator action", "Goal parked: operator action")]
    [InlineData("", "Goal parked: ")]
    [InlineData("  keep spacing.  ", "Goal parked:   keep spacing.  ")]
    public void Build_SuppliedReason_PreservesText(string reason, string expected)
    {
        var resolution = GoalParkResolution.Build(reason);

        Assert.Equal(expected, resolution);
        Assert.True(GoalParkResolution.IsParkResolution(resolution));
    }

    [Fact]
    public void Fallback_UnavailableReason_IsRecognized()
    {
        Assert.Equal("Goal parked: reason unavailable", GoalParkResolution.Fallback);
        Assert.True(GoalParkResolution.IsParkResolution(GoalParkResolution.Fallback));
    }

    [Theory]
    [InlineData("Goal parked.", true)]
    [InlineData("GOAL PARKED: operator action", true)]
    [InlineData("Goal parked:", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Resume.", false)]
    [InlineData("yes", false)]
    [InlineData("Goal parked", false)]
    [InlineData("goal parked.", false)]
    [InlineData(" Goal parked.", false)]
    [InlineData(" Goal parked: waiting", false)]
    [InlineData("Goal parked. ", false)]
    [InlineData("Goal parked..", false)]
    [InlineData("The goal parked: later", false)]
    public void IsParkResolution_StoredText_UsesExactRecognition(
        string? text, bool expected)
    {
        Assert.Equal(expected, GoalParkResolution.IsParkResolution(text));
    }

    [Fact]
    public void BuildConfirmation_GoalPrefix_PreservesCliOutput()
    {
        Assert.Equal("Goal parked 12345678.", GoalParkResolution.BuildConfirmation("12345678"));
    }

    [Theory]
    [InlineData("Goal parked.", true, false, true)]
    [InlineData("Goal parked: reason unavailable", true, false, true)]
    [InlineData("Resume.", true, false, false)]
    [InlineData("Goal parked.", false, false, false)]
    [InlineData("Goal parked.", true, true, false)]
    public void Hydrate_StoredAnswer_RecognizesOnlySyntheticCompletions(
        string answer, bool completed, bool dismissed, bool expected)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Recognize stored park completions.");
        var request = kernel.RequestHumanInput(goal.Id, null, "Resume work?");
        var snapshot = kernel.ExportSnapshot();
        var stored = Assert.Single(snapshot.HumanInputRequests) with
        {
            IsCompleted = completed,
            WasDismissed = dismissed,
            Answer = answer,
            AnsweredAt = request.RequestedAt
        };

        var hydrated = AgentOrchestratorKernel.FromSnapshot(
            snapshot with { HumanInputRequests = [stored] }).GetHumanInputRequest(request.Id);

        Assert.Equal(expected, hydrated.IsSyntheticParkedHumanWaitCompletion);
        if (completed && !dismissed)
            Assert.Equal(answer, hydrated.Answer);
    }

    [Fact]
    public void Sweep_NoParkDecision_CompletesWaitWithSyntheticFallback()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Sweep a legacy parked goal.");
        var request = kernel.RequestHumanInput(goal.Id, null, "Resume work?");
        Escalate(kernel, goal);
        kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with
        {
            Status = GoalStatus.Parked
        });
        var parked = kernel.GetGoal(goal.Id);
        Assert.Equal(GoalStatus.Parked, parked.Status);
        Assert.False(request.IsCompleted);
        Assert.DoesNotContain(parked.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            GoalParkResolution.IsParkResolution(evt.Message));
        Assert.True(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(parked));

        var resolved = kernel.SweepParkedGoalHumanWaits();

        Assert.Equal(1, resolved);
        var completed = kernel.GetHumanInputRequest(request.Id);
        Assert.True(completed.IsCompleted);
        Assert.Equal("Goal parked: reason unavailable", completed.Answer);
        Assert.True(completed.IsSyntheticParkedHumanWaitCompletion);
        Assert.False(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(
            kernel.GetGoal(goal.Id)));
    }

    [Theory]
    [InlineData(ProgressKind.GoalPolicyDecision, "Goal parked: reason unavailable")]
    [InlineData(ProgressKind.HumanInputRequested, "Goal parked: reason unavailable")]
    [InlineData(ProgressKind.GoalPolicyDecision, "Goal parked.")]
    [InlineData(ProgressKind.HumanInputRequested, "Goal parked.")]
    public void ClearsEscalation_StoredParkEvent_IsRecognized(ProgressKind kind, string text)
    {
        var evt = new ProgressEvent(GoalId.New(), null, kind, text, DateTimeOffset.UtcNow);

        Assert.True(VerifiedAcceptanceEscalationDecision.ClearsPersistedVerifiedAcceptanceEscalation(evt));
    }

    [Fact]
    public void Escalation_FallbackParkDecision_ClearsEpisode()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Clear a fallback park escalation.");
        Escalate(kernel, goal);
        Assert.True(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));

        kernel.RecordGoalPolicyDecision(goal.Id, GoalParkResolution.Fallback);

        Assert.False(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));
    }

    [Fact]
    public void Source_ParkTextLiteral_HasOneOwner()
    {
        var root = VerifiedRepositoryRoot.Find();
        var sourceRoot = Path.Combine(root, "src");
        var matches = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(sourceRoot, path).Split(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part =>
                    part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals(".scratch", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals(".orchestrator-prototype", StringComparison.OrdinalIgnoreCase)))
            .Where(path => File.ReadAllText(path).Contains("\"Goal parked", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(["src/Mcg.AgentOrchestrator.Core/Domain/GoalParkResolution.cs"], matches);
    }

    private static void Escalate(AgentOrchestratorKernel kernel, Goal goal) =>
        kernel.RecordGoalPolicyDecision(goal.Id, "Interrupted acceptance requires review.",
            new ConductorTickOutcomePayload("Escalated", "Verified",
                nameof(ConductorEscalationKind.BackgroundAcceptanceFailed)));
}
