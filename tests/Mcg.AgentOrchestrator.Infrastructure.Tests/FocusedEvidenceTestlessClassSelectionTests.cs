using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

// Parallel safe: each case owns its source/TRX tree and stubs command execution.
public sealed class FocusedEvidenceTestlessClassSelectionTests : RemoteFirstFocusedEvidenceFixture
{
    private const string Reason = "declares-no-tests";
    private const string TestedSource = "public class AlphaTests { [Xunit.Fact] public void Runs() {} }";
    private const string SupportSource = "public static class SupportHelper { private struct Attributes { internal int Size; } }";

    [Fact]
    public void MixedPlan_SelectsOnlyTestedClassAndRecordsDrop()
    {
        using var fixture = new Fixture();
        fixture.Write(SupportSource);

        var checks = fixture.Plan("AlphaTests|SupportHelper", out var coverage);

        Assert.Equal("AlphaTests", Assert.Single(Assert.Single(checks).FocusedEvidenceTokens).Value);
        Assert.Single(coverage.TargetToChecks);
        AssertDrop(coverage);
        Assert.Contains("SupportHelper (declares-no-tests)", FocusedEvidenceTraitExcludedSelection.FormatDropped(coverage));
    }

    [Fact]
    public async Task MixedRun_SummaryCarriesTestlessDrop()
    {
        using var fixture = new Fixture();
        fixture.Write(SupportSource);

        var result = await fixture.Run("AlphaTests;SupportHelper");

        Assert.True(result.Passed, result.Summary);
        Assert.True(result.IsValidEvidence);
        AssertDrop(result.Coverage!);
        Assert.Contains("SupportHelper (declares-no-tests)", result.Summary);
        var call = Assert.Single(fixture.Calls.Where(args => args.Contains("--filter-class")));
        Assert.Equal("*AlphaTests*", call[Array.IndexOf(call, "--filter-class") + 1]);
        Assert.DoesNotContain(call, argument => argument.Contains("SupportHelper", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticSupportWithNestedStruct_DropsSelection()
    {
        using var fixture = new Fixture();
        fixture.Write(SupportSource);

        Assert.Empty(fixture.Plan("SupportHelper", out var coverage));
        AssertDrop(coverage);
    }

    [Theory]
    [InlineData("public class SupportHelper { [Xunit.Fact] public void A() {} }", "")]
    [InlineData("public class SupportHelper { [Xunit.Theory] public void A(int x) {} }", "")]
    [InlineData("public class SupportHelper : BaseTests { }", "")]
    [InlineData("public class SupportHelper : IDisposable { }", "")]
    [InlineData("public static class SupportHelper { public class Nested { [Xunit.Fact] public void A() {} } }", "")]
    [InlineData("public static class SupportHelper { public class Nested : BaseTests {} }", "")]
    [InlineData("public partial class SupportHelper { }", "public partial class SupportHelper { [Xunit.Fact] public void A() {} }")]
    [InlineData("public partial class SupportHelper { }", "public partial class SupportHelper : BaseTests { }")]
    [InlineData("class CustomFactAttribute : Xunit.FactAttribute {} public class SupportHelper { [CustomFact] public void A() {} }", "")]
    [InlineData("public partial class SupportHelper { }", "#if HIDDEN\npublic partial class SupportHelper { [Xunit.Fact] public void A() {} }\n#endif")]
    [InlineData("public class SupportHelper { }", "public class Broken {")]
    public void UnprovenOrTestedClass_RemainsSelected(string source, string otherPart)
    {
        using var fixture = new Fixture();
        fixture.Write("public class PlainHelper {}", "Plain.cs");
        Assert.Empty(fixture.Plan("PlainHelper", out var coverage));
        Assert.Equal(Reason, Assert.Single(Assert.Single(coverage.DroppedSelections!).Reasons));
        fixture.Write(source);
        if (otherPart.Length > 0) fixture.Write(otherPart, "Other.cs");

        AssertRetained(fixture, "SupportHelper");
    }

    [Fact]
    public void ProjectWithoutRecognisedTests_RemainsSelected()
    {
        using var fixture = new Fixture();
        fixture.Write("", "Known.cs");
        fixture.Write(SupportSource);
        AssertRetained(fixture, "SupportHelper");
    }

    [Theory]
    [InlineData("Alpha", "public class Alpha {}")]
    [InlineData("Runs", "public class Runs {}")]
    [InlineData("SupportHelper", "public class SupportHelper {} public class SupportHelperTests { [Xunit.Fact] public void A() {} }")]
    [InlineData("SupportHelper", "public class SupportHelper {} public class SupportHelperDerived : BaseTests {}")]
    public void SubstringCollision_RetainsSelector(string selector, string source)
    {
        using var fixture = new Fixture();
        fixture.Write(source);
        AssertRetained(fixture, selector);
    }

    [Fact]
    public void SubstringOnlyTestlessDeclaration_RetainsOriginalSelector()
    {
        using var fixture = new Fixture();
        fixture.Write("public static class SupportHelperHarness {}");

        AssertRetained(fixture, "SupportHelper");
    }

    [Fact]
    public void QualifiedPartialClass_DropsOnlyWhenEveryPartIsTestless()
    {
        using var fixture = new Fixture();
        fixture.Write("namespace Sample { public partial class SupportHelper {} }");
        fixture.Write("namespace Sample { public partial class SupportHelper { public void Help() {} } }", "Other.cs");
        Assert.Empty(fixture.Plan("Sample.SupportHelper", out var coverage));
        Assert.Equal("Sample.SupportHelper", Assert.Single(coverage.DroppedSelections!).Selection);

        fixture.Write("namespace Sample { public partial class SupportHelper { [Xunit.Fact] public void A() {} } }", "Other.cs");
        AssertRetained(fixture, "Sample.SupportHelper");
    }

    [Fact]
    public async Task OnlyTestlessClass_ReturnsNoRunnableSelectionWithoutLaunch()
    {
        using var fixture = new Fixture();
        fixture.Write(SupportSource);

        var result = await fixture.Run("SupportHelper");

        Assert.True(result.Accepted);
        Assert.False(result.Passed);
        Assert.False(result.IsValidEvidence);
        Assert.Empty(result.Checks);
        Assert.Empty(fixture.Calls);
        Assert.Equal(FindingEvidenceOutcomeReason.CandidateInconclusive, result.OutcomeReason);
        Assert.Equal("no-runnable-selection", result.Coverage!.ExecutionMode);
        AssertDrop(result.Coverage);
        Assert.Contains("no runnable selection", result.Summary);
        Assert.Contains("SupportHelper (declares-no-tests)", result.Summary);
    }

    [Fact]
    public async Task TestedClassMatchingZeroTests_RemainsApparatusFailure()
    {
        using var fixture = new Fixture();
        var result = await fixture.Run("AlphaTests", executed: false);

        Assert.True(result.Accepted);
        Assert.False(result.Passed);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, result.OutcomeReason);
        Assert.Null(result.Coverage!.DroppedSelections);
        var failure = Assert.Single(result.Checks.Where(check =>
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionApparatusFailure));
        Assert.Contains("executed 0 tests", failure.OutputTail);
        Assert.Single(fixture.Calls.Where(args => args.Contains("--filter-class")));
    }

    [Theory]
    [InlineData("NotExecuted")]
    [InlineData("Skipped")]
    public void MixedReceipt_SkippedFactCannotSupplyExecutionCoverage(string skippedOutcome)
    {
        using var fixture = new Fixture();
        fixture.Write("""
            public class SkippedTests { [Xunit.Fact(Skip = "deliberate zero execution")] public void DoesNotRun() {} }
            public class FailedTests { [Xunit.Fact] public void Fails() {} }
            """);
        var check = Assert.Single(fixture.Plan("AlphaTests;SkippedTests;FailedTests", out var planCoverage));
        Assert.Null(planCoverage.DroppedSelections);
        Assert.Equal(3, check.FocusedEvidenceTokens.Count);
        var receipt = Path.Combine(fixture.Root, "mixed.trx");
        File.WriteAllText(receipt, $$"""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="passed" testName="AlphaTests.Runs" outcome="Passed" />
                <UnitTestResult testId="failed" testName="FailedTests.Fails" outcome="Failed" />
                <UnitTestResult testId="skipped" testName="SkippedTests.DoesNotRun" outcome="{{skippedOutcome}}" />
              </Results>
              <ResultSummary outcome="Failed">
                <Counters total="3" executed="2" passed="1" failed="1" notExecuted="1" />
              </ResultSummary>
            </TestRun>
            """);

        var execution = GoalAcceptanceVerifierTestTelemetry.ExtractTrxExecutedTestIdentities([receipt]);
        Assert.Empty(execution.UnreadableReceiptPaths);
        Assert.Equal(new[] { "AlphaTests.Runs", "FailedTests.Fails" }, execution.Identities);
        var coverage = GoalAcceptanceVerifierTestTelemetry.InspectFocusedEvidenceSelectionCoverage(
            check.FocusedEvidenceSelections, [receipt]);
        Assert.Empty(coverage.UnreadableReceiptPaths);
        Assert.Equal("FullyQualifiedName~SkippedTests", Assert.Single(coverage.UncoveredSelections));
    }

    [Fact]
    public void TraitAndTestlessDrops_PreserveBothReasonsInOrder()
    {
        using var fixture = new Fixture();
        fixture.Write(SupportSource + " public class HostTests { [Xunit.Fact, Xunit.Trait(\"Category\",\"HostIntegration\")] public void A() {} }");
        var checks = fixture.Plan("HostTests;SupportHelper;AlphaTests", out var coverage);
        Assert.Single(checks);
        Assert.Equal(new[] { "HostTests", "SupportHelper" }, coverage.DroppedSelections!.Select(drop => drop.Selection));
        Assert.Equal(new[] { "excluded-by-trait:Category=HostIntegration", Reason },
            coverage.DroppedSelections.Select(drop => Assert.Single(drop.Reasons)));
    }

    [Fact]
    public void MixedMethodArmAndNegativeTokens_RemainUnchanged()
    {
        using var fixture = new Fixture();
        fixture.Write(SupportSource);
        FocusedEvidenceFilterToken[] tokens =
        [
            new("SupportHelper", "SupportHelper", FocusedEvidenceTokenKind.Class, "SupportHelper", "SupportHelper"),
            new("Runs", "Runs", FocusedEvidenceTokenKind.Method, "Runs", "AlphaTests"),
            new("Alpha", "Alpha", FocusedEvidenceTokenKind.ExcludedClass, "Alpha", "Alpha"),
            new("Local", "Local", FocusedEvidenceTokenKind.ExcludedTrait, "Local", "")
        ];
        var filter = new FocusedEvidenceFilter("request", "request", tokens);
        var result = FocusedEvidenceTestlessClassSelection.Apply(fixture.Root,
            [("target", Project, filter)], [], out var dropped);
        Assert.Same(filter, Assert.Single(result).Filter);
        Assert.Empty(dropped);

        filter = filter with { Tokens = tokens.Skip(2).ToArray() };
        result = FocusedEvidenceTestlessClassSelection.Apply(fixture.Root,
            [("target", Project, filter)], [], out dropped);
        Assert.Same(filter, Assert.Single(result).Filter);
        Assert.Empty(dropped);
    }

    [Fact]
    public void TraitExcludedCollision_StillPreventsTestlessProof()
    {
        using var fixture = new Fixture();
        fixture.Write(SupportSource + " public class SupportHelperHost { [Xunit.Fact, Xunit.Trait(\"Category\",\"HostIntegration\")] public void A() {} }");
        var token = new FocusedEvidenceFilterToken("SupportHelper", "SupportHelper",
            FocusedEvidenceTokenKind.Class, "SupportHelper", "SupportHelper");
        var filter = new FocusedEvidenceFilter("SupportHelper", "SupportHelper", [token]);
        var result = FocusedEvidenceTestlessClassSelection.Apply(fixture.Root,
            [("target", Project, filter)], [], out var dropped);
        Assert.Same(filter, Assert.Single(result).Filter);
        Assert.Empty(dropped);
    }

    [Fact]
    public void MissingSource_CannotAuthorizeDrop()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Root, Path.GetDirectoryName(Project)!, "Known.cs"));
        AssertRetained(fixture, "SupportHelper");
    }

    [Fact]
    public void PersistedFacts_SecondApplyMatchesFirstWithoutParsing()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".orchestrator"));
        fixture.Write(SupportSource);
        var filter = ClassFilter("AlphaTests", "SupportHelper");
        using var parses = FocusedEvidenceParsedRootCache.Observe(fixture.Root);

        var first = Assert.Single(ApplyWithColdStores(out var firstDrops));
        Assert.Equal("AlphaTests", Assert.Single(first.Filter!.Tokens).Value);
        Assert.Equal("SupportHelper", Assert.Single(firstDrops).Selection);
        Assert.Equal(Reason, Assert.Single(firstDrops[0].Reasons));
        var sources = AssertPersistedSources(fixture);
        Assert.Equal(2, sources.Length);
        foreach (var source in sources)
            Assert.Equal(1, parses.CountFor(source));

        // RecordParse also increments ParseCount. Scoping the observation excludes other tests' parses.
        var beforeSecondApply = parses.Total;
        var second = Assert.Single(ApplyWithColdStores(out var secondDrops));
        Assert.Equal(beforeSecondApply, parses.Total);
        Assert.Equal((first.Target, first.Project), (second.Target, second.Project));
        Assert.NotNull(second.Filter);
        Assert.Equal((first.Filter.OriginalToken, first.Filter.CanonicalText),
            (second.Filter.OriginalToken, second.Filter.CanonicalText));
        Assert.Equal(first.Filter.Tokens.ToArray(), second.Filter.Tokens.ToArray());
        var firstDrop = Assert.Single(firstDrops);
        var secondDrop = Assert.Single(secondDrops);
        Assert.Equal((firstDrop.Target, firstDrop.Project, firstDrop.Selection),
            (secondDrop.Target, secondDrop.Project, secondDrop.Selection));
        Assert.Equal(firstDrop.Reasons.ToArray(), secondDrop.Reasons.ToArray());

        List<(string Target, string Project, FocusedEvidenceFilter? Filter)> ApplyWithColdStores(
            out IReadOnlyList<FocusedEvidenceDroppedSelection> dropped)
        {
            using var store = UseColdStores();
            return FocusedEvidenceTestlessClassSelection.Apply(fixture.Root, [("target", Project, filter)], [], out dropped);
        }
    }

    [Theory]
    [InlineData(TestedSource, SupportSource, "class Broken {", true)]
    [InlineData("", SupportSource, "", true)]
    [InlineData(TestedSource, "class SupportHelperHarness {}", "", true)]
    [InlineData(TestedSource, SupportSource, "class SupportHelperTests { [Fact] void Runs() {} }", true)]
    [InlineData(TestedSource, SupportSource, "#if HIDDEN\nclass SupportHelper {}\n#endif", true)]
    [InlineData(TestedSource, "#if HIDDEN\n#endif\nclass SupportH\\u0065lper {}", "", true)]
    [InlineData(TestedSource, "class SupportHelper : Base {}", "", true)]
    [InlineData(TestedSource, "class SupportHelper { [CustomFact] void Help() {} }", "", true)]
    [InlineData(TestedSource, "class SupportHelper { [Other.CustomTheoryAttribute] void Help() {} }", "", true)]
    [InlineData(TestedSource, SupportSource, "#if HIDDEN\nclass Unrelated {}\n#endif", false)]
    public void DecisionRule_PreservesPreChangeOutcome(string known, string selected, string other, bool retained)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".orchestrator"));
        fixture.Write(SupportSource);
        AssertSelection(retained: false);

        fixture.Write(known, "Known.cs");
        fixture.Write(selected);
        fixture.Write(other, "Other.cs");
        AssertSelection(retained);
        Assert.Equal(3, AssertPersistedSources(fixture).Length);
        using var parses = FocusedEvidenceParsedRootCache.Observe(fixture.Root);
        AssertSelection(retained);
        Assert.Equal(0, parses.Total);

        void AssertSelection(bool retained)
        {
            using var store = UseColdStores();
            var filter = ClassFilter("SupportHelper");
            var result = FocusedEvidenceTestlessClassSelection.Apply(fixture.Root,
                [("target", Project, filter)], [], out var dropped);
            if (retained)
            {
                Assert.Same(filter, Assert.Single(result).Filter);
                Assert.Empty(dropped);
            }
            else
            {
                Assert.Empty(result);
                var drop = Assert.Single(dropped);
                Assert.Equal(("target", Project, "SupportHelper"), (drop.Target, drop.Project, drop.Selection));
                Assert.Equal(Reason, Assert.Single(drop.Reasons));
            }
        }
    }

    private static string[] AssertPersistedSources(Fixture fixture)
    {
        var directory = Path.Combine(fixture.Root, ".orchestrator", FocusedEvidenceDeclarationFactStore.DirectoryName);
        var sources = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(fixture.Root, Project).ToArray();
        foreach (var source in sources)
        {
            var hash = AcceptanceTestClassSourceExtractStore.HashText(File.ReadAllText(source));
            Assert.True(File.Exists(FocusedEvidenceDeclarationFactStore.EntryPath(directory, hash)), source);
        }
        return sources;
    }

    private static FocusedEvidenceFilter ClassFilter(params string[] selectors)
    {
        var request = string.Join('|', selectors);
        return new(request, request, selectors.Select(selector =>
            new FocusedEvidenceFilterToken(selector, selector, FocusedEvidenceTokenKind.Class, selector, selector)).ToArray());
    }

    private static IDisposable UseColdStores() => FocusedEvidenceDeclarationFactStore.Use(
        new FocusedEvidenceDeclarationFactStore(new FocusedEvidenceParsedRootCache.Store(32)));

    private static void AssertRetained(Fixture fixture, string selector)
    {
        var checks = fixture.Plan(selector, out var coverage);
        Assert.Equal(selector, Assert.Single(Assert.Single(checks).FocusedEvidenceTokens).Value);
        Assert.Null(coverage.DroppedSelections);
    }

    private static void AssertDrop(FocusedEvidenceCoverage coverage)
    {
        var drop = Assert.Single(coverage.DroppedSelections!);
        Assert.Equal(Project, drop.Project);
        Assert.Equal("SupportHelper", drop.Selection);
        Assert.Equal(Reason, Assert.Single(drop.Reasons));
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateManifestWorkspace("""
            {"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}
            """);
        internal List<string[]> Calls { get; } = [];
        private readonly GoalId _goal = new(Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, Project))!);
            File.WriteAllText(Path.Combine(Root, Project), "<Project />");
            Write(TestedSource, "Known.cs");
        }

        internal void Write(string source, string name = "Tests.cs") =>
            File.WriteAllText(Path.Combine(Root, Path.GetDirectoryName(Project)!, name), source);

        internal IReadOnlyList<AcceptanceManifestCheck> Plan(string request, out FocusedEvidenceCoverage coverage)
        {
            Assert.True(TryBuildFocusedEvidenceChecks("Infrastructure.Tests: " + request,
                new AcceptanceGateEngineSettings(), Root, out var checks, out coverage, out var rejection), rejection.Detail);
            return checks;
        }

        internal Task<FocusedEvidenceRunResult> Run(string request, bool executed = true)
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _, _) =>
            {
                Calls.Add(args);
                if (args.Contains("--report-trx-filename"))
                    WriteTrx(args, executed);
                return Task.FromResult(new CommandResult(0, "fixture command completed"));
            });
            return verifier.RunFocusedEvidenceAsync(Root, _goal, "Infrastructure.Tests: " + request);
        }

        private static void WriteTrx(string[] args, bool executed)
        {
            var directory = args[Array.IndexOf(args, "--results-directory") + 1];
            var filename = args[Array.IndexOf(args, "--report-trx-filename") + 1];
            Directory.CreateDirectory(directory);
            XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(trx + "TestRun",
                new XElement(trx + "TestDefinitions", executed ? new XElement(trx + "UnitTest", new XAttribute("id", "one"), new XAttribute("name", "AlphaTests.Runs"),
                    new XElement(trx + "TestMethod", new XAttribute("className", "AlphaTests"), new XAttribute("name", "Runs"))) : null),
                new XElement(trx + "Results", executed ? new XElement(trx + "UnitTestResult", new XAttribute("testId", "one"),
                    new XAttribute("testName", "AlphaTests.Runs"), new XAttribute("outcome", "Passed")) : null),
                new XElement(trx + "ResultSummary", new XAttribute("outcome", "Completed"), new XElement(trx + "Counters",
                    new XAttribute("total", executed ? 1 : 0), new XAttribute("executed", executed ? 1 : 0),
                    new XAttribute("passed", executed ? 1 : 0), new XAttribute("failed", 0), new XAttribute("notExecuted", 0)))))
                .Save(Path.Combine(directory, filename));
        }

        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(_goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
