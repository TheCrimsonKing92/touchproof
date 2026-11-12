using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = PreTesterCoupledClassSelectionsTests.Fixture;

// Parallel-safe: hand-written candidate sources and TRX in a private tree, no git or runners.
public sealed class ImpactReplayUnboundedSelectionTests
{
    [Theory]
    [InlineData("SelectedTestClassBound", 1001)]
    [InlineData("ChangedSourceFileCount", 1001)]
    [InlineData("FrontierSymbolBound", 2)]
    public void CurrentSelectionAndHistoricalBoundReport_KeepUnboundedCountAndShare(string kind, int count)
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture, kind, count);
        var bounded = ReverseDependencyTestImpactReader.Read(fixture.Root, input.Changed);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, bounded.Outcome);
        Assert.Null(bounded.DegradationKind);
        Assert.Equal(count, bounded.TestClassNames.Count);

        var measured = ImpactReplayUnboundedSelectionMeasurement.Apply(input.Attempt, fixture.Root,
            input.Changed, input.Sources, input.Classes, null, []);

        Assert.Equal(count, measured.UnboundedSelectedClassCount);
        Assert.Equal(0.25, measured.UnboundedNonFloorTestSecondsShare);
        Assert.Null(measured.UnboundedClassCountUnavailableReason);
        Assert.Null(measured.UnboundedDurationShareUnavailableReason);
        Assert.Equal(input.Attempt.SelectionOutcome, measured.SelectionOutcome);
        Assert.Equal(input.Attempt.DegradationKind, measured.DegradationKind);
        Assert.Equal(input.Attempt.SelectionReason, measured.SelectionReason);
        Assert.Equal(input.Attempt.WouldRunLanes, measured.WouldRunLanes);
        Assert.Empty(measured.SkippedLanes);
        var (json, markdown) = WriteReport(fixture, [measured]);
        using (json)
        {
            var attempt = json.RootElement.GetProperty("attempts")[0];
            Assert.Equal(count, attempt.GetProperty("unboundedSelectedClassCount").GetInt32());
            Assert.Equal(0.25, attempt.GetProperty("unboundedNonFloorTestSecondsShare").GetDouble());
        }
        Assert.Contains($"| {kind} | hand-written | {count} | 25.0% |", markdown);
        Assert.Contains($"{kind}: 1 attempts; class count unavailable: 0; duration share unavailable: 0", markdown);
    }

    [Theory]
    [InlineData("missing-path")]
    [InlineData("missing-file")]
    [InlineData("missing-duration")]
    [InlineData("missing-class")]
    [InlineData("zero-baseline")]
    public void IncompleteDurations_KeepCountAndReportUnavailableShare(string fault)
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture, "SelectedTestClassBound", 1001);
        var path = input.Attempt.LaneOutcomes[0].TrxPaths[0];
        var attempt = input.Attempt;
        if (fault == "missing-file") File.Delete(path);
        if (fault == "missing-duration")
            File.WriteAllText(path, "<TestRun><UnitTestResult testName='Consumer0Tests.Works' outcome='Passed' /></TestRun>");
        if (fault == "missing-class")
            File.WriteAllText(path, "<TestRun><UnitTestResult duration='00:00:02' outcome='Passed' /></TestRun>");
        if (fault == "missing-path") attempt = attempt with { LaneOutcomes = [new("lane", "Passed", [])] };
        if (fault == "zero-baseline")
        {
            path = WriteTrx(fixture.Root, "zero", ("Consumer0Tests", 0));
            attempt = attempt with { LaneOutcomes = [new("lane", "Passed", [path])], BalancedLaneOutcomes = [] };
        }
        var measured = ImpactReplayUnboundedSelectionMeasurement.Apply(attempt, fixture.Root,
            input.Changed, input.Sources, input.Classes, null, []);

        Assert.Equal(1001, measured.UnboundedSelectedClassCount);
        Assert.Null(measured.UnboundedNonFloorTestSecondsShare);
        Assert.Null(measured.UnboundedClassCountUnavailableReason);
        Assert.False(string.IsNullOrWhiteSpace(measured.UnboundedDurationShareUnavailableReason));
        var (json, markdown) = WriteReport(fixture, [measured]);
        using (json)
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("attempts")[0]
                .GetProperty("unboundedNonFloorTestSecondsShare").ValueKind);
        Assert.Contains("| SelectedTestClassBound | hand-written | 1001 | unavailable:", markdown);
        Assert.Contains("class count unavailable: 0; duration share unavailable: 1", markdown);
    }

    [Fact]
    public void MissingFloorTrx_DoesNotInvalidateNonFloorShare()
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture, "SelectedTestClassBound", 1001);
        var attempt = input.Attempt with
        {
            LaneOutcomes = [input.Attempt.LaneOutcomes[0], new("floor", "Passed", [])],
            DurationCoverageComplete = false
        };
        var measured = ImpactReplayUnboundedSelectionMeasurement.Apply(attempt, fixture.Root,
            input.Changed, input.Sources, input.Classes, null, []);
        Assert.Equal(1001, measured.UnboundedSelectedClassCount);
        Assert.Equal(0.25, measured.UnboundedNonFloorTestSecondsShare);
        Assert.Null(measured.UnboundedDurationShareUnavailableReason);
    }

    [Theory]
    [InlineData("unreadable-index")]
    [InlineData("unmapped-class")]
    public void IncompleteSelection_ReportsNoPartialCountAndContinues(string fault)
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture, "SelectedTestClassBound", 1001);
        var project = Path.Combine(fixture.Root, Fixture.Project);
        var projectText = File.ReadAllText(project);
        if (fault == "unreadable-index") File.WriteAllText(project, "<Project>");
        var unavailable = ImpactReplayUnboundedSelectionMeasurement.Apply(input.Attempt, fixture.Root,
            input.Changed, input.Sources, input.Classes, null, fault == "unmapped-class" ? ["MissingTests"] : []);
        Assert.Null(unavailable.UnboundedSelectedClassCount);
        Assert.Null(unavailable.UnboundedNonFloorTestSecondsShare);
        Assert.False(string.IsNullOrWhiteSpace(unavailable.UnboundedClassCountUnavailableReason));
        Assert.Equal(unavailable.UnboundedClassCountUnavailableReason, unavailable.UnboundedDurationShareUnavailableReason);
        File.WriteAllText(project, projectText);
        var next = ImpactReplayUnboundedSelectionMeasurement.Apply(input.Attempt with { AttemptId = "next" },
            fixture.Root, input.Changed, input.Sources, input.Classes, null, []);
        Assert.Equal(1001, next.UnboundedSelectedClassCount);
        var (json, markdown) = WriteReport(fixture, [unavailable, next]);
        using (json)
        {
            var attempts = json.RootElement.GetProperty("attempts");
            Assert.Equal(JsonValueKind.Null, attempts[0].GetProperty("unboundedSelectedClassCount").ValueKind);
            Assert.Equal(1001, attempts[1].GetProperty("unboundedSelectedClassCount").GetInt32());
        }
        Assert.Contains("SelectedTestClassBound: 2 attempts; class count unavailable: 1; duration share unavailable: 1", markdown);
        Assert.Contains("| SelectedTestClassBound | hand-written | unavailable:", markdown);
        Assert.Contains("| SelectedTestClassBound | next | 1001 | 25.0% |", markdown);
    }

    [Theory]
    [InlineData("ChangedFileUnmapped")]
    [InlineData("UnmappedTestClass")]
    [InlineData("ChangedTestSupportUnresolved")]
    public void MappingAbandonment_DoesNotAcquireBoundMeasurements(string kind)
    {
        var attempt = Attempt(kind, [], [], []);
        var measured = ImpactReplayUnboundedSelectionMeasurement.Apply(attempt, "unused", [],
            new ImpactReplayTestSourceIndex(Array.Empty<Microsoft.CodeAnalysis.SyntaxTree>()), [], null, []);
        Assert.Same(attempt, measured);
        Assert.DoesNotContain("## Unbounded selections", ImpactReplayReportWriter.Render(new("override", [], [measured], [], [], [])));
    }

    private static (ImpactReplayAttemptRecord Attempt, string[] Changed,
        ImpactReplayTestSourceIndex Sources, AcceptanceTestClassDescriptor[] Classes) Arrange(
        Fixture fixture, string kind, int count)
    {
        fixture.Write(Fixture.Source, "public class NewContract {}" +
            (kind is "FrontierSymbolBound" or "ChangedSourceFileCount" ? string.Join("\n", Enumerable.Range(1, 64)
                .Select(index => $"public class ExtraType{index} {{}}")) : ""));
        var changed = new List<string> { Fixture.Source };
        if (kind == "ChangedSourceFileCount")
            foreach (var index in Enumerable.Range(1, 20))
            {
                var path = $"src/Mcg.AgentOrchestrator.Core/Extra{index}.cs";
                fixture.Write(path, $"public class ChangedType{index} {{}}");
                changed.Add(path);
            }
        var names = Enumerable.Range(0, count).Select(index => $"Consumer{index}Tests").ToArray();
        const string consumers = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Consumers.cs";
        fixture.Write(consumers, string.Join("\n", names.Select(name =>
            $"public class {name} {{ [Fact] public void Works() {{ _ = new NewContract(); }} }}")) +
            "\npublic class OtherTests { [Fact] public void Works() {} }");
        var classes = names.Append("OtherTests").Select(name => new AcceptanceTestClassDescriptor(name, null)).ToArray();
        var selected = WriteTrx(fixture.Root, "selected", (names[0], 2));
        var copy = Path.Combine(fixture.Root, "copy.trx");
        File.Copy(selected, copy);
        var other = WriteTrx(fixture.Root, "other", ("OtherTests", 6));
        var floor = WriteTrx(fixture.Root, "floor", (names[0], 100));
        var attempt = Attempt(kind, changed.ToArray(),
            [new("lane", "Passed", [selected]), new("floor", "Passed", [floor])],
            [new("balanced", "Passed", [copy, other])]);
        return (attempt, changed.ToArray(), new([Path.Combine(fixture.Root, consumers)]), classes);
    }

    private static ImpactReplayAttemptRecord Attempt(string kind, string[] changed,
        ImpactReplayLaneOutcome[] lanes, ImpactReplayLaneOutcome[] balanced) =>
        new("hand-written", "goal", DateTimeOffset.UnixEpoch, "main", "branch", "Abandoned", kind,
            "original fallback", changed, [], ["floor"], ["floor", "lane"], ["floor", "lane"], [], 0,
            lanes, [], true, [], BalancedLaneOutcomes: balanced);

    private static (JsonDocument Json, string Markdown) WriteReport(Fixture fixture, ImpactReplayAttemptRecord[] attempts)
    {
        var output = Path.Combine(fixture.Root, "report");
        ImpactReplayReportWriter.Write(output, new("override", ["floor"], attempts, [], [], []));
        return (JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "impact-replay.json"))),
            File.ReadAllText(Path.Combine(output, "impact-replay.md")));
    }

    private static string WriteTrx(string root, string name, params (string Class, int Seconds)[] results)
    {
        var path = Path.Combine(root, name + ".trx");
        var definitions = new XElement("TestDefinitions", results.Select((item, index) =>
            new XElement("UnitTest", new XAttribute("id", index),
                new XElement("TestMethod", new XAttribute("className", item.Class)))));
        var executions = new XElement("Results", results.Select((item, index) =>
            new XElement("UnitTestResult", new XAttribute("testId", index),
                new XAttribute("testName", item.Class + ".Works"), new XAttribute("executionId", name + index),
                new XAttribute("outcome", "Passed"),
                new XAttribute("duration", TimeSpan.FromSeconds(item.Seconds).ToString("c")))));
        new XDocument(new XElement("TestRun", definitions, executions)).Save(path);
        return path;
    }
}
