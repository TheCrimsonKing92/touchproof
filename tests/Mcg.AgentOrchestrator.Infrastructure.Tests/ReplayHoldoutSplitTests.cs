using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: immutable cases and local callbacks only.
public sealed class ReplayHoldoutSplitTests
{
    [Fact]
    public void Learn_UnorderedCasesAndTies_ReceivesOnlyTimeOrderedTraining()
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        ReplayHarnessCase Case(string id, int hours) => ReplayHarnessCase.CreateReplayable(id, at.AddHours(hours), "recorded");
        var input = new[] { Case("future", 2), Case("tie-b", 0), Case("earlier", -1), Case("tie-a", 0), Case("later", 1) };
        var split = ReplayHoldoutSplit.Create(input, at);

        Assert.Equal(new[] { "earlier", "tie-a", "tie-b" }, split.Training.Select(item => item.CaseId));
        Assert.Equal(new[] { "later", "future" }, split.Evaluation.Select(item => item.CaseId));
        Assert.All(split.Evaluation, evaluation =>
            Assert.All(split.Training, training => Assert.True(evaluation.RecordedAt > training.RecordedAt)));
        var calls = 0;
        var candidate = split.Learn(training =>
        {
            calls++;
            Assert.Equal(new[] { "earlier", "tie-a", "tie-b" }, training.Select(item => item.CaseId));
            Assert.DoesNotContain(training, item => split.Evaluation.Contains(item));
            return string.Join(",", training.Select(item => item.CaseId));
        });
        Assert.Equal(1, calls);
        Assert.Equal("earlier,tie-a,tie-b", candidate);
    }

    [Fact]
    public void Create_EmptyOrOneSidedCases_PreservesPartition()
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var item = ReplayHarnessCase.CreateReplayable("case", at, "recorded");
        var empty = ReplayHoldoutSplit.Create([], at);
        Assert.Empty(empty.Training);
        Assert.Empty(empty.Evaluation);
        Assert.Equal(0, empty.Learn(training => training.Count));
        var allTraining = ReplayHoldoutSplit.Create([item], at);
        Assert.Equal(new[] { item }, allTraining.Training);
        Assert.Empty(allTraining.Evaluation);
        var allEvaluation = ReplayHoldoutSplit.Create([item], at.AddTicks(-1));
        Assert.Empty(allEvaluation.Training);
        Assert.Equal(new[] { item }, allEvaluation.Evaluation);
    }
}
