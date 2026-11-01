using Mcg.AgentOrchestrator.Infrastructure;
using Token = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;

public sealed class FocusedEvidenceGroupCostModelTests
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    [Fact]
    public void ResolvedPlanRetainsTheHistorySnapshotForPlacement()
    {
        var history = new History(new() { ["Infrastructure.Tests:AdvanceLoopTests"] = 600 });
        Assert.True(FocusedEvidenceRequestResolver.TryBuildFocusedEvidenceChecks(
            "Infrastructure.Tests:AdvanceLoopTests", new(), FocusedEvidenceOverflowSyntheticRoot.Root,
            out var checks, out _, out var rejection, history), rejection.Detail);
        var plan = Assert.IsType<FocusedEvidenceCheckPlan>(checks);
        Assert.Same(history, plan.History);
        Assert.Single(plan);
        Assert.Equal("AdvanceLoopTests", Assert.Single(Assert.Single(plan[0].FocusedEvidenceSelections)).ContainingClass);
    }

    [Fact]
    public void UnknownClassesUseRequestedMedianAndMethodsCountClassOnce()
    {
        Token[] cohort = [Class("A"), Class("B"), Class("C"), Class("D")];
        var model = new FocusedEvidenceGroupCostModel(Project, cohort,
            new History(new() { ["Infrastructure.Tests:A"] = 100, ["Infrastructure.Tests:B"] = 300,
                ["Infrastructure.Tests:Unrelated"] = 9000 }));
        Assert.Equal(200, model.PredictSeconds([Class("C")]));
        Assert.Equal(500, model.PredictSeconds([Class("A"), Class("C"), Class("C"), Class("D")]));
        Assert.Equal(800, FocusedEvidenceGroupCostModel.Sum(cohort.Select(token => model.PredictSeconds([token]))));
    }

    [Fact]
    public void InvalidHistoryIsUnavailableAndZeroRemainsKnown()
    {
        Token[] cohort = [Class("A"), Class("B"), Class("C")];
        var history = new History(new() { ["Infrastructure.Tests:A"] = -1,
            ["Infrastructure.Tests:B"] = double.NaN, ["Infrastructure.Tests:C"] = double.PositiveInfinity });
        Assert.Null(new FocusedEvidenceGroupCostModel(Project, cohort, history).PredictSeconds(cohort));
        Assert.Null(new FocusedEvidenceGroupCostModel(Project, cohort).PredictSeconds(cohort));
        var zero = new FocusedEvidenceGroupCostModel(Project, cohort,
            new History(new() { ["Infrastructure.Tests:A"] = 0 }));
        Assert.Equal(0, zero.PredictSeconds(cohort));
        Assert.Null(FocusedEvidenceGroupCostModel.Sum([1, null]));
        Assert.Equal("unavailable", FocusedEvidenceGroupCostModel.Format(null));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(300, false)]
    [InlineData(300.001, true)]
    [InlineData(double.PositiveInfinity, false)]
    public void HeavyThresholdIsStrictAndFinite(double seconds, bool heavy) =>
        Assert.Equal(heavy, FocusedEvidenceGroupCostModel.IsHeavy(seconds));

    private static Token Class(string name) => new(name, "FullyQualifiedName~" + name,
        GoalAcceptanceVerifier.FocusedEvidenceTokenKind.Class, name, name);
    private sealed class History(Dictionary<string, double> seconds) : IClassDurationHistory
    {
        public double? PredictSeconds(string key) => seconds.TryGetValue(key, out var value) ? value : null;
    }
}
