using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

// Each scenario owns its source/TRX tree and injected runners. The collection supplies isolated build roots.
[Collection("IsolatedProcessSpawningRemainderC")]
public sealed class FocusedEvidenceTraitExcludedSelectionTests : RemoteFirstFocusedEvidenceFixture
{
    private const string Reason = "excluded-by-trait:Category=HostIntegration";
    private const string HostSource = """
        internal sealed class OptInFactAttribute : Xunit.FactAttribute { }
        public class HostTests
        {
            [OptInFact, Xunit.Trait("Category", "HostIntegration")]
            public void Connects() { }
            [Xunit.Theory, Xunit.InlineData(1), Xunit.Trait("Category", "HostIntegration")]
            public void Sends(int value) { }
        }
        """;

    [Fact]
    public async Task AllExcluded_ReturnsNoRunnableSelectionWithoutLaunching()
    {
        using var fixture = new Fixture();
        fixture.Write(HostSource);
        var result = await fixture.Run("HostTests");

        Assert.True(result.Accepted, result.Summary);
        Assert.False(result.Passed);
        Assert.False(result.IsValidEvidence);
        Assert.Empty(result.Checks);
        Assert.Null(result.Arms);
        Assert.Equal(FindingEvidenceOutcomeReason.CandidateInconclusive, result.OutcomeReason);
        Assert.Empty(fixture.Calls);
        Assert.Equal("no-runnable-selection", result.Coverage!.ExecutionMode);
        AssertDrop(result.Coverage);
        Assert.Contains("no runnable selection", result.Summary);
        Assert.Contains(Reason, result.Summary);
    }

    [Fact]
    public async Task MixedRequest_RunsOnlyOrdinaryClassAndClassifiesGreen()
    {
        using var fixture = new Fixture();
        fixture.Write(HostSource + " public class AlphaTests { [Xunit.Fact] public void Runs() { } }");
        var result = await fixture.Run("HostTests;AlphaTests");

        Assert.True(result.Passed, result.Summary);
        Assert.True(result.IsValidEvidence);
        AssertDrop(result.Coverage!);
        var check = Assert.Single(result.Checks.Where(check => check.ExecutedTestCount is > 0));
        Assert.Equal(1, check.ExecutedTestCount);
        var call = Assert.Single(fixture.Calls.Where(args => args.Contains("--filter-class")));
        Assert.Equal("*AlphaTests*", call[Array.IndexOf(call, "--filter-class") + 1]);
        Assert.DoesNotContain(call, argument => argument.Contains("HostTests", StringComparison.Ordinal));
        Assert.Contains(Reason, result.Summary);

        var candidate = Assert.Single(result.Arms!);
        var classification = PreTesterApparatusReceiptClassifier.ClassifyOutcome(result, true, candidate.Sha,
            [new("Infrastructure.Tests", "HostTests"), new("Infrastructure.Tests", "AlphaTests")], _ => false);
        Assert.Equal("green", classification.Outcome);
        Assert.Null(classification.Unusable);
        var withoutDrop = PreTesterApparatusReceiptClassifier.ClassifyOutcome(result with { Coverage = null },
            true, candidate.Sha, [new("Infrastructure.Tests", "HostTests"), new("Infrastructure.Tests", "AlphaTests")], _ => false);
        Assert.Equal("required-class-missing", withoutDrop.Unusable!.Check);
    }

    [Fact]
    public async Task MisspelledClassWithoutSource_RemainsZeroMatchApparatusFailure()
    {
        using var fixture = new Fixture();
        var result = await fixture.Run("MisspeledTests", executed: false);

        Assert.True(result.Accepted);
        Assert.False(result.Passed);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, result.OutcomeReason);
        Assert.Null(result.Coverage!.DroppedSelections);
        var failure = Assert.Single(result.Checks.Where(check =>
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionApparatusFailure));
        Assert.Contains("executed 0 tests", failure.OutputTail);
        Assert.Single(fixture.Calls.Where(args => args.Contains("--filter-class")));
    }

    [Fact]
    public async Task RemoteFirst_UsesSamePrunedSelectionAndAcceptsRemoteReceipt()
    {
        using var fixture = new Fixture();
        fixture.Write(HostSource + " public class AlphaTests { [Xunit.Fact] public void Runs() { } }");
        var checks = fixture.Plan("HostTests|AlphaTests", out var coverage);
        AssertDrop(coverage);
        var selection = Assert.Single(Assert.Single(checks).FocusedEvidenceSelections);
        Assert.Equal("AlphaTests", Assert.Single(selection).Value);
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor();

        var outcome = await scenario.DirectAsync(checks);

        var request = Assert.Single(fake.Requests);
        Assert.Equal("FullyQualifiedName~AlphaTests", request.Filter);
        var result = Assert.Single(outcome.Results);
        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal("remote-executor=fixture-executor", result.ResultSummary);
        Assert.Equal(3, result.ExecutedTestCount);
        Assert.Equal(0, scenario.LocalCalls);
    }

    [Theory]
    [InlineData("public class HostTests { [Xunit.Fact, Xunit.Trait(\"Category\",\"HostIntegration\")] public void A() {} [Xunit.Fact] public void B() {} }")]
    [InlineData("public class HostTests { const string Value=\"HostIntegration\"; [Xunit.Fact, Xunit.Trait(\"Category\",Value)] public void A() {} }")]
    [InlineData("public class HostTests { [UnknownFact, Xunit.Trait(\"Category\",\"HostIntegration\")] public void A() {} }")]
    [InlineData("public class HostTests : BaseTests { [Xunit.Fact, Xunit.Trait(\"Category\",\"HostIntegration\")] public void A() {} }")]
    [InlineData("public abstract class HostTests { [Xunit.Fact, Xunit.Trait(\"Category\",\"HostIntegration\")] public void A() {} }")]
    [InlineData("public class HostTests<T> { [Xunit.Fact, Xunit.Trait(\"Category\",\"HostIntegration\")] public void A() {} }")]
    [InlineData("public class HostTests { }")]
    public void IncompleteOrRunnableDiscovery_RetainsClass(string source)
    {
        using var fixture = new Fixture();
        fixture.Write(source);
        var checks = fixture.Plan("HostTests", out var coverage);
        Assert.Equal("HostTests", Assert.Single(Assert.Single(checks).FocusedEvidenceTokens).Value);
        Assert.Null(coverage.DroppedSelections);
    }

    [Fact]
    public void SubstringAndPartialClasses_RequireEveryMatchingTestExcluded()
    {
        using var fixture = new Fixture();
        fixture.Write("""
            public partial class HostTests { [Xunit.Fact, Xunit.Trait("Category","HostIntegration")] public void A() {} }
            public class HostTestsExtra { [Xunit.Fact] public void B() {} }
            """);
        Assert.Single(fixture.Plan("HostTests", out var coverage));
        Assert.Null(coverage.DroppedSelections);
        fixture.Write("public partial class HostTests { [Xunit.Fact] public void C() {} }", "More.cs");
        Assert.Single(fixture.Plan("HostTests&FullyQualifiedName!~HostTestsExtra", out coverage));
        Assert.Null(coverage.DroppedSelections);
    }

    [Fact]
    public async Task NonTraitFilterMatchingNothing_RemainsZeroMatchFailure()
    {
        using var fixture = new Fixture();
        fixture.Write(HostSource);
        var result = await fixture.Run("HostTests&FullyQualifiedName!~HostTests", executed: false);
        Assert.Null(result.Coverage!.DroppedSelections);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, result.OutcomeReason);
        Assert.Contains(result.Checks, check =>
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionApparatusFailure);
    }

    [Fact]
    public void ActiveExclusions_RecordAllContributingReasons()
    {
        using var fixture = new Fixture();
        fixture.Write("""
            public class HostTests
            {
                [Xunit.Fact, Xunit.Trait("Category","HostIntegration")] public void A() {}
                [Xunit.Fact, Xunit.Trait("Category","Other")] public void B() {}
            }
            """);
        Assert.Empty(fixture.Plan("HostTests&Category!=Other", out var coverage));
        Assert.Equal(new[] { Reason, "excluded-by-trait:Category=Other" },
            Assert.Single(coverage.DroppedSelections!).Reasons);
    }

    [Fact]
    public void ClassTraitsAndGlobalUsings_AuthorizePositiveDiscovery()
    {
        using var fixture = new Fixture();
        fixture.Write("global using Xunit;", "Usings.cs");
        fixture.Write("[Trait(\"Category\",\"HostIntegration\")] public class HostTests { [Fact] public void A() {} }");
        Assert.Empty(fixture.Plan("HostTests", out var coverage));
        AssertDrop(coverage);
    }

    [Fact]
    public void VstestWithoutImplicitExclusion_RetainsHostClass()
    {
        using var fixture = new Fixture();
        fixture.Write(HostSource);
        var settings = new AcceptanceGateEngineSettings();
        File.WriteAllText(Path.Combine(fixture.Root, Project), "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
        Assert.True(TryBuildFocusedEvidenceChecks("Infrastructure.Tests: HostTests", settings, fixture.Root,
            out var checks, out var coverage, out var rejection), rejection.Detail);
        Assert.Single(checks);
        Assert.Null(coverage.DroppedSelections);
    }

    private static void AssertDrop(FocusedEvidenceCoverage coverage)
    {
        var drop = Assert.Single(coverage.DroppedSelections!);
        Assert.Equal(Project, drop.Project);
        Assert.Equal("HostTests", drop.Selection);
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
