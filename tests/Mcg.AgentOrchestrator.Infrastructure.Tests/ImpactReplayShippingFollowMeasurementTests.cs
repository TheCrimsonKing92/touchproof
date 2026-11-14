using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = PreTesterCoupledClassSelectionsTests.Fixture;

// Parallel-safe: sources and duration receipts live in a private fixture tree; no runners or git.
public sealed class ImpactReplayShippingFollowMeasurementTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DiagnosticCallback_BothModesAndCachePaths_PreservesSelection(bool follow, bool bypass)
    {
        using var fixture = new Fixture();
        Arrange(fixture);
        var plain = ReverseDependencyTestImpactReader.Read(fixture.Root, [Fixture.Source],
            bypassCache: bypass, propagateShippingSourceReach: follow);
        var pairs = new List<(string Type, string Class)>();
        var diagnostic = ReverseDependencyTestImpactReader.Read(fixture.Root, [Fixture.Source],
            bypassCache: bypass, propagateShippingSourceReach: follow,
            shippingReachDiagnostics: (type, name) => pairs.Add((type, name)));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, plain.Outcome);
        Assert.Equal(JsonSerializer.Serialize(plain with { CacheReceipt = null }),
            JsonSerializer.Serialize(diagnostic with { CacheReceipt = null }));
        Assert.Contains(("ShippingConsumer", "ShippingConsumerTests"), pairs);
        Assert.Equal(follow, pairs.Contains(("ShippingConsumer", "ConsumerOnlyTests")));
        Assert.Equal(follow, diagnostic.TestClassNames.Contains("ConsumerOnlyTests"));
    }

    [Fact]
    public void ChangedConstant_AddsConsumerOnlyClassAndAttribution_LeavesDecisionUnchanged()
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture);
        var before = ReverseDependencyTestImpactReader.Read(fixture.Root, [Fixture.Source]);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, before.Outcome);
        Assert.Equal(["DirectTests", "ShippingConsumerTests"], before.TestClassNames);
        Assert.DoesNotContain("ConsumerOnlyTests", input.Attempt.SelectedTestClasses);
        var measured = Measure(fixture, input);

        Assert.Equal(["ConsumerOnlyTests"], measured.ShippingFollowAddedClasses);
        Assert.Equal(["consumer"], measured.ShippingFollowAddedLanes);
        var attribution = Assert.Single(measured.ShippingFollowAttributions!);
        Assert.Equal("ShippingConsumer", attribution.ShippingType);
        Assert.Equal(["ConsumerOnlyTests"], attribution.AddedClasses);
        Assert.Equal(5, measured.ShippingFollowAddedSeconds);
        Assert.Equal(100d * 10 / 15, measured.ShippingFollowRemainingSavingPercent!.Value, 8);
        Assert.Null(measured.ShippingFollowClassesUnavailableReason);
        Assert.Null(measured.ShippingFollowSecondsUnavailableReason);
        Assert.Null(measured.ShippingFollowRemainingSavingUnavailableReason);
        Assert.Equal(input.Attempt, measured with
        {
            ShippingFollowAddedClasses = null, ShippingFollowAddedLanes = null,
            ShippingFollowAddedSeconds = null, ShippingFollowRemainingSavingPercent = null,
            ShippingFollowAttributions = null
        });
        var after = ReverseDependencyTestImpactReader.Read(fixture.Root, [Fixture.Source]);
        Assert.DoesNotContain("ConsumerOnlyTests", after.TestClassNames);

        var output = Path.Combine(fixture.Root, "report");
        ImpactReplayReportWriter.Write(output, new("override", [], [measured], [], [], []));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "impact-replay.json")));
        var row = json.RootElement.GetProperty("attempts")[0];
        Assert.Equal("ConsumerOnlyTests", row.GetProperty("shippingFollowAddedClasses")[0].GetString());
        Assert.Equal("ShippingConsumer", row.GetProperty("shippingFollowAttributions")[0]
            .GetProperty("shippingType").GetString());
    }

    [Fact]
    public void SharedConsumerClass_AttributesEveryShippingType_OncePerType()
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture);
        fixture.Write("src/Mcg.AgentOrchestrator.Core/SecondConsumer.cs",
            "public class SecondConsumer { public string Text => NewContract.Text; }");
        fixture.Write(TestPath("ConsumerOnlyTests"), """
            public class ConsumerOnlyTests {
                [Fact] public void Works() { _ = new ShippingConsumer(); _ = new SecondConsumer(); }
                [Fact] public void Again() { _ = new ShippingConsumer(); }
            }
            """);
        var measured = Measure(fixture, input);
        Assert.Equal(["ConsumerOnlyTests"], measured.ShippingFollowAddedClasses);
        Assert.Equal(["SecondConsumer", "ShippingConsumer"], measured.ShippingFollowAttributions!
            .Select(item => item.ShippingType));
        Assert.All(measured.ShippingFollowAttributions!, item =>
            Assert.Equal(["ConsumerOnlyTests"], item.AddedClasses));
    }

    [Theory]
    [InlineData("missing-trx")]
    [InlineData("zero-saving")]
    [InlineData("negative-saving")]
    [InlineData("missing-saving")]
    public void MissingMetricInputs_PreserveClassAndLaneEvidence(string fault)
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture);
        var attempt = input.Attempt;
        if (fault == "missing-trx") attempt = attempt with { LaneOutcomes = [new("direct", "Passed", [])] };
        if (fault == "zero-saving") attempt = attempt with { ClassLevelSecondsSaved = 0 };
        if (fault == "negative-saving") attempt = attempt with { ClassLevelSecondsSaved = -1 };
        if (fault == "missing-saving") attempt = attempt with
            { ClassLevelSecondsSaved = null, ClassLevelUnavailableReason = "baseline missing" };
        var measured = Measure(fixture, input with { Attempt = attempt });
        Assert.Equal(["ConsumerOnlyTests"], measured.ShippingFollowAddedClasses);
        Assert.Equal(["consumer"], measured.ShippingFollowAddedLanes);
        Assert.Null(measured.ShippingFollowClassesUnavailableReason);
        Assert.Null(measured.ShippingFollowRemainingSavingPercent);
        Assert.False(string.IsNullOrWhiteSpace(measured.ShippingFollowRemainingSavingUnavailableReason));
        if (fault == "missing-trx")
        {
            Assert.Null(measured.ShippingFollowAddedSeconds);
            Assert.Equal("incomplete TRX duration or class evidence", measured.ShippingFollowSecondsUnavailableReason);
        }
        else Assert.Equal(5, measured.ShippingFollowAddedSeconds);
        if (fault == "missing-saving")
            Assert.Equal("baseline missing", measured.ShippingFollowRemainingSavingUnavailableReason);
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("floor")]
    [InlineData("already-selected")]
    public void AlreadyRetainedClass_AddsNoSecondsOrLanes(string retainedBy)
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture);
        var attempt = input.Attempt with { WouldRunLanes = ["direct", "consumer"] };
        ImpactReplaySourceGuardReport? guards = null;
        var selected = input.Recorded;
        if (retainedBy == "floor") attempt = attempt with { FloorLanes = ["consumer"] };
        if (retainedBy == "already-selected") selected = selected.Append("ConsumerOnlyTests").ToHashSet(StringComparer.Ordinal);
        if (retainedBy == "guard")
            guards = new ImpactReplaySourceGuardReport(["ConsumerOnlyTests"]);
        var measured = ImpactReplayShippingFollowMeasurement.Apply(attempt, fixture.Root, [Fixture.Source],
            input.Sources, input.Classes, guards, [], [], selected, input.Lanes);
        Assert.Empty(measured.ShippingFollowAddedLanes!);
        Assert.Equal(0, measured.ShippingFollowAddedSeconds);
        Assert.Equal(100, measured.ShippingFollowRemainingSavingPercent);
        if (retainedBy == "already-selected") Assert.Empty(measured.ShippingFollowAddedClasses!);
    }

    [Fact]
    public void AddedCostExceedsSaving_ReportsNegativeRemainingPercent()
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture);
        var measured = Measure(fixture, input with { Attempt = input.Attempt with { ClassLevelSecondsSaved = 2 } });
        Assert.Equal(-150, measured.ShippingFollowRemainingSavingPercent);
    }

    [Theory]
    [InlineData("recorded-unresolved")]
    [InlineData("unreadable-index")]
    [InlineData("unmapped-class")]
    public void UnavailableSelection_RecordsReasonWithoutNumbers(string fault)
    {
        using var fixture = new Fixture();
        var input = Arrange(fixture);
        if (fault == "recorded-unresolved") input = input with
            { Attempt = input.Attempt with { SelectionOutcome = "Abandoned", SelectionReason = "fixture fallback" } };
        if (fault == "unreadable-index") fixture.Write(Fixture.Project, "<Project>");
        if (fault == "unmapped-class") input = input with
            { Classes = input.Classes.Where(item => item.FullName != "ConsumerOnlyTests").ToArray() };
        var measured = Measure(fixture, input);
        Assert.Null(measured.ShippingFollowAddedClasses);
        Assert.Null(measured.ShippingFollowAddedLanes);
        Assert.Null(measured.ShippingFollowAddedSeconds);
        Assert.Null(measured.ShippingFollowRemainingSavingPercent);
        Assert.False(string.IsNullOrWhiteSpace(measured.ShippingFollowClassesUnavailableReason));
        Assert.Equal(measured.ShippingFollowClassesUnavailableReason, measured.ShippingFollowSecondsUnavailableReason);
        Assert.Equal(measured.ShippingFollowClassesUnavailableReason, measured.ShippingFollowRemainingSavingUnavailableReason);
    }

    [Fact]
    public void TwelveTypes_ReportTopTenDistributionsAndUnavailableReason()
    {
        // Reversed input order and a tie near the cutoff make traversal order observable.
        int[] counts = [1, 2, 3, 3, 5, 6, 7, 8, 9, 10, 11, 12];
        var attempts = counts.Select((count, index) => Attempt($"attempt-{index:00}") with
        {
            ShippingFollowAddedClasses = Enumerable.Range(0, count).Select(n => $"Class{n}").ToArray(),
            ShippingFollowAddedLanes = ["extra"], ShippingFollowAddedSeconds = count,
            ShippingFollowRemainingSavingPercent = 10 * (index + 1),
            ShippingFollowAttributions = [new($"Type{index:00}", Enumerable.Range(0, count)
                .Select(n => $"Class{n}").Append("Class0").ToArray())]
        }).Reverse().ToList();
        attempts.Add(Attempt("unavailable") with
        {
            ShippingFollowClassesUnavailableReason = "fixture input missing",
            ShippingFollowSecondsUnavailableReason = "fixture input missing",
            ShippingFollowRemainingSavingUnavailableReason = "fixture input missing"
        });
        attempts.Add(Attempt("old-checkpoint"));
        var markdown = ImpactReplayReportWriter.Render(new("override", [], attempts, [], [], []));
        var section = markdown[markdown.IndexOf("## Hop-1 shipping consumers", StringComparison.Ordinal)..];
        Assert.Contains("- Added classes: minimum 1; median 6.5; 90th percentile 11; maximum 12; unavailable: 2", section);
        Assert.Contains("- Remaining saving: minimum 10%; median 65%; 90th percentile 110%; maximum 120%; unavailable: 2", section);
        Assert.Contains("| unavailable | unavailable: fixture input missing | unavailable: fixture input missing | unavailable: fixture input missing | unavailable: fixture input missing |", section);
        Assert.Contains("| old-checkpoint | unavailable: measurement not recorded |", section);
        var ranking = section.Split('\n').Where(line => line.StartsWith("| Type", StringComparison.Ordinal))
            .Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Equal(new[] { "| Type11 | 12 |", "| Type10 | 11 |", "| Type09 | 10 |", "| Type08 | 9 |",
            "| Type07 | 8 |", "| Type06 | 7 |", "| Type05 | 6 |", "| Type04 | 5 |",
            "| Type02 | 3 |", "| Type03 | 3 |" }, ranking);
    }

    [Fact]
    public void RepeatedClassAcrossAttempts_RankingSumsPerAttempt()
    {
        var attempt = Attempt("first") with
        {
            ShippingFollowAddedClasses = ["SharedTests"], ShippingFollowAddedLanes = [],
            ShippingFollowAttributions = [new("SharedType", ["SharedTests", "SharedTests"])]
        };
        var markdown = ImpactReplayReportWriter.Render(new("override", [],
            [attempt, attempt with { AttemptId = "second" }], [], [], []));
        Assert.Contains("| SharedType | 2 |", markdown);
        Assert.Contains("- Remaining saving: minimum unavailable; median unavailable; 90th percentile unavailable; maximum unavailable; unavailable: 2", markdown);
    }

    private static Input Arrange(Fixture fixture)
    {
        fixture.Write(Fixture.Source, "public static class NewContract { public const string Text = \"changed\"; }");
        fixture.Write("src/Mcg.AgentOrchestrator.Core/ShippingConsumer.cs",
            "public class ShippingConsumer { public string Text => NewContract.Text; }");
        fixture.Write(TestPath("DirectTests"),
            "public class DirectTests { [Fact] public void Works() { _ = NewContract.Text; } }");
        fixture.Test("ShippingConsumerTests", "ShippingConsumer");
        fixture.Test("ConsumerOnlyTests", "ShippingConsumer");
        fixture.Test("OtherTests");
        AcceptanceTestClassDescriptor[] classes = [new("DirectTests", null), new("ShippingConsumerTests", null),
            new("ConsumerOnlyTests", null), new("OtherTests", null)];
        AcceptanceTestLane[] lanes = [new("direct", "FullyQualifiedName=DirectTests|FullyQualifiedName=ShippingConsumerTests"),
            new("consumer", "FullyQualifiedName=ConsumerOnlyTests"), new("other", "FullyQualifiedName=OtherTests")];
        var trx = Path.Combine(fixture.Root, "duration.trx");
        var results = new[] { ("DirectTests", 2), ("ShippingConsumerTests", 3), ("ConsumerOnlyTests", 5), ("OtherTests", 10) };
        new XDocument(new XElement("TestRun", new XElement("TestDefinitions", results.Select((item, index) =>
                new XElement("UnitTest", new XAttribute("id", index),
                    new XElement("TestMethod", new XAttribute("className", item.Item1))))),
            new XElement("Results", results.Select((item, index) => new XElement("UnitTestResult",
                new XAttribute("testId", index), new XAttribute("testName", item.Item1 + ".Works"),
                new XAttribute("outcome", "Passed"), new XAttribute("duration", TimeSpan.FromSeconds(item.Item2).ToString("c")))))))
            .Save(trx);
        var attempt = Attempt("constant") with { LaneOutcomes = [new("direct", "Passed", [trx])] };
        return new(attempt, new(classes.Select(item => Path.Combine(fixture.Root, TestPath(item.FullName)))),
            classes, lanes, new HashSet<string>(attempt.SelectedTestClasses, StringComparer.Ordinal));
    }

    private static ImpactReplayAttemptRecord Attempt(string id) =>
        new(id, "goal", DateTimeOffset.UnixEpoch, "main", "branch", "Resolved", null, null,
            [Fixture.Source], ["DirectTests", "ShippingConsumerTests"], [], ["direct", "consumer", "other"],
            ["direct"], ["consumer", "other"], 15, [], [], true, [], ClassLevelSecondsSaved: 15);
    private static ImpactReplayAttemptRecord Measure(Fixture fixture, Input input) =>
        ImpactReplayShippingFollowMeasurement.Apply(input.Attempt, fixture.Root, [Fixture.Source],
            input.Sources, input.Classes, null, [], [], input.Recorded, input.Lanes);
    private static string TestPath(string name) => "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + name + ".cs";
    private sealed record Input(ImpactReplayAttemptRecord Attempt, ImpactReplayTestSourceIndex Sources,
        AcceptanceTestClassDescriptor[] Classes, AcceptanceTestLane[] Lanes, IReadOnlySet<string> Recorded);
}
