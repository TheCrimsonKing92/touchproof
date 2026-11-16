using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every Resolve case owns a temporary filesystem root through the support fixture.
public sealed class PlannerOutputContractTestsSearchPatternMention : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void SearchPatternLine_IsAccepted()
    {
        const string line = "List Directory.GetDirectories(root) and, for each goal, file names with fixed patterns (`*.json` and `*.trx`). Never put the id inside a search pattern.";
        var plan = PlanWithTargetLine(line);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, CreateTempDirectory());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains(line, result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("- Inspect pattern `*.json`.")]
    [Xunit.InlineData("- Inspect PATTERNS `*.json`.")]
    [Xunit.InlineData("- Inspect glob `Missing?.json`.")]
    [Xunit.InlineData("- Inspect GlObS `src/*.json`.")]
    [Xunit.InlineData("- Inspect `*.json` (pattern).")]
    [Xunit.InlineData("- Inspect `pattern` and `*.json` with globs.")]
    public void WholeWordCueOutsideCode_AllowsWildcard(string line)
    {
        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine(line), string.Empty, CreateTempDirectory());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains(line, result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void UncuedWildcardTarget_IsRejected()
    {
        const string line = "- Inspect `*.json` for the concrete implementation seam.";

        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine(line), string.Empty, CreateTempDirectory());

        AssertMissingFileRejection(result, "*.json");
        Xunit.Assert.Contains(line, result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("- Inspect `*.json` using `patterns`.")]
    [Xunit.InlineData("- Inspect `glob` and `*.json`.")]
    [Xunit.InlineData("- Inspect `pattern*.json` for the concrete seam.")]
    [Xunit.InlineData("- Inspect patterned `*.json`.")]
    [Xunit.InlineData("- Inspect globbing `*.json`.")]
    [Xunit.InlineData("- Inspect pattern_name `*.json`.")]
    [Xunit.InlineData("- Inspect pattern2 `*.json`.")]
    [Xunit.InlineData("- Inspect épattern `*.json`.")]
    [Xunit.InlineData("- Inspect pat`code`tern `*.json`.")]
    [Xunit.InlineData("Patterns on a neighboring line.\n- Inspect `*.json`.")]
    [Xunit.InlineData("Patterns on a neighboring line.\r\n- Inspect `*.json`.")]
    [Xunit.InlineData("Patterns on a neighboring line.\r- Inspect `*.json`.")]
    [Xunit.InlineData("- Inspect `*.json`.\nPatterns on a neighboring line.")]
    [Xunit.InlineData("- Inspect `*.json`.\r\nPatterns on a neighboring line.")]
    [Xunit.InlineData("- Inspect `*.json`.\rPatterns on a neighboring line.")]
    public void MissingSameLineProseCue_KeepsMissingFileRejection(string line)
    {
        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine(line), string.Empty, CreateTempDirectory());

        AssertMissingFileRejection(result, line.Contains("pattern*.json", StringComparison.Ordinal)
            ? "pattern*.json"
            : "*.json");
    }

    [Xunit.Fact]
    public void MarkedWildcardWithPatternCue_IsRejected()
    {
        const string line = "- Inspect `src/*.json` (new file) for the pattern implementation.";

        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine(line), string.Empty, CreateTempDirectory());

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("path contains a wildcard", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending citation: 'src/*.json'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ConcreteMissingFileWithPatternCue_IsRejected()
    {
        var result = PlannerOutputContract.Resolve(
            PlanWithTargetLine("- Inspect `src/Missing.json` for pattern handling."),
            string.Empty, CreateTempDirectory());

        AssertMissingFileRejection(result, "src/Missing.json");
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
