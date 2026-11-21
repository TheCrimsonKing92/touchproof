using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PreTesterReverseDependencySelectionEquivalenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealCostPathPreservesInputsAndDecisionWithBrokenLaneOnlySource(bool skip)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        fixture.WithCostHistory("""
            , "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests", "tests/LaneOnly.Tests"],
              "balancedLanePool": { "laneCount": 2 }
            """);
        // This nested project is in lane identity, but outside both gate-scanned projects.
        fixture.Write("tests/LaneOnly.Tests/Nested/Nested.csproj", "<Project />");
        fixture.Write("tests/LaneOnly.Tests/Nested/Broken.cs", "public class Broken {");
        var state = HostLedgerStateDirectory.Resolve(fixture.Root);
        if (skip)
        {
            var attempts = Path.Combine(state, "acceptance-gate-attempts");
            // Completed attempts stay in the denominator without any recorded candidate failures.
            foreach (var resultPath in Directory.EnumerateFiles(attempts, "*.result.json"))
                File.Delete(resultPath);
        }
        var selected = new[] { "ConsumerTests" };
        var changed = new[] { PreTesterReverseDependencySelectionFixture.Source };
        using var cacheScope = AcceptanceTestClassSourceCache.Use(new AcceptanceTestClassSourceCache(null));
        var expected = new ReverseDependencyEvidenceCostInputs(1, 1, skip ? 0 : 1);
        // The old, non-handoff path is the reference, also pinned to explicit input values.
        Assert.Equal(expected, ReverseDependencyEvidenceCostHistory.Read(fixture.Root, state, selected, changed));
        AcceptanceTestClassSourceCache.Current.Forget(fixture.Root);
        var handoff = new SelectionSourceTexts();
        handoff.Read(Path.Combine(fixture.Root, PreTesterReverseDependencySelectionFixture.TestRoot + "ConsumerTests.cs"));
        var actualInputs = ReverseDependencyEvidenceCostHistory.Read(fixture.Root, state, selected, changed, sourceTexts: handoff);
        Assert.Equal(expected, actualInputs);
        var decision = ReverseDependencyEvidenceCostRule.Evaluate(
            actualInputs.SelectionSeconds, actualInputs.FullGateSeconds, actualInputs.FailureRate);
        Assert.Equal(!skip, decision.Run);
        Assert.Equal($"REVERSE_DEPENDENCY_EVIDENCE_COST selection_seconds=1 full_gate_seconds=1 candidate_failure_rate={(skip ? "0" : "1")} decision={(skip ? "skip" : "run")}", decision.Line);

        var lines = new List<string>();
        var actual = PreTesterReverseDependencySelections.Select(fixture.Root, changed, lines.Add);
        if (skip)
        {
            Assert.Empty(actual.Selections);
            Assert.Empty(actual.Provenance);
            Assert.Equal("PRE_TESTER_GUARD_SELECTED class=none kind=reverse-dependency-skipped-cost " + decision.Line,
                Assert.Single(lines));
        }
        else
        {
            Assert.Equal(new FindingEvidenceSelection("Infrastructure.Tests", "ConsumerTests"), Assert.Single(actual.Selections));
            var provenance = Assert.Single(actual.Provenance);
            Assert.Equal(("ConsumerTests", "reverse-dependency", changed[0]),
                (provenance.Class, provenance.Kind, provenance.Path));
            Assert.Empty(lines);
        }
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("ambiguous")]
    [InlineData("missing")]
    [InlineData("cost")]
    public void SelectionMatchesEstablishedSelectionsProvenanceAndDiagnostics(string scenario)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        const string path = PreTesterReverseDependencySelectionFixture.Source;
        if (scenario == "ambiguous")
            fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "OtherConsumer.cs", """
                namespace Other { public class ConsumerTests {
                    [Fact] public void Works() { _ = new AppContract(); }
                } }
                """);
        if (scenario == "missing") File.Delete(Path.Combine(fixture.Root, path));
        var lines = new List<string>();

        var actual = PreTesterReverseDependencySelections.Select(fixture.Root, [path], lines.Add,
            costDecision: _ => scenario == "cost" ? ReverseDependencyEvidenceCostRule.Evaluate(50, 100, 0)
                : ReverseDependencyEvidenceCostRule.Evaluate(1, 100, 1));

        if (scenario == "resolved")
        {
            var selected = Assert.Single(actual.Selections);
            Assert.Equal("Infrastructure.Tests", selected.TestProject);
            Assert.Equal("ConsumerTests", selected.TestClass);
            var provenance = Assert.Single(actual.Provenance);
            Assert.Equal("ConsumerTests", provenance.Class);
            Assert.Equal("reverse-dependency", provenance.Kind);
            Assert.Equal(path, provenance.Path);
            Assert.Empty(lines);
        }
        else
        {
            Assert.Empty(actual.Selections);
            Assert.Empty(actual.Provenance);
            Assert.Equal(scenario switch
            {
                "ambiguous" => "PRE_TESTER_GUARD_SELECTED class=ConsumerTests kind=reverse-dependency-unresolved reason=class-match-not-unique path=" + path,
                "missing" => "PRE_TESTER_GUARD_SELECTED class=none kind=reverse-dependency-degraded reason=non-source-or-missing-path path=" + path,
                _ => "PRE_TESTER_GUARD_SELECTED class=none kind=reverse-dependency-skipped-cost REVERSE_DEPENDENCY_EVIDENCE_COST selection_seconds=50 full_gate_seconds=100 candidate_failure_rate=0 decision=skip"
            }, Assert.Single(lines));
        }
    }

    [Theory]
    [InlineData("inherited")]
    [InlineData("custom-fact")]
    [InlineData("abstract-name")]
    [InlineData("partial")]
    [InlineData("escaped")]
    public void ResolutionPreservesCrossFileAndIdentifierSemantics(string scenario)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var selected = "ConsumerTests";
        switch (scenario)
        {
            case "inherited":
                fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "ConsumerTests.cs",
                    "public class ConsumerTests : BaseTests {}");
                fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "BaseTests.cs",
                    "public abstract class BaseTests { [Fact] public void Works() {} }");
                break;
            case "custom-fact":
                fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "ConsumerTests.cs",
                    "public class ConsumerTests { [MyFact] public void Works() {} }");
                fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "MyFact.cs",
                    "public class MyFactAttribute : Xunit.FactAttribute {}");
                break;
            case "abstract-name":
                fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "Abstract.cs",
                    "namespace Other { public abstract class ConsumerTests { [Fact] public void Works() {} } }");
                break;
            case "partial":
                fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "ConsumerTests.cs",
                    "public partial class ConsumerTests {}");
                fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "Methods.cs",
                    "public partial class ConsumerTests { [Fact] public void Works() {} }");
                break;
            case "escaped":
                fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "ConsumerTests.cs",
                    @"public class Consum\u0065rTests { [Fact] public void Works() {} }");
                break;
        }
        var lines = new List<string>();

        var actual = PreTesterReverseDependencySelections.Select(fixture.Root,
            [PreTesterReverseDependencySelectionFixture.Source], lines.Add,
            (_, _) => new RepositoryRecheckTestSelection(true, [selected]),
            _ => ReverseDependencyEvidenceCostRule.Evaluate(1, 100, 1));

        Assert.Equal(selected, Assert.Single(actual.Selections).TestClass);
        Assert.Equal(selected, Assert.Single(actual.Provenance).Class);
        Assert.Empty(lines);
    }

    [Theory]
    [InlineData("public class Broken {")]
    [InlineData("[Collection(GetCollection())] public class UnrelatedTests { [Fact] public void Works() {} }")]
    public void UnrelatedInvalidSourceStillMakesResolutionUnresolved(string source)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        fixture.Write(PreTesterReverseDependencySelectionFixture.TestRoot + "Unrelated.cs", source);
        var lines = new List<string>();
        var actual = PreTesterReverseDependencySelections.Select(fixture.Root,
            [PreTesterReverseDependencySelectionFixture.Source], lines.Add,
            (_, _) => new RepositoryRecheckTestSelection(true, ["ConsumerTests"]),
            _ => throw new InvalidOperationException("Unresolved selection must not evaluate cost."));
        Assert.Empty(actual.Selections);
        Assert.Empty(actual.Provenance);
        Assert.Equal("PRE_TESTER_GUARD_SELECTED class=ConsumerTests kind=reverse-dependency-unresolved reason=class-match-not-unique path=" +
            PreTesterReverseDependencySelectionFixture.Source, Assert.Single(lines));
    }
}
