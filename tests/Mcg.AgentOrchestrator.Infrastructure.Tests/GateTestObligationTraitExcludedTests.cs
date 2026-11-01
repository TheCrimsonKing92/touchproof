using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repositories and receipts; console capture is AsyncLocal.
// These tests never replace the global landing git runner or change process environment.
public sealed class GateTestObligationTraitExcludedTests
{
    private const string Reason = "excluded-by-trait:Category=HostIntegration";
    private const string HostSource = """
        public sealed class HostTests
        {
            [Xunit.Fact, Xunit.Trait("Category", "HostIntegration")]
            public void First() { }
            [Xunit.Fact, Xunit.Trait("Category", "HostIntegration")]
            public void Second() { }
        }
        """;

    [Fact]
    public void HostOnlySelector_RecordsExclusionAndReleasesLandingHold()
    {
        using var fixture = new Fixture(["HostTests"]);
        fixture.WriteAttempt();
        fixture.AssertBaselineHold();

        fixture.BindExecution();
        var result = fixture.Evaluate();

        Assert.Equal(GateTestObligationEvaluator.Outcome.TraitExcluded, result.Outcome);
        AssertExclusion(fixture, result);
        fixture.AssertLanding(result, []);
    }

    [Fact]
    public void HostAndPassedSelectors_ReleaseLandingHold()
    {
        using var fixture = new Fixture(["HostTests", "OrdinaryTests"]);
        fixture.WriteAttempt([("OrdinaryTests.Check", "Passed")]);
        fixture.AssertBaselineHold();

        fixture.BindExecution();
        var result = fixture.Evaluate();

        Assert.Equal(GateTestObligationEvaluator.Outcome.TraitExcluded, result.Outcome);
        AssertExclusion(fixture, result);
        fixture.AssertLanding(result, []);
    }

    [Fact]
    public void HostAndMisspelledSelectors_KeepOnlyMisspelledLandingHold()
    {
        using var fixture = new Fixture(["HostTests", "MisspeltTests"]);
        fixture.WriteAttempt();
        fixture.AssertBaselineHold("HostTests", "MisspeltTests");

        fixture.BindExecution();
        var result = fixture.Evaluate();

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { "MisspeltTests" }, result.Selectors);
        AssertExclusion(fixture, result);
        fixture.AssertLanding(result, ["MisspeltTests"]);
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("inherited")]
    [InlineData("syntax-error")]
    [InlineData("no-tests")]
    public void UnprovenSource_PreservesSelectorAndLandingHold(string scenario)
    {
        var hostSource = HostSource.Replace("\r\n", "\n", StringComparison.Ordinal);
        var source = scenario switch
        {
            "mixed" => hostSource.Replace(
                "[Xunit.Fact, Xunit.Trait(\"Category\", \"HostIntegration\")]\n    public void Second()",
                "[Xunit.Fact]\n    public void Second()", StringComparison.Ordinal),
            "inherited" => hostSource.Replace("class HostTests", "class HostTests : BaseTests",
                StringComparison.Ordinal) + "\npublic class BaseTests { }",
            "syntax-error" => hostSource + "\npublic class Broken {",
            _ => "[Xunit.Trait(\"Category\", \"HostIntegration\")] public class HostTests { }"
        };
        // Pin the mixed fixture's ordinary test so an ineffective replacement cannot green it.
        if (scenario == "mixed") Assert.Contains("[Xunit.Fact]\n    public void Second()", source);
        using var fixture = new Fixture(["HostTests"], source);
        fixture.WriteAttempt();
        fixture.AssertBaselineHold();

        fixture.BindExecution();
        var result = fixture.Evaluate();

        AssertUnexcludedHold(result, "HostTests");
        fixture.AssertLanding(result, ["HostTests"]);
    }

    [Theory]
    [InlineData("missing-execution")]
    [InlineData("missing-manifest")]
    [InlineData("different-candidate")]
    [InlineData("vstest-filter")]
    public void UnknownAppliedExclusion_PreservesHold(string scenario)
    {
        using var fixture = new Fixture(["HostTests"]);
        fixture.WriteAttempt();
        fixture.AssertBaselineHold();
        if (scenario != "missing-execution") fixture.BindExecution();
        if (scenario == "missing-manifest") File.Delete(fixture.ManifestPath);
        if (scenario == "vstest-filter") fixture.WriteManifest("vstest", ["--filter", "FullyQualifiedName~OtherTests"]);
        if (scenario == "different-candidate")
        {
            TestClassRenameEvidenceTests.Git(fixture.WorktreeRoot, "commit", "--allow-empty", "-m", "Different candidate");
            Assert.NotEqual(fixture.Candidate, TestClassRenameEvidenceTests.Git(fixture.WorktreeRoot, "rev-parse", "HEAD"));
        }

        AssertUnexcludedHold(fixture.Evaluate(), "HostTests");
    }

    [Fact]
    public void MethodSelector_NullClassProofPreservesHold()
    {
        using var fixture = new Fixture(["HostTests.First"]);
        fixture.WriteAttempt();
        fixture.AssertBaselineHold("HostTests.First");
        fixture.BindExecution();

        AssertUnexcludedHold(fixture.Evaluate(), "HostTests.First");
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("NotExecuted")]
    public void UnsuccessfulOtherSelector_NeverReleasesLandingHold(string outcome)
    {
        using var fixture = new Fixture(["HostTests", "OrdinaryTests"]);
        fixture.WriteAttempt([("OrdinaryTests.Check", outcome)]);
        var baseline = fixture.Evaluate();
        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, baseline.Outcome);
        fixture.BindExecution();

        var result = fixture.Evaluate();

        Assert.Equal(baseline.Outcome, result.Outcome);
        Assert.Equal(new[] { "OrdinaryTests" }, result.Selectors);
        Assert.Empty(result.TraitExclusions ?? []);
        fixture.AssertLanding(result, ["OrdinaryTests"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PassedCheckAlongsideHostSelector_RegradesRemainingSelectors(bool misspelled)
    {
        using var fixture = new Fixture(misspelled
            ? ["HostTests", "policy check", "MisspeltTests"] : ["HostTests", "policy check"]);
        fixture.WriteAttempt();
        fixture.BindExecution();
        fixture.WriteManifest(includePendingCheck: true);
        fixture.WriteCheckOutcome(true);

        var result = fixture.Evaluate();

        Assert.Equal(misspelled ? GateTestObligationEvaluator.Outcome.SelectorNotFound
            : GateTestObligationEvaluator.Outcome.TraitExcluded, result.Outcome);
        if (misspelled) Assert.Equal(new[] { "MisspeltTests" }, result.Selectors);
        AssertExclusion(fixture, result);
        fixture.AssertLanding(result, misspelled ? ["MisspeltTests"] : []);
    }

    [Theory]
    [InlineData("absent", true)]
    [InlineData("excluded", true)]
    [InlineData("ordinary", false)]
    [InlineData("unproven", false)]
    [InlineData("unfiltered", false)]
    public void MultipleProjects_RequireProofOnlyFromEveryMatchingProject(string scenario, bool excluded)
    {
        var otherSource = scenario switch
        {
            "absent" => "public class OtherTests { [Xunit.Fact] public void Check() { } }",
            "ordinary" => "namespace Other; public class HostTests { [Xunit.Fact] public void Check() { } }",
            "unproven" => "namespace Other; " + HostSource.Replace("class HostTests",
                "class HostTests : BaseTests", StringComparison.Ordinal) + " public class BaseTests { }",
            _ => "namespace Other; " + HostSource
        };
        using var fixture = new Fixture(["HostTests"], additionalProjects:
        [
            ("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Other/Mcg.AgentOrchestrator.Infrastructure.Other.Tests.csproj",
                scenario == "unfiltered" ? "public class OtherTests { }" : otherSource),
            (scenario == "unfiltered"
                ? "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"
                : "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Empty/Mcg.AgentOrchestrator.Infrastructure.Empty.Tests.csproj",
                scenario == "unfiltered" ? otherSource : "public class UnrelatedTests { }")
        ]);
        fixture.WriteAttempt();
        fixture.AssertBaselineHold();
        fixture.BindExecution();

        var result = fixture.Evaluate();

        if (excluded)
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.TraitExcluded, result.Outcome);
            AssertExclusion(fixture, result);
        }
        else AssertUnexcludedHold(result, "HostTests");
        fixture.AssertLanding(result, excluded ? [] : ["HostTests"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingCheckAlongsideHostSelector_PreservesLandingHold(bool misspelled)
    {
        using var fixture = new Fixture(misspelled
            ? ["HostTests", "policy check", "MisspeltTests"] : ["HostTests", "policy check"]);
        fixture.WriteAttempt();
        fixture.BindExecution();
        fixture.WriteManifest(includePendingCheck: true);

        var result = fixture.Evaluate();

        Assert.Equal(misspelled ? GateTestObligationEvaluator.Outcome.SelectorNotFound
            : GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
        var unresolved = misspelled ? new[] { "MisspeltTests" } : new[] { "policy check" };
        Assert.Equal(unresolved, result.Selectors);
        AssertExclusion(fixture, result);
        fixture.AssertLanding(result, unresolved);
    }

    private static void AssertExclusion(Fixture fixture, GateTestObligationEvaluator.Result result)
    {
        var exclusion = Assert.Single(result.TraitExclusions!);
        Assert.Equal("HostTests", exclusion.Selector);
        Assert.Equal(new[] { Reason }, exclusion.Reasons);
        Assert.Equal(fixture.AttemptId, result.SourceId);
        Assert.Equal($"GATE_TEST_OBLIGATION_TRAIT_EXCLUDED id={Assert.Single(fixture.Goal.GateTestObligations).Id[..8]} " +
            $"selector=HostTests reason={Reason} source={fixture.AttemptId}", result.TraitExclusionProvenance);
    }

    private static void AssertUnexcludedHold(GateTestObligationEvaluator.Result result, string selector)
    {
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { selector }, result.Selectors);
        Assert.Empty(result.TraitExclusions ?? []);
        Assert.Equal("", result.TraitExclusionProvenance);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        private readonly ProspectiveEvidenceGateFixture _evidence = new();
        private readonly List<string> _projects = [Project];
        private string _metadataPath = "";
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal string WorktreeRoot { get; }
        internal string Candidate { get; }
        internal string AttemptId { get; private set; } = "";
        internal string ManifestPath => Path.Combine(_evidence.Root, "config", "acceptance-manifest.json");

        internal Fixture(string[] selectors, string? source = null,
            (string Project, string Source)[]? additionalProjects = null)
        {
            Kernel = GateTestObligationIntakeTests.Seed(_evidence.Kernel, _evidence.Goal, selectors);
            Goal = Kernel.GetGoal(_evidence.Goal.Id);
            WorktreeRoot = GoalWorktreeLayout.WorktreePath(_evidence.Root, Goal.Id);
            Directory.CreateDirectory(Path.Combine(WorktreeRoot, Path.GetDirectoryName(Project)!));
            File.WriteAllText(Path.Combine(WorktreeRoot, Project), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(WorktreeRoot, Path.GetDirectoryName(Project)!, "HostTests.cs"),
                (source ?? HostSource).Replace("\r\n", "\n", StringComparison.Ordinal));
            foreach (var (project, projectSource) in additionalProjects ?? [])
            {
                var directory = Path.Combine(WorktreeRoot, Path.GetDirectoryName(project)!);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(WorktreeRoot, project), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
                File.WriteAllText(Path.Combine(directory, "OtherSource.cs"), projectSource);
                _projects.Add(project);
            }
            TestClassRenameEvidenceTests.Git(WorktreeRoot, "init", "-b", "main");
            TestClassRenameEvidenceTests.Git(WorktreeRoot, "config", "user.name", "Trait Exclusion Test");
            TestClassRenameEvidenceTests.Git(WorktreeRoot, "config", "user.email", "trait@example.invalid");
            TestClassRenameEvidenceTests.Commit(WorktreeRoot, "Candidate source", "tests");
            Candidate = TestClassRenameEvidenceTests.Git(WorktreeRoot, "rev-parse", "HEAD");
            WriteManifest();
        }

        internal void WriteManifest(string runner = "mtp", string[]? arguments = null, bool includePendingCheck = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            var checks = _projects.Select(project => (object)new { name = project, type = "dotnet-test",
                project, runner, arguments = arguments ?? Array.Empty<string>() }).ToList();
            if (includePendingCheck) checks.Add(new { name = "policy check", type = "command",
                command = "check-policy", arguments = Array.Empty<string>() });
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new { checks }));
        }

        internal void WriteAttempt((string Name, string Outcome)[]? rows = null)
        {
            var attempt = _evidence.WriteAttempt(rows ?? [("UnrelatedTests.Check", "Passed")], candidateSha: Candidate);
            AttemptId = attempt.Id;
            _metadataPath = attempt.MetadataPath;
            var receipt = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(
                _evidence.OrchestratorDirectory, Goal.Id, Candidate));
            Assert.Equal(AttemptId, receipt.Id);
            Assert.NotEmpty(receipt.Results);
        }

        internal void BindExecution()
        {
            var metadata = JsonNode.Parse(File.ReadAllText(_metadataPath))!.AsObject();
            metadata["executionDirectory"] = _evidence.Root;
            File.WriteAllText(_metadataPath, metadata.ToJsonString());
            var receipt = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(
                _evidence.OrchestratorDirectory, Goal.Id, Candidate));
            Assert.Equal(WorktreeRoot, receipt.WorktreeRoot);
            Assert.Equal(_projects.Count, receipt.Checks!.Declared.Count);
        }

        internal void WriteCheckOutcome(bool passed)
        {
            GateTestObligationCheckOutcomeTests.WriteResult(_evidence, AttemptId, criteria:
                [new { name = "policy check", passed }]);
            var receipt = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(
                _evidence.OrchestratorDirectory, Goal.Id, Candidate));
            Assert.Equal(passed ? GateCheckSelectorMatcher.Outcome.Satisfied : GateCheckSelectorMatcher.Outcome.Failed,
                GateCheckSelectorMatcher.Grade("policy check", receipt.Checks));
        }

        internal GateTestObligationEvaluator.Result Evaluate() => Assert.Single(
            GateTestObligationEvaluator.Evaluate(Goal, _evidence.OrchestratorDirectory, Candidate));

        internal void AssertBaselineHold(params string[] selectors)
        {
            var result = Evaluate();
            Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
            Assert.Equal(selectors.Length == 0 ? new[] { "HostTests" } : selectors, result.Selectors);
            Assert.Empty(result.TraitExclusions ?? []);
        }

        internal void AssertLanding(GateTestObligationEvaluator.Result result, string[] unresolved)
        {
            var workspace = OrchestratorWorkspace.ForDirectory(_evidence.Root, _evidence.Root) with
            { OrchestratorDirectory = _evidence.OrchestratorDirectory, IntegrationBranch = "main" };
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var landing = LandingExecutor.EvaluatePendingHumanInput(Kernel, Goal, workspace, Candidate);
                if (unresolved.Length == 0)
                {
                    Assert.Empty(landing.ObligationLines);
                    Assert.Null(landing.HoldDescription);
                }
                else
                {
                    var line = Assert.Single(landing.ObligationLines);
                    Assert.Equal($"gate test obligation {Assert.Single(Goal.GateTestObligations).Id[..8]} " +
                        $"(brief v{Goal.AuthoritativeBrief.Version}) " +
                        $"{(result.Outcome == GateTestObligationEvaluator.Outcome.Failed ? "failed" :
                            result.Outcome == GateTestObligationEvaluator.Outcome.Pending ? "pending" : "selector-not-found")}: " +
                        $"{string.Join(", ", unresolved)}; source {AttemptId}", line);
                    Assert.Equal(line, landing.HoldDescription);
                }
            });
            if (result.TraitExclusionProvenance.Length > 0)
                Assert.Contains(result.TraitExclusionProvenance + Environment.NewLine, output, StringComparison.Ordinal);
            else Assert.DoesNotContain("GATE_TEST_OBLIGATION_TRAIT_EXCLUDED", output, StringComparison.Ordinal);
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_evidence.Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            _evidence.Dispose();
            Assert.False(Directory.Exists(_evidence.Root), "The fixture's private repository was not removed.");
        }
    }
}
