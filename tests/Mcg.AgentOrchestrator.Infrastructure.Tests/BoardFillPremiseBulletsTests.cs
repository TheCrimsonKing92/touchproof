using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: the classifier uses only the supplied text.
public sealed class BoardFillPremiseBulletsTests
{
    [Theory]
    [InlineData("operator-evidence/board-fill/round-53.json.")]
    [InlineData(@"C:\operator-evidence\round-53.json.")]
    [InlineData("backlog note 1048c065E0764BA5.")]
    [InlineData("backlog note A.")]
    public void BulletCount_RuntimeObservationWithEvidencePathOrNoteId_CountsOnlySourceClaims(string citation)
    {
        var premise = WithSourceClaims($"runtime-observation: Results are recorded in {citation}");

        Assert.Equal(2, BoardFillVerifierContract.BulletCount(premise));
    }

    [Theory]
    [InlineData("runtime-observation: Rounds failed often.")]
    [InlineData("Observed in operator-evidence/round.json.")]
    [InlineData("Runtime-Observation: Observed in operator-evidence/round.json.")]
    [InlineData("**runtime-observation:** Observed in operator-evidence/round.json.")]
    [InlineData("runtime-observation: Recorded in note xyz.")]
    [InlineData("runtime-observation: Recorded in note abc-123.")]
    [InlineData("runtime-observation: Recorded in round.json.")]
    [InlineData("runtime-observation: Recorded in operator-evidence/round.")]
    [InlineData("runtime-observation: Recorded in operator-evidence.json/round.")]
    [InlineData("runtime-observation: Rounds failed often.\n\nProse cites note ACE.")]
    public void BulletCount_IncompleteRuntimeObservationOrUnprefixedEvidencePath_StillCounts(string bullet)
    {
        var premise = WithSourceClaims(bullet + "\n- runtime-observation: Figures are in note B7.");

        Assert.Equal(3, BoardFillVerifierContract.BulletCount(premise));
    }

    private static string WithSourceClaims(string bullet) => $"""
        ## Measured premise
        - The first source claim cites `src/First.cs:3`.
        * {bullet}
        - The second source claim cites `src/Second.cs:8`.
        """;
}
