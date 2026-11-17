using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every Resolve case owns a temporary filesystem root through the support fixture.
public sealed class PlannerOutputContractTestsHistoricalCitation : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("rejected")]
    [Xunit.InlineData("superseded")]
    [Xunit.InlineData("replaced")]
    [Xunit.InlineData("previously")]
    [Xunit.InlineData("formerly")]
    [Xunit.InlineData("earlier draft")]
    [Xunit.InlineData("REJECTED")]
    [Xunit.InlineData("Superseded")]
    [Xunit.InlineData("RePlAcEd")]
    [Xunit.InlineData("EARLIER DRAFT")]
    [Xunit.InlineData("earlier\tdraft")]
    [Xunit.InlineData("earlier   draft")]
    [Xunit.InlineData("earlier\u00a0draft")]
    public void ExactTargetWithHistoricalWildcard_IsAcceptedAndPreserved(string cue)
    {
        var workingDirectory = CreateTempDirectory();
        var target = Path.Combine(workingDirectory, "src", "Feature", "Target.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "// Existing implementation seam.");
        const string targetLine = "- Inspect `src/Feature/Target.cs` for the implementation seam.";
        const string citation = "src/*/Target.cs";
        var historicalLine = $"- {cue}: `{citation}`.";
        var plan = PlanWithTargetLine(targetLine + "\n" + historicalLine);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Equal(plan, result.Plan);
        Xunit.Assert.Contains(historicalLine, result.Plan, StringComparison.Ordinal);

        var uncuedPlan = PlanWithTargetLine(targetLine + $"\n- `{citation}`.");
        var uncuedResult = PlannerOutputContract.Resolve(uncuedPlan, string.Empty, workingDirectory);

        AssertMissingFileRejection(uncuedResult, citation);
    }

    [Xunit.Fact]
    public void ConcreteMissingTargetWithHistoricalCue_IsRejected()
    {
        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine("- Previously rejected `src/Missing.json`."),
            string.Empty, CreateTempDirectory());

        AssertMissingFileRejection(result, "src/Missing.json");
    }

    [Xunit.Fact]
    public void HistoricalWildcardMarkedNewFile_IsRejected()
    {
        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine("- Rejected `src/*.json` (new file)."),
            string.Empty, CreateTempDirectory());

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("path contains a wildcard", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending citation: 'src/*.json'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void UncuedWildcardTarget_IsRejected()
    {
        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine("- Inspect `*.json` for the implementation seam."),
            string.Empty, CreateTempDirectory());

        AssertMissingFileRejection(result, "*.json");
    }

    [Xunit.Theory]
    [Xunit.InlineData("Rejected on a neighboring line.\n- Inspect `*.json`.")]
    [Xunit.InlineData("Rejected on a neighboring line.\r\n- Inspect `*.json`.")]
    [Xunit.InlineData("Rejected on a neighboring line.\r- Inspect `*.json`.")]
    [Xunit.InlineData("- Inspect `*.json`.\nRejected on a neighboring line.")]
    [Xunit.InlineData("- Inspect `*.json`.\r\nRejected on a neighboring line.")]
    [Xunit.InlineData("- Inspect `*.json`.\rRejected on a neighboring line.")]
    public void HistoricalCueOnNeighboringLine_KeepsMissingFileRejection(string line)
    {
        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine(line), string.Empty, CreateTempDirectory());

        AssertMissingFileRejection(result, "*.json");
    }

    [Xunit.Theory]
    [Xunit.InlineData("- Inspect unrejected `*.json`.")]
    [Xunit.InlineData("- Inspect supersededness `*.json`.")]
    [Xunit.InlineData("- Inspect replacedment `*.json`.")]
    [Xunit.InlineData("- Inspect previously_x `*.json`.")]
    [Xunit.InlineData("- Inspect formerly2 `*.json`.")]
    [Xunit.InlineData("- Inspect rejected_x `*.json`.")]
    [Xunit.InlineData("- Inspect rejected2 `*.json`.")]
    [Xunit.InlineData("- Inspect érejected `*.json`.")]
    [Xunit.InlineData("- Inspect earlier drafts `*.json`.")]
    [Xunit.InlineData("- Inspect earlier-draft `*.json`.")]
    [Xunit.InlineData("- Inspect earlier `*.json`.")]
    [Xunit.InlineData("- Inspect draft `*.json`.")]
    [Xunit.InlineData("- Inspect earlier\vdraft `*.json`.")]
    [Xunit.InlineData("- Inspect earlier\fdraft `*.json`.")]
    [Xunit.InlineData("- Inspect rej`code`ected `*.json`.")]
    [Xunit.InlineData("- Inspect `rejected` and `*.json`.")]
    [Xunit.InlineData("- Inspect `*.json` using `earlier draft`.")]
    public void MissingWholeWordProseCue_KeepsMissingFileRejection(string line)
    {
        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine(line), string.Empty, CreateTempDirectory());

        AssertMissingFileRejection(result, "*.json");
    }

    private static void AssertMissingFileRejection(PlannerOutputContractResult result, string citation)
    {
        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("does not exist and is not marked as a new file", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", result.Diagnostic, StringComparison.Ordinal);
    }

    private static string PlanWithTargetLine(string line)
    {
        var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var bodyStart = plan.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return plan[..bodyStart] + "\n\nInspect `PlannerOutputContract.Resolve` for target citation validation.\n" + line + "\n" + plan[bodyEnd..];
    }
}
