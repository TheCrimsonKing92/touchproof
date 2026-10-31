using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each replay owns its report, manifest, and attempt paths.
public sealed class DeclaredTestSegmentSelectorReplayTests
{
    [Theory]
    [InlineData("follow_patterns")]
    [InlineData("follow_filters")]
    [InlineData("follow_rotation")]
    [InlineData("follow_stop")]
    [InlineData("filter::tests")]
    [InlineData("args::tests")]
    [InlineData("version")]
    public void Evaluate_UnqualifiedWholeSegments_SatisfiesObligation(string selector)
    {
        using var fixture = new DeclaredTestReplayFixture();
        LoadReplay(fixture);

        var result = fixture.Evaluate(selector);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(DeclaredTestReplayFixture.SourceId, result.SourceId);
    }

    [Fact]
    public void Evaluate_AllRequiredSelectors_SatisfiesObligation()
    {
        using var fixture = new DeclaredTestReplayFixture();
        LoadReplay(fixture);

        var result = fixture.Evaluate("follow_patterns", "filter::tests", "args::tests", "version");

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(DeclaredTestReplayFixture.SourceId, result.SourceId);
    }

    [Theory]
    [InlineData("ilter::tests")]
    [InlineData("filter::test")]
    [InlineData("follow_pattern")]
    public void Evaluate_PartialSegments_ReportsSelectorNotFound(string selector)
    {
        using var fixture = new DeclaredTestReplayFixture();
        LoadReplay(fixture);

        var result = fixture.Evaluate(selector);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { selector }, result.Selectors);
        Assert.Equal(DeclaredTestReplayFixture.SourceId, result.SourceId);
    }

    [Fact]
    public void Evaluate_WholeSegmentWithFailedRow_FailsObligation()
    {
        using var fixture = new DeclaredTestReplayFixture();
        LoadReplay(fixture);
        FailArgsRow(fixture);

        var result = fixture.Evaluate("args::tests");

        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        Assert.Equal(new[] { "args::tests" }, result.Selectors);
        Assert.Equal(DeclaredTestReplayFixture.SourceId, result.SourceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_TestsAtDifferentPositions_FailsIfAnyMatchedRowFails(bool fail)
    {
        using var fixture = new DeclaredTestReplayFixture();
        LoadReplay(fixture);
        if (fail) FailArgsRow(fixture);

        var result = fixture.Evaluate("tests");

        Assert.Equal(fail ? GateTestObligationEvaluator.Outcome.Failed
            : GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(DeclaredTestReplayFixture.SourceId, result.SourceId);
    }

    private static void LoadReplay(DeclaredTestReplayFixture fixture, [CallerFilePath] string source = "")
    {
        var root = VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verified)
            ? Path.Combine(verified, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests")
            : Path.GetDirectoryName(source)!;
        File.Copy(Path.Combine(root, "Fixtures", "GateCtrf", "conduct-tail-segment-selectors.ctrf.json"),
            fixture.Path, overwrite: true);
        var report = fixture.Read();
        Assert.True(report.Applies);
        Assert.Equal(8, report.Rows.Count);
        Assert.All(report.Rows, row => Assert.Equal("Passed", row.Outcome));
    }

    private static void FailArgsRow(DeclaredTestReplayFixture fixture)
    {
        var document = JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        var tests = document["results"]!["tests"]!.AsArray();
        var row = Assert.Single(tests, test => test!["testId"]!.GetValue<string>() ==
            "conduct-tail::args::tests::parses_pattern_options_and_log_list_entries");
        row!["status"] = "failed";
        document["results"]!["summary"]!["passed"] = 7;
        document["results"]!["summary"]!["failed"] = 1;
        File.WriteAllText(fixture.Path, document.ToJsonString());
    }
}
