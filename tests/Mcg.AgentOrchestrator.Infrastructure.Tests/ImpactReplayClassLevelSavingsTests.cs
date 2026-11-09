using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: only hand-written receipts in a private temporary directory.
public sealed class ImpactReplayClassLevelSavingsTests : IDisposable
{
    private readonly string root = SharedTestSupport.CreateTempDirectory();
    private static readonly AcceptanceTestClassDescriptor[] Classes =
        [new("Sample.AlphaTests", null), new("Sample.BetaTests", null),
         new("Sample.GammaTests", null), new("Sample.FloorTests", null)];

    [Theory]
    [InlineData(true, false, 7.25)]
    [InlineData(false, true, 0d)]
    [InlineData(false, false, null)]
    public void Selection_CompleteReceipts_OnlyAvoidableExecutionsAreSaved(
        bool resolved, bool abandoned, double? expected)
    {
        var alpha = Write("alpha", ("Sample.AlphaTests", "00:00:01.5", "a1", "Passed"),
            ("Sample.AlphaTests", "00:00:00.5", "a2", "Passed"),
            ("Sample.BetaTests", "00:00:03", "b1", "Failed"));
        var beta = Write("beta", ("Sample.GammaTests", "00:00:04", "g1", "Passed"),
            ("Sample.GammaTests", "00:00:00.25", "g2", "Passed"));
        var floor = Write("floor", ("Sample.FloorTests", "00:00:10", "f1", "Passed"));

        var estimate = ImpactReplayClassLevelSavings.Measure(
            [new("Alpha lane", "Failed", [alpha]), new("Chaos gate", "Passed", [floor])],
            [new("Beta lane", "Passed", [beta])], ["Chaos gate"],
            new HashSet<string>(["Sample.AlphaTests"], StringComparer.Ordinal), Classes,
            resolved, abandoned, durationComplete: true);

        Assert.Equal(expected, estimate.SecondsSaved);
        Assert.Equal(9.25d, estimate.NonFloorTestSeconds);
        Assert.Equal(!resolved && !abandoned ? "selection unresolved" : null, estimate.UnavailableReason);
        Assert.Null(estimate.CpuSecondsSaved);
        Assert.Equal("no per-class CPU data read by impact-replay", estimate.CpuUnavailableReason);
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("malformed")]
    [InlineData("missing-duration")]
    [InlineData("missing-class")]
    [InlineData("missing-path")]
    [InlineData("duration-incomplete")]
    public void IncompleteEvidence_RejectsPartialNumeratorAndDenominator(string fault)
    {
        var valid = Write("valid", ("Sample.BetaTests", "00:00:03", "b1", "Passed"));
        var path = Path.Combine(root, "fault.trx");
        if (fault == "malformed") File.WriteAllText(path, "<TestRun>");
        else if (fault == "missing-duration")
            path = Write("fault", ("Sample.GammaTests", null, "g1", "Passed"));
        else if (fault == "missing-class")
            File.WriteAllText(path, "<TestRun><UnitTestResult duration='00:00:04' outcome='Passed' /></TestRun>");
        else if (fault == "duration-incomplete") path = valid;

        var estimate = ImpactReplayClassLevelSavings.Measure(
            [new("Alpha lane", "Passed", [valid]),
             new("Beta lane", "Passed", fault == "missing-path" ? [] : [path])], [], [],
            new HashSet<string>(StringComparer.Ordinal), Classes, true, false, fault != "duration-incomplete");

        Assert.Null(estimate.SecondsSaved);
        Assert.Null(estimate.NonFloorTestSeconds);
        Assert.Equal("incomplete TRX duration or class evidence", estimate.UnavailableReason);
    }

    [Fact]
    public void RetainedClasses_ExcludesSelectedGuardsAndFloorMembersAcrossLanes()
    {
        AcceptanceTestLane[] lanes =
            [new("Chaos gate", "FullyQualifiedName=Sample.FloorTests"),
             new("Alpha lane", "FullyQualifiedName~Tests")];
        var retained = ImpactReplayClassLevelSavings.RetainedClasses(["Sample.AlphaTests"],
            ["Sample.BetaTests"], lanes, ["Chaos gate"], Classes);
        Assert.Equal(new[] { "Sample.AlphaTests", "Sample.BetaTests", "Sample.FloorTests" }, retained.Order());
        var path = Write("mixed", ("Sample.AlphaTests", "00:00:01", "a", "Passed"),
            ("Sample.BetaTests", "00:00:02", "b", "Passed"),
            ("Sample.FloorTests", "00:00:04", "f", "Passed"),
            ("Sample.GammaTests", "00:00:08", "g", "Passed"),
            ("Unscanned.Tests", "00:00:16", "u", "Passed"));

        var estimate = ImpactReplayClassLevelSavings.Measure([new("Alpha lane", "Passed", [path])],
            [new("Balanced 1", "Passed", [path])], ["Chaos gate"], retained, Classes, true, false, true);

        Assert.Equal(8d, estimate.SecondsSaved);
        Assert.Equal(31d, estimate.NonFloorTestSeconds);
    }

    [Fact]
    public void Retries_CountExecutionsOnceWithoutCountingReceiptCopies()
    {
        var first = Write("first", ("Sample.BetaTests", "00:00:02", "b1", "Failed"),
            ("Sample.BetaTests", "00:00:02", "b1", "Failed"),
            ("Sample.BetaTests", "00:00:04", "b2", "Passed"),
            ("Sample.BetaTests", "00:00:16", "skipped", "Skipped"));
        var retry = Write("first-run-1", ("Sample.BetaTests", "00:00:08", "b3", "Passed"));
        var copy = Path.Combine(root, "copy.trx");
        File.Copy(first, copy);

        var estimate = ImpactReplayClassLevelSavings.Measure(
            [new("Alpha lane", "Passed", [first, retry]), new("Beta lane", "Passed", [first, copy])],
            [], [], new HashSet<string>(StringComparer.Ordinal), Classes, true, false, true);

        Assert.Equal(14d, estimate.SecondsSaved);
        Assert.Equal(14d, estimate.NonFloorTestSeconds);
    }

    [Fact]
    public void SharedFloorReceipt_IsExcludedFromEveryLane()
    {
        var path = Write("shared", ("Sample.BetaTests", "00:00:03", "b1", "Passed"));
        var estimate = ImpactReplayClassLevelSavings.Measure(
            [new("Alpha lane", "Passed", [path]), new("Chaos gate", "Passed", [path])],
            [], ["Chaos gate"], new HashSet<string>(StringComparer.Ordinal), Classes, true, false, true);
        Assert.Equal(0d, estimate.SecondsSaved);
        Assert.Equal(0d, estimate.NonFloorTestSeconds);
    }

    private string Write(string name, params (string Class, string? Duration, string Execution, string Outcome)[] executions)
    {
        var path = Path.Combine(root, name + ".trx");
        new XDocument(new XElement("TestRun",
            new XElement("TestDefinitions", executions.Select((item, index) => new XElement("UnitTest",
                new XAttribute("id", index), new XElement("TestMethod", new XAttribute("className", item.Class))))),
            new XElement("Results", executions.Select((item, index) => new XElement("UnitTestResult",
                new XAttribute("testId", index), new XAttribute("testName", item.Class + ".Works"),
                new XAttribute("executionId", item.Execution), new XAttribute("outcome", item.Outcome),
                item.Duration is null ? null : new XAttribute("duration", item.Duration)))))).Save(path);
        return path;
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
