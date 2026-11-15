using Mcg.SemanticImpactSpike;
using Xunit;

public sealed class SemanticImpactSpikeKnownAnswerTests
{
    [Fact]
    public async Task SourceGroundedCorpusMatchesEveryScoredExpectation()
    {
        var root = SpikeTestContext.Root;
        var data = QueryData.Read(Path.Combine(root, "tools/Mcg.SemanticImpactSpike/data/known-answer-queries.json"));
        Assert.SkipWhen(!await RepositoryBaseline.MatchesAsync(root, data), "Baseline src/tests trees drifted; corpus must be re-grounded.");
        using var loader = new SolutionWorkspaceLoader(SpikeTestContext.Properties());
        await loader.OpenAsync(Path.Combine(root, "Mcg.AgentOrchestrator.sln"));
        Assert.False(loader.Failed, string.Join("\n", loader.Diagnostics));
        var index = new ReverseReferenceIndex();
        index.Build(loader, root);
        var results = SpikeChecks.KnownAnswers(index, data);
        Assert.Equal(12, results.Length);
        Assert.All(results.Where(r => r.Score != "unscored"), r => Assert.True(r.Score == "correct",
            r.Id + ": missing=" + string.Join(",", r.Missing) + "; extra=" + string.Join(",", r.Extra) + "; error=" + r.Error));
    }
}
