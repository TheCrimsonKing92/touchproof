using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Outcome = Mcg.AgentOrchestrator.App.Orchestration.GateTestObligationEvaluator.Outcome;

// Parallel-safe: each replay owns its report, manifest and attempt paths; other cases use immutable rows.
public sealed class DeclaredTestDotBoundarySelectorTests
{
    private const string TestFile = "tests/Get-McgGoalCard.Tests.ps1";
    private const string Describe = "GoalCardAcrossRoots";
    private const string First = Describe + ".reads_events_while_a_writer_holds_the_file_open";
    private const string Second = Describe + ".renders_both_roots";
    private const string Legacy = Describe + "Legacy.other";

    [Theory]
    [InlineData(Describe, "passed", "passed", nameof(Outcome.Satisfied))]
    [InlineData(Describe, "passed", "failed", nameof(Outcome.Failed))]
    [InlineData(Describe, "failed", "passed", nameof(Outcome.Failed))]
    [InlineData(Describe, "skipped", "skipped", nameof(Outcome.Failed))]
    [InlineData(TestFile + "::" + Describe, "passed", "passed", nameof(Outcome.Satisfied))]
    [InlineData(TestFile + "::" + Describe, "passed", "failed", nameof(Outcome.Failed))]
    [InlineData(First, "passed", "failed", nameof(Outcome.Satisfied))]
    [InlineData("GoalCard", "passed", "passed", nameof(Outcome.SelectorNotFound))]
    [InlineData("GoalCardAcross", "passed", "passed", nameof(Outcome.SelectorNotFound))]
    public void Evaluate_PesterBlockNames_GradesOnlySelectedTests(
        string selector, string firstStatus, string secondStatus, string expected)
    {
        using var fixture = new DeclaredTestReplayFixture();
        WriteReport(fixture, firstStatus, secondStatus);
        var report = fixture.Read();
        Assert.True(report.Applies);
        Assert.Equal(new[] { TestFile + "::" + First, TestFile + "::" + Second, TestFile + "::" + Legacy },
            report.Rows.Select(row => row.Identity));

        var result = fixture.Evaluate(selector);

        Assert.Equal(expected, result.Outcome.ToString());
        Assert.Equal(DeclaredTestReplayFixture.SourceId, result.SourceId);
        if (expected != nameof(Outcome.Satisfied)) Assert.Equal(new[] { selector }, result.Selectors);
    }

    [Theory]
    [InlineData("Describe", "file::Describe.Context.It", true)]
    [InlineData("file::Describe", "file::Describe.Context.It", true)]
    [InlineData("Describe.Context", "file::Describe.Context.It", true)]
    [InlineData("Describe.Context.It", "file::Describe.Context.It", true)]
    [InlineData("Describe", "file::DescribeLegacy.It", false)]
    [InlineData("Describe", "file::Describe/It", false)]
    [InlineData("describe", "file::Describe.It", false)]
    [InlineData("file::Describe", "File::Describe.It", false)]
    [InlineData("file::Describe", "file.Context::Describe.It", false)]
    [InlineData("file::Context::Describe", "file::Context.Child::Describe.It", false)]
    [InlineData("file::Describe", "file::other::Describe.It", false)]
    [InlineData("file::Describe", "pkg::file::Describe.It::tail", true)]
    [InlineData("x::", "x::.hidden", false)]
    [InlineData("x::", "x::", true)]
    [InlineData("", ".hidden", false)]
    public void Matches_LastSegment_RequiresOrdinalDotBoundary(
        string selector, string identity, bool expected)
    {
        Assert.Equal(expected, DeclaredTestSegmentSelector.Matches(selector, identity));
    }

    [Theory]
    [InlineData("Passed", 2, 0, true)]
    [InlineData("Failed", 1, 1, false)]
    public void Evaluate_ExactAndDescendantRows_RequiresBothToPass(
        string descendantOutcome, int passed, int failed, bool satisfied)
    {
        var result = DeclaredTestSelectorResolver.Evaluate(Describe, [
            new(TestFile + "::" + Describe, Describe, [TestFile], "Passed", "report"),
            new(TestFile + "::" + First, First, [TestFile], descendantOutcome, "report")]);

        Assert.Equal(passed, result.Passed);
        Assert.Equal(failed, result.Failed);
        Assert.Equal(0, result.Skipped + result.Superseded);
        Assert.Equal(satisfied, result.Satisfied);
    }

    private static void WriteReport(DeclaredTestReplayFixture fixture, string firstStatus, string secondStatus)
    {
        var document = JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        document["results"]!["tests"] = new JsonArray(
            Test(First, firstStatus), Test(Second, secondStatus), Test(Legacy, "failed"));
        document["results"]!["summary"] = new JsonObject
        {
            ["tests"] = 3,
            ["passed"] = new[] { firstStatus, secondStatus }.Count(status => status == "passed"),
            ["failed"] = 1 + new[] { firstStatus, secondStatus }.Count(status => status == "failed"),
            ["skipped"] = new[] { firstStatus, secondStatus }.Count(status => status == "skipped")
        };
        File.WriteAllText(fixture.Path, document.ToJsonString());
    }

    private static JsonObject Test(string name, string status) => new()
    {
        ["testId"] = TestFile + "::" + name,
        ["name"] = name,
        ["suite"] = new JsonArray(TestFile),
        ["status"] = status
    };
}
