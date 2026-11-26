using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: the classifier uses only the supplied text.
public sealed class BoardFillPremiseBulletsTests
{
    [Theory]
    [InlineData("operator-evidence/board-fill/round-53.json.")]
    [InlineData(@"C:\operator-evidence\round-53.json.")]
    [InlineData("backlog note 1048c065E0764BA5.")]
    [InlineData("backlog note A.")]
    [InlineData("*note B7*.")]
    [InlineData("**note B7**.")]
    [InlineData("“note B7”.")]
    [InlineData("‘note B7’.")]
    [InlineData("the following paragraph.\n\n  See note B7.")]
    [InlineData("the following paragraph.\n\n  ---\n\n  See note B7.")]
    [InlineData("the following line.\n###Evidence cites note B7.")]
    [InlineData("the following line.\n```inline``` cites note B7.")]
    [InlineData("the following line.\n<div-like>See note B7. </div-like>")]
    public void BulletCount_RuntimeObservationWithEvidencePathOrNoteId_CountsOnlySourceClaims(string citation)
    {
        var premise = WithSourceClaims($"runtime-observation: Results are recorded in {citation}");

        Assert.Equal(2, BoardFillVerifierContract.BulletCount(premise));
    }

    [Theory]
    [InlineData("  ", "under note B7.")]
    [InlineData("\t", "in operator-evidence/round.json.")]
    public void SourceClaims_MixedIndentationInOpeningParagraph_ExcludeObservation(string indent, string citation)
    {
        var premise = WithSourceClaims($"runtime-observation: Figures are recorded\n{indent}for this round\n{citation}");
        var prompt = BoardFillVerifierContract.Prompt(premise, BoardFillAssessmentTestFixture.Head);
        using var document = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);

        Assert.Equal(2, BoardFillVerifierContract.BulletCount(premise));
        Assert.Equal("""
            ## Measured premise
            - The first source claim cites `src/First.cs:3`.
            - The second source claim cites `src/Second.cs:8`.
            """.ReplaceLineEndings("\n"), document.RootElement.GetProperty("measuredPremise").GetString());
    }

    [Theory]
    [InlineData("runtime-observation: Rounds failed often.")]
    [InlineData("Observed in operator-evidence/round.json.")]
    [InlineData("Runtime-Observation: Observed in operator-evidence/round.json.")]
    [InlineData("**runtime-observation:** Observed in operator-evidence/round.json.")]
    [InlineData("runtime-observation: Recorded in note xyz.")]
    [InlineData("runtime-observation: Recorded in note abc-123.")]
    [InlineData("runtime-observation: Recorded in *note abc-123*.")]
    [InlineData("runtime-observation: Recorded in “note abc-123”.")]
    [InlineData("runtime-observation: Recorded in round.json.")]
    [InlineData("runtime-observation: Recorded in operator-evidence/round.")]
    [InlineData("runtime-observation: Recorded in operator-evidence.json/round.")]
    [InlineData("runtime-observation: Rounds failed often.\n\nProse cites note ACE.")]
    public void BulletCount_IncompleteRuntimeObservationOrUnprefixedEvidencePath_StillCounts(string bullet)
    {
        var premise = WithSourceClaims(bullet + "\n- runtime-observation: Figures are in note B7.");

        Assert.Equal(3, BoardFillVerifierContract.BulletCount(premise));
    }

    [Theory]
    [InlineData("+")]
    [InlineData("1.")]
    [InlineData("1)")]
    public void SourceClaims_AdjacentMarkdownListItems_KeepTheirOwnCitations(string marker)
    {
        var premise = WithSourceClaims($"""
            runtime-observation: Rounds failed often.
            {marker} Separate item cites operator-evidence/round.json.
            - runtime-observation: Figures are in note B7.
            {marker} Separate item follows a cited observation.
            """);

        Assert.Equal(3, BoardFillVerifierContract.BulletCount(premise));
        var payload = BoardFillPremiseBullets.SourceClaimPremise(premise);
        Assert.Contains("runtime-observation: Rounds failed often.", payload);
        Assert.Contains($"{marker} Separate item cites operator-evidence/round.json.", payload);
        Assert.DoesNotContain("runtime-observation: Figures are in note B7.", payload);
        Assert.Contains($"{marker} Separate item follows a cited observation.", payload);
    }

    [Theory]
    [InlineData("> ", "")]
    [InlineData("\n ", "")]
    [InlineData("---\n", "")]
    [InlineData("***\n", "")]
    [InlineData("___\n", "")]
    [InlineData(" _ _ _ \n", "")]
    [InlineData("### ", "")]
    [InlineData("#\n", "")]
    [InlineData("```text\n", "\n```")]
    [InlineData(" ~~~text\n", "\n ~~~")]
    [InlineData("<!-- ", " -->")]
    [InlineData("<?evidence ", " ?>")]
    [InlineData("<!Evidence ", " >")]
    [InlineData("<![CDATA[", " ]]>")]
    [InlineData("<PRE>\n", "\n</PRE>")]
    [InlineData("<DIV>\n", "\n</DIV>\n")]
    public void SourceClaims_AdjacentMarkdownBlocks_KeepTheirOwnCitations(string blockStart, string blockEnd)
    {
        var separateBlock = $"{blockStart}Separate block cites note B7.{blockEnd}";
        var followingBlock = $"{blockStart}Separate block follows a cited observation.{blockEnd}";
        var premise = WithSourceClaims($"""
            runtime-observation: Rounds failed often.
            {separateBlock}
            - runtime-observation: Figures are in note B7.
            {followingBlock}
            """);

        Assert.Equal(3, BoardFillVerifierContract.BulletCount(premise));
        var payload = BoardFillPremiseBullets.SourceClaimPremise(premise);
        Assert.Contains($"runtime-observation: Rounds failed often.\n{separateBlock}", payload);
        Assert.DoesNotContain("runtime-observation: Figures are in note B7.", payload);
        Assert.EndsWith($"{followingBlock}\n- The second source claim cites `src/Second.cs:8`.", payload);
    }

    [Theory]
    [InlineData("### Evidence cites note B7.")]
    [InlineData("```text\nSee operator-evidence/round.json.\n```")]
    [InlineData("~~~text\nSee note B7.\n~~~")]
    [InlineData("<!-- Evidence cites note B7. -->")]
    [InlineData("> Evidence cites note B7.")]
    [InlineData("+ Evidence cites operator-evidence/round.json.")]
    [InlineData("1. Evidence cites note B7.")]
    public void SourceClaims_NestedMarkdownBlocks_KeepCitationsWithObservation(string block)
    {
        var premise = WithSourceClaims($"runtime-observation: Rounds failed often.\n  {block.Replace("\n", "\n  ")}");

        Assert.Equal(2, BoardFillVerifierContract.BulletCount(premise));
        Assert.Equal("""
            ## Measured premise
            - The first source claim cites `src/First.cs:3`.
            - The second source claim cites `src/Second.cs:8`.
            """.ReplaceLineEndings("\n"), BoardFillPremiseBullets.SourceClaimPremise(premise));
    }

    [Theory]
    [InlineData("  ### Detail", "  ### Detail cites note B7.")]
    [InlineData("  ```text\n  ```", "  ```text\n  See note B7.\n  ```")]
    [InlineData("  ```text", "  ```text\n  See note B7.")]
    [InlineData("  ~~~text\n  ~~~", "  ~~~text\n  See note B7.\n  ~~~")]
    [InlineData("  ---", "  ---\n  See note B7.")]
    [InlineData("  ===", "  ===\n  See note B7.")]
    [InlineData("  <div>", "  <div>\n  See note B7.")]
    public void SourceClaims_OutdentedProseAfterNestedBlock_StaysOutsideItem(string block, string citedBlock)
    {
        var uncitedItem = $"runtime-observation: Rounds failed often.\n{block}\nProse cites note B7.";
        const string followingProse = "Prose follows the cited observation.";
        var premise = WithSourceClaims($"""
            {uncitedItem}
            - runtime-observation: Figures are recorded below.
            {citedBlock}
            {followingProse}
            """);

        Assert.Equal(3, BoardFillVerifierContract.BulletCount(premise));
        Assert.Equal(WithSourceClaims($"{uncitedItem}\n{followingProse}").ReplaceLineEndings("\n"),
            BoardFillPremiseBullets.SourceClaimPremise(premise));
    }

    private static string WithSourceClaims(string bullet) => $"""
        ## Measured premise
        - The first source claim cites `src/First.cs:3`.
        * {bullet}
        - The second source claim cites `src/Second.cs:8`.
        """;
}
