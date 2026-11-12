using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temporary result files and changes no shared state.
public sealed class AcceptanceIncompleteShortfallExplanationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("shortfall-").FullName;

    [Xunit.Fact]
    public void IsExplained_ObservedCounts_ExplainsOnlyTheTimeout()
    {
        var path = WriteTrx(Counters(4057, 4054, 2, 4050, 4, 1),
            ("Cls.Failed", "Failed"), ("Cls.TimedOut", "Timeout"));
        Assert.True(Explain(Decision(4057, 4054, 2), path));
    }

    [Xunit.Theory]
    [Xunit.InlineData(4058, 4054, 2)] // Shortfall two, only one timeout.
    [Xunit.InlineData(4055, 4054, 2)] // Negative shortfall.
    [Xunit.InlineData(4056, 4054, 2)] // Excess explanation for zero shortfall.
    public void IsExplained_ShortfallDoesNotMatchNamedResults_IsUnexplained(int total, int executed, int skipped)
    {
        var path = WriteTrx(Counters(total, executed, skipped, 4050, 4, 1), ("Cls.TimedOut", "Timeout"));
        Assert.False(Explain(Decision(total, executed, skipped), path));
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, 4054, 2)]
    [Xunit.InlineData(4057, null, 2)]
    [Xunit.InlineData(4057, 4054, null)]
    public void IsExplained_UnknownCount_IsUnexplained(int? total, int? executed, int? skipped)
    {
        var path = WriteTrx(Counters(4057, 4054, 2, 4050, 4, 1), ("Cls.TimedOut", "Timeout"));
        Assert.False(Explain(Decision(total, executed, skipped), path));
    }

    [Xunit.Fact]
    public void IsExplained_MissingOrMalformedEvidence_IsUnexplained()
    {
        var decision = Decision(1, 0, 0);
        Assert.False(AcceptanceIncompleteShortfallExplanation.IsExplained(decision, ["Cls.TimedOut"], []));
        Assert.False(Explain(decision, Path.Combine(_root, "missing.trx")));
        var malformed = Path.Combine(_root, "malformed.trx");
        File.WriteAllText(malformed, "<TestRun>");
        Assert.False(Explain(decision, malformed));
        var valid = WriteTrx(Counters(1, 0, 0, 0, 0, 1), ("Cls.TimedOut", "Timeout"));
        Assert.False(AcceptanceIncompleteShortfallExplanation.IsExplained(
            decision, ["Cls.TimedOut"], [valid, malformed]));
        Assert.False(AcceptanceIncompleteShortfallExplanation.IsExplained(null, ["Cls.TimedOut"], [valid]));
    }

    [Xunit.Fact]
    public void IsExplained_CountsDisagreeWithDecision_IsUnexplained()
    {
        var path = WriteTrx(Counters(4057, 4054, 2, 4050, 4, 1), ("Cls.TimedOut", "Timeout"));
        Assert.False(Explain(Decision(4058, 4055, 2), path));
    }

    [Xunit.Fact]
    public void IsExplained_ExecutedCounterAbsent_TimeoutAlreadyCounted()
    {
        var counters = Counters(3, 2, 0, 0, 1, 1);
        counters.Attribute("executed")!.Remove();
        var path = WriteTrx(counters, ("Cls.Failed", "Failed"), ("Cls.TimedOut", "Timeout"));
        Assert.False(Explain(Decision(3, 2, 0), path));
    }

    [Xunit.Theory]
    [Xunit.InlineData(1, false)]
    [Xunit.InlineData(0, true)]
    public void IsExplained_FailedRow_RequiresEvidenceOfExclusion(int executed, bool expected)
    {
        var path = WriteTrx(Counters(executed + 1, executed, 0, 0, 1, 0), ("Cls.Failed", "Failed"));
        Assert.Equal(expected, Explain(Decision(executed + 1, executed, 0), path));
    }

    [Xunit.Fact]
    public void IsExplained_TimeoutIsNotNamed_IsUnexplained()
    {
        var path = WriteTrx(Counters(2, 1, 0, 0, 1, 1),
            ("Cls.Failed", "Failed"), ("Other.Timeout", "Timeout"));
        Assert.False(Explain(Decision(2, 1, 0), path));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Cls.TimedOut", "Cls.TimedOut", 1, true)]
    [Xunit.InlineData("Cls.TimedOut", "Cls.TimedOut", 2, false)]
    [Xunit.InlineData("Cls.TimedOut(a: 1)", "Cls.TimedOut(a: 2)", 2, true)]
    public void IsExplained_DuplicateAndParameterizedRows_CountsLogicalIdentities(
        string first, string second, int shortfall, bool expected)
    {
        var path = WriteTrx(Counters(shortfall, 0, 0, 0, 0, 2), (first, "Timeout"), (second, "Timeout"));
        Assert.Equal(expected, AcceptanceIncompleteShortfallExplanation.IsExplained(
            Decision(shortfall, 0, 0), [first, second], [path]));
    }

    [Xunit.Fact]
    public void IsExplained_ConflictingIdentityOutcomes_IsUnexplained()
    {
        var path = WriteTrx(Counters(2, 1, 0, 0, 1, 1),
            ("Cls.TimedOut", "Timeout"), ("Cls.TimedOut", "Failed"));
        Assert.False(Explain(Decision(2, 1, 0), path));
    }

    [Xunit.Theory]
    [Xunit.InlineData("timeout", "2")]
    [Xunit.InlineData("executed", "unknown")]
    [Xunit.InlineData("notExecuted", "-1")]
    public void IsExplained_ConflictingOrMalformedCounters_IsUnexplained(string counter, string value)
    {
        var counters = Counters(1, 0, 0, 0, 0, 1);
        counters.SetAttributeValue(counter, value);
        var path = WriteTrx(counters, ("Cls.TimedOut", "Timeout"));
        Assert.False(Explain(Decision(1, 0, 0), path));
    }

    [Xunit.Fact]
    public void IsExplained_ZeroShortfallWithoutExcludedResults_IsExplained()
    {
        var path = WriteTrx(Counters(1, 1, 0, 0, 1, 0), ("Cls.Failed", "Failed"));
        Assert.True(Explain(Decision(1, 1, 0), path));
    }

    [Xunit.Fact]
    public void IsExplained_MultipleReceipts_SumsCountsAndResolvesDefinitions()
    {
        var first = WriteTrx(Counters(2, 1, 0, 0, 1, 1),
            ("Cls.Failed", "Failed"), ("Cls.TimedOut", "Timeout"));
        var second = WriteTrx(Counters(1, 0, 0, 0, 0, 1), ("display", "Timeout"));
        var document = XDocument.Load(second);
        document.Descendants().Single(element => element.Name.LocalName == "UnitTestResult")
            .SetAttributeValue("testId", "timeout-id");
        document.Root!.Add(new XElement("TestDefinitions", new XElement("UnitTest",
            new XAttribute("id", "timeout-id"), new XElement("TestMethod",
                new XAttribute("className", "Other"), new XAttribute("name", "TimedOut")))));
        document.Save(second);
        Assert.True(AcceptanceIncompleteShortfallExplanation.IsExplained(
            Decision(3, 1, 0), ["Cls.Failed", "Cls.TimedOut", "Other.TimedOut"], [first, second]));
    }

    private static bool Explain(AcceptanceShardCompletionDecision decision, string path) =>
        AcceptanceIncompleteShortfallExplanation.IsExplained(decision, ["Cls.Failed", "Cls.TimedOut"], [path]);

    private static AcceptanceShardCompletionDecision Decision(int? total, int? executed, int? skipped) =>
        new(false, AcceptanceShardCompletionPredicates.IncompleteExecution, false, 1,
            total, executed, "Failed", NotExecutedTestCount: skipped);

    private static XElement Counters(int total, int executed, int skipped, int passed, int failed, int timeout) =>
        new("Counters", new XAttribute("total", total), new XAttribute("executed", executed),
            new XAttribute("notExecuted", skipped), new XAttribute("passed", passed),
            new XAttribute("failed", failed), new XAttribute("timeout", timeout));

    private string WriteTrx(XElement counters, params (string Identity, string Outcome)[] results)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "ResultSummary", counters),
            new XElement(ns + "Results", results.Select(result => new XElement(ns + "UnitTestResult",
                new XAttribute("testName", result.Identity), new XAttribute("outcome", result.Outcome)))))).Save(path);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
