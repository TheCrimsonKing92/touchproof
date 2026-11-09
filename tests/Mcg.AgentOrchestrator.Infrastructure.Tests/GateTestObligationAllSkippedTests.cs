using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static GateTestObligationCheckOutcomeTests;

// Parallel-safe: unique temporary receipts, in-memory goals and AsyncLocal console capture.
public sealed class GateTestObligationAllSkippedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkippedSelectors_ReleaseLandingWithProvenance(bool everySelector)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "mixed.trx",
            Row("SkippedTests", "First", "NotExecuted"), Row("SkippedTests", "Second", "NotExecuted"),
            Row("OtherTests", "Check", everySelector ? "NotExecuted" : "Passed"));
        var (id, _, _) = fixture.WriteAttempt([], additionalPaths: [path]);
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["SkippedTests", "OtherTests"]);
        var goal = kernel.GetGoal(fixture.Goal.Id);

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(
            goal, fixture.OrchestratorDirectory, fixture.CandidateSha));

        Assert.Equal(GateTestObligationEvaluator.Outcome.AllSkipped, result.Outcome);
        Assert.Equal(id, result.SourceId);
        var skipped = everySelector ? new[] { "SkippedTests", "OtherTests" } : ["SkippedTests"];
        Assert.Equal(skipped, result.AllSkippedSelectors);
        var prefix = $"GATE_TEST_OBLIGATION_ALL_SKIPPED id={Assert.Single(goal.GateTestObligations).Id[..8]}";
        var expected = string.Join("; ", skipped.Select(selector =>
            $"{prefix} selector={selector} reason=all-skipped source={id}"));
        if (everySelector) expected += $"; {prefix} scope=every-selector selectors=2 reason=all-skipped source={id}";
        Assert.Equal(expected, result.AllSkippedProvenance);
        var evidence = GroupedGateReceiptTrxEvidence.Evaluate(skipped, [path], fixture.Root);
        Assert.All(evidence, item =>
        {
            Assert.Equal(0, item.Passed);
            Assert.False(item.Satisfied);
        });

        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root, fixture.Root) with
        { OrchestratorDirectory = fixture.OrchestratorDirectory, IntegrationBranch = "main" };
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var landing = LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, fixture.CandidateSha);
            Assert.Empty(landing.ObligationLines);
            Assert.Null(landing.HoldDescription);
        });
        Assert.Contains(expected + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData("notexecuted")]
    [InlineData("Skipped")]
    public void AnyNonSkippedFailure_KeepsSelectorFailed(string outcome)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        fixture.WriteAttempt([("AlphaTests.Skipped", "NotExecuted"), ("AlphaTests.Fault", outcome)]);

        var result = Evaluate(fixture, "AlphaTests");

        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        Assert.Equal(new[] { "AlphaTests" }, result.Selectors);
        AssertNoAllSkipped(result);
    }

    [Fact]
    public void UnmatchedSelector_RemainsSelectorNotFound()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        fixture.WriteAttempt([("OtherTests.Row", "NotExecuted")]);

        var result = Evaluate(fixture, "MissingTests");

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { "MissingTests" }, result.Selectors);
        AssertNoAllSkipped(result);
    }

    [Fact]
    public void PassedAndSkippedRows_RemainSatisfied()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        fixture.WriteAttempt([("AlphaTests.Skipped", "NotExecuted"), ("AlphaTests.Pass", "Passed")]);

        var result = Evaluate(fixture, "AlphaTests");

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        AssertNoAllSkipped(result);
    }

    [Fact]
    public void AmbiguousSkippedClasses_KeepBlockingGrade()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "ambiguous.trx",
            Row("First.AlphaTests", "Check", "NotExecuted"), Row("Second.AlphaTests", "Check", "NotExecuted"));
        fixture.WriteAttempt([], additionalPaths: [path]);
        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate(["AlphaTests"], [path], fixture.Root));
        Assert.Equal(new[] { "First.AlphaTests", "Second.AlphaTests" }, evidence.AmbiguousClassNames);

        var result = Evaluate(fixture, "AlphaTests");

        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        AssertNoAllSkipped(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingOrFailedCommandCheck_StillHoldsSkippedObligation(bool failed)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt([("AlphaTests.Check", "NotExecuted")]);
        WriteManifest(fixture, TypeCheck());
        if (failed) WriteResult(fixture, id, failedChecks: [CheckName]);

        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["AlphaTests", Selector]);
        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(kernel.GetGoal(fixture.Goal.Id),
            fixture.OrchestratorDirectory, fixture.CandidateSha));

        Assert.Equal(failed ? GateTestObligationEvaluator.Outcome.Failed : GateTestObligationEvaluator.Outcome.Pending,
            result.Outcome);
        if (failed) AssertNoAllSkipped(result);
        else
        {
            Assert.Equal(new[] { Selector }, result.Selectors);
            Assert.Equal(new[] { "AlphaTests" }, result.AllSkippedSelectors);
            Assert.Contains("selector=AlphaTests reason=all-skipped", result.AllSkippedProvenance);
            Assert.DoesNotContain("scope=every-selector", result.AllSkippedProvenance);
        }
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root, fixture.Root) with
        { OrchestratorDirectory = fixture.OrchestratorDirectory, IntegrationBranch = "main" };
        var landing = LandingExecutor.EvaluatePendingHumanInput(kernel, kernel.GetGoal(fixture.Goal.Id), workspace,
            fixture.CandidateSha);
        Assert.Contains(failed ? " failed:" : " pending:", Assert.Single(landing.ObligationLines));
        Assert.NotNull(landing.HoldDescription);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclaredOrUnreadableEvidence_PreservesOriginalGrade(bool declared)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, path, _) = fixture.WriteAttempt([("AlphaTests.Check", "NotExecuted")]);
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["AlphaTests"]);
        var obligation = Assert.Single(kernel.GetGoal(fixture.Goal.Id).GateTestObligations);
        var evidence = GroupedGateReceiptTrxEvidence.Evaluate(obligation.Selectors, [path], fixture.Root);
        Assert.Equal(1, Assert.Single(evidence).Failed);
        // Model an unreadable second read without a timing-dependent file race.
        if (!declared) File.WriteAllText(path, "invalid TRX");

        var result = GateTestObligationAllSkippedGrade.Grade(obligation,
            new(id, DateTimeOffset.UnixEpoch, [path], null), evidence, fixture.Root, declared);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        AssertNoAllSkipped(result);
    }

    [Fact]
    public void OlderSatisfiedSource_RetainsExistingPrecedence()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (passedId, _, _) = fixture.WriteAttempt([("AlphaTests.Check", "Passed")],
            completedAt: DateTimeOffset.UnixEpoch);
        fixture.WriteAttempt([("AlphaTests.Check", "NotExecuted")],
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        var result = Evaluate(fixture, "AlphaTests");

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(passedId, result.SourceId);
        AssertNoAllSkipped(result);
    }

    private static GroupedGateTrxSamples.Row Row(string className, string method, string outcome) =>
        new($"{className}.{method}", className, method, $"{className}.{method}", outcome);

    private static void AssertNoAllSkipped(GateTestObligationEvaluator.Result result)
    {
        Assert.Empty(result.AllSkippedSelectors ?? []);
        Assert.Equal("", result.AllSkippedProvenance);
    }
}
