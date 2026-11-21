using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static AcceptanceSelectedExecutionFixture;
using static AcceptanceSelectedExecutionProjectFixture;
using static ImpactReplayChangedSupportReferenceScanTests;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Each scan owns its sources. The selected-execution session uses injected inventories.
public sealed class AcceptanceSelectedExecutionSupportFallbackTests
{
    [Theory]
    [InlineData(1, 20, "exceeds threshold")]
    [InlineData(100, 1, "truncated")]
    [InlineData(0, 20, "invalid support closure threshold")]
    public void SupportFallback_RetainsInfrastructureAndRecordsCause(int threshold, int maximumVisited, string reason)
    {
        using var fixture = new SourceFixture();
        fixture.Add("Support.cs", "public class Helpers { }");
        fixture.Add("A.cs", "namespace Candidate; public class A { Helpers value; }");
        fixture.Add("B.cs", "namespace Candidate; public class B { A value; }");
        fixture.Runnable.UnionWith(["Candidate.A", "Candidate.B"]);
        string[] changed = ["Support.cs"];
        var result = Select(fixture, changed, threshold, maximumVisited);
        var session = CreateSession(result, changed, ["Candidate.A", "Candidate.CliReached"]);

        var observation = AcceptanceSelectedExecutionProjectFixture.Observe(session);

        var record = Assert.IsType<AcceptanceSelectedExecutionRecord>(observation.SelectedExecution);
        Assert.Equal("plan", record.Decision);
        Assert.Contains("Support.cs:", record.FullGateReason);
        Assert.Contains(reason, record.FullGateReason);
        foreach (var lane in Lanes)
        {
            var entry = Assert.Single(record.Lanes, item => item.Name == lane.Name);
            Assert.Equal("retained", entry.Disposition);
            Assert.Equal(lane.Filter, entry.Filter);
        }
        Assert.Equal("narrowed", Assert.Single(session.ProjectChecks, check => check.Project == CliTestsProject).Lane.Disposition);
        using var json = JsonDocument.Parse(observation.ToJson());
        var summary = Assert.Single(json.RootElement.GetProperty("selectedExecution").GetProperty("supportSelections").EnumerateArray());
        Assert.Equal("Support.cs", summary.GetProperty("path").GetString());
        Assert.True(summary.GetProperty("fullRunTriggered").GetBoolean());
    }

    [Fact]
    public void TruncatedSupportClosure_SchedulesCoreOutsideOriginalPolicyScope()
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        using var repository = new RepositoryTestReachTests.RepositoryFixture();
        const string support = RepositoryTestReachTests.RepositoryFixture.TestDirectory + "/Support.cs";
        const string referrer = RepositoryTestReachTests.RepositoryFixture.TestDirectory + "/A.cs";
        repository.Write(support, "public class Helpers { }");
        repository.Write(referrer, "namespace Candidate; public class A { Helpers value; }");
        Check[] declarations =
        [
            new() { Name = "core tests", Type = "dotnet-test", Project = CoreTestsProject, Arguments = ["--verbosity", "minimal"] },
            new() { Name = "infrastructure tests", Type = "dotnet-test", Project = InfrastructureTestsProject }
        ];
        repository.Write("config/acceptance-manifest.json", JsonSerializer.Serialize(new { checks = declarations }));
        string[] changed = [support];
        var settings = new AcceptanceGateEngineSettings { EnforceStructuralCoverage = false };
        AcceptanceBalancedLaneExpansion lanes = new(Lanes, null);
        var baseline = GoalAcceptanceVerifier.CreateEffectiveGatePlan(repository.Root, changed, settings,
            infrastructureLaneExpansion: lanes, balanceFocusedSelection: true);
        Assert.True(baseline.PolicyShardPlan.Applies);
        Assert.False(baseline.PolicyShardPlan.ForceFull);
        Assert.False(baseline.PolicyShardPlan.IncludesProject(CoreTestsProject));
        Assert.DoesNotContain(baseline.Checks, check => check.Project == CoreTestsProject);

        var result = ImpactReplayChangedSupportReferenceScan.Select(repository.Root, changed,
            [new(Path.Combine(repository.Root, InfrastructureTestsProject), true,
                [Path.Combine(repository.Root, support), Path.Combine(repository.Root, referrer)]),
             new(Path.Combine(repository.Root, CoreTestsProject), false, [])],
            () => new HashSet<string>(StringComparer.Ordinal) { "Candidate.A" }, maximumVisited: 1);
        var fallback = Assert.Single(result.SupportSelections);
        Assert.True(fallback.Truncated);
        Assert.Contains(CoreTestsProject, fallback.AffectedProjects);
        var session = CreateSession(result, changed, result.Classes);

        var effective = GoalAcceptanceVerifier.CreateEffectiveGatePlan(repository.Root, changed, settings,
            infrastructureLaneExpansion: lanes, selectedExecution: session, balanceFocusedSelection: true);

        Assert.False(effective.StructuralCoverageApplies);
        Assert.False(effective.PolicyShardPlan.ForceFull);
        var core = Assert.Single(effective.Checks, check => check.Project == CoreTestsProject);
        Assert.Equal(declarations[0].Arguments, core.Arguments);
        var observation = AcceptanceGateLanePlanPublisher.Classify("attempt", effective.CandidateLanes,
            effective.InfrastructureTestLanes, effective.Manifest.Checks, effective.PolicyRequiredChecks,
            effective.PolicyShardPlan, effective.DotnetShardDisposition, effective.SemanticDeduplications,
            effective.Checks, effective.Checks, effective.Checks, selectedExecution: session);
        var record = Assert.IsType<AcceptanceSelectedExecutionRecord>(observation.SelectedExecution);
        Assert.Contains(support, record.FullGateReason);
        Assert.Contains("truncated", record.FullGateReason);
    }

    [Fact]
    public void MultipleFallbacks_AggregateWithExistingReasonInPathOrder()
    {
        ImpactReplayChangedSupportFileSummary[] summaries =
        [
            new("Z.cs", 0, 0, false, true, false, [InfrastructureTestsProject], true, ["truncated"]),
            new("A.cs", 2, 3, true, false, false, [InfrastructureTestsProject], true, ["exceeds threshold"])
        ];
        var plan = AcceptanceSelectedExecutionPlan.Create(["Z.cs", "A.cs"],
            ReverseDependencyTestSelection.Abandoned(ReverseDependencyDegradationKind.ChangedFileUnmapped, "existing reason"),
            Lanes, Classes, [], [], supportSelections: summaries);

        Assert.Equal("existing reason; A.cs: exceeds threshold; Z.cs: truncated", plan.FullGateReason);
        Assert.Equal(Lanes, plan.ExecutionLanes);
        Assert.Equal(summaries, plan.SupportSelections);
    }

    [Fact]
    public void ProjectFallback_TakesPrecedenceOverItsClassSelection()
    {
        ImpactReplayChangedSupportFileSummary[] summaries =
        [
            new("CliSupport.cs", 1, 2, true, false, false, [CliTestsProject], true, ["exceeds threshold"])
        ];
        var plan = AcceptanceSelectedExecutionPlan.Create(["CliSupport.cs"],
            ReverseDependencyTestSelection.Resolved(["Candidate.A", "Candidate.CliReached", "Candidate.ConsoleReached"]),
            Lanes, Classes, [], [], selectableProjectChecks: () => Inventories, supportSelections: summaries);

        Assert.Equal("CliSupport.cs: exceeds threshold", plan.FullGateReason);
        Assert.Equal("retained", Assert.Single(plan.ProjectChecks, check => check.Project == CliTestsProject).Lane.Disposition);
        Assert.Equal("narrowed", Assert.Single(plan.ProjectChecks, check => check.Project == OwnerConsoleTestsProject).Lane.Disposition);
        Assert.Equal("narrowed", Assert.Single(plan.Lanes, lane => lane.Name == "Balanced 1").Disposition);
    }

    [Fact]
    public void BoundedSupportSelection_NarrowsOnlyTheProjectWithTheReferrer()
    {
        var plan = AcceptanceSelectedExecutionPlan.Create(["Support.cs"],
            ReverseDependencyTestSelection.Resolved(["Candidate.A"]), Lanes, Classes, [], [],
            selectableProjectChecks: () => Inventories,
            supportProjectClasses: new Dictionary<string, IReadOnlyList<string>>
            {
                [CliTestsProject] = ["Candidate.CliReached"]
            });

        Assert.Null(plan.FullGateReason);
        var cli = Assert.Single(plan.ProjectChecks, check => check.Project == CliTestsProject).Lane;
        Assert.Equal("narrowed", cli.Disposition);
        Assert.Equal("Candidate.CliReached", Assert.Single(cli.SelectedClasses));
        Assert.Equal("skipped", Assert.Single(plan.ProjectChecks, check => check.Project == OwnerConsoleTestsProject).Lane.Disposition);
    }

    [Fact]
    public void LeafChange_KeepsNarrowedLanes()
    {
        using var fixture = new SourceFixture();
        fixture.Add("A.cs", "namespace Candidate; public class A { }");
        fixture.Add("B.cs", "namespace Candidate; public class B { }");
        fixture.Runnable.UnionWith(["Candidate.A", "Candidate.B"]);
        string[] changed = ["A.cs"];
        var result = Select(fixture, changed);
        var session = CreateSession(result, changed, result.Classes);

        var observation = AcceptanceSelectedExecutionFixture.Observe(session);

        var record = Assert.IsType<AcceptanceSelectedExecutionRecord>(observation.SelectedExecution);
        Assert.Equal("plan", record.Decision);
        Assert.Null(record.FullGateReason);
        var lane = Assert.Single(record.Lanes, item => item.Name == "Balanced 1");
        Assert.Equal("narrowed", lane.Disposition);
        Assert.Equal("Candidate.A", Assert.Single(lane.SelectedClasses));
        Assert.Equal("skipped", Assert.Single(record.Lanes, item => item.Name == "Balanced 2").Disposition);
    }

    private static ImpactReplayChangedSupportSelection Select(SourceFixture fixture, string[] changed,
        int threshold = 1, int maximumVisited = 20) =>
        ImpactReplayChangedSupportReferenceScan.Select(fixture.Root, changed,
            [fixture.Projects[0] with { Path = Path.GetFullPath(InfrastructureTestsProject, fixture.Root) }],
            () => fixture.Runnable, threshold: threshold, maximumVisited: maximumVisited);

    private static AcceptanceSelectedExecutionSession CreateSession(ImpactReplayChangedSupportSelection result,
        string[] changed, IReadOnlyList<string> classes) =>
        new(AcceptanceSelectedExecutionMode.Enforce, "in-memory candidate", changed, read: (_, _) =>
            new(ReverseDependencyTestSelection.Resolved(classes), Classes, [],
                ProjectClasses: project => Inventories.Single(item => item.Project == project).Classes,
                SupportSelections: result.SupportSelections, SupportProjectClasses: result.ProjectClasses));
}
