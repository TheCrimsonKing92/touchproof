using Mcg.AgentOrchestrator.Core;

public sealed class GoalPromotionFactProvenanceTests
{
    [Theory]
    [InlineData("reconstructed:ancestry")]
    [InlineData("reconstructed:merge-evidence")]
    [InlineData(GoalPromotionFact.SoloProvenance)]
    [InlineData(GoalPromotionFact.CohortProvenance)]
    [InlineData(GoalPromotionFact.MergeTrainProvenance)]
    [InlineData(GoalPromotionFact.CliFastForwardProvenance)]
    [InlineData(GoalPromotionFact.PullRequestMergedProvenance)]
    public void RecognizedProvenance_Constructs(string provenance) =>
        Assert.Equal(provenance, Fact(provenance).Provenance);

    [Theory]
    [InlineData("reconstructed:")]
    [InlineData("arbitrary")]
    [InlineData("reconstructed:Ancestry")]
    [InlineData("reconstructed:merge evidence")]
    [InlineData("reconstructed:-ancestry")]
    [InlineData("reconstructed:ancestry-")]
    [InlineData("reconstructed:merge--evidence")]
    [InlineData("reconstructed:merge_evidence")]
    public void UnrecognizedProvenance_Throws(string provenance) =>
        Assert.Throws<ArgumentException>(() => Fact(provenance));

    private static GoalPromotionFact Fact(string provenance) => new("goal", new string('b', 40),
        new string('a', 40), new string('c', 40), new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), provenance);
}
