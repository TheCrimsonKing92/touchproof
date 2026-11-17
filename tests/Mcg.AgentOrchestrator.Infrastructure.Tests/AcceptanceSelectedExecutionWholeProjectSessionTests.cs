using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: read-only manifest plus explicit policy switches and injected candidate inventory.
public sealed class AcceptanceSelectedExecutionWholeProjectSessionTests
{
    private const string NewClass = "Candidate.NewCoreTests";
    private static readonly AcceptanceTestClassDescriptor[] LaneClasses =
        [new("WorkerProfileTests", null), new("WorkspaceConsolidatorTests", null)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddedCoreClass_ResolvesAndKeepsScheduledCoreCheck(bool shadow)
    {
        var mode = shadow ? AcceptanceSelectedExecutionMode.Shadow : AcceptanceSelectedExecutionMode.Enforce;
        string[] changes = ["tests/Mcg.AgentOrchestrator.Core.Tests/NewCoreTests.cs"];
        var settings = ReadManifest();
        var manifest = settings.DeclaredDotnetTestChecks;
        Assert.NotEmpty(settings.InfrastructureTestLanes);
        var policy = Policy(changes, manifest);
        Assert.True(policy.Applies);
        Assert.False(policy.ForceFull);
        Assert.True(policy.IncludesProject(CoreTestsProject));
        var scheduled = AcceptanceWholeProjectCheck.Scheduled(manifest, [], policy,
            DotnetShardDisposition.RunDotnetShards, changes);
        Assert.Contains(scheduled, check => check.Name == "core tests" && check.Project == CoreTestsProject);
        var reads = new List<string>();
        var session = Session(changes, mode, project =>
        {
            reads.Add(project);
            return project == CoreTestsProject ? [new(NewClass, null)] : [];
        });
        var lanes = session.GateTransform!(SelectInfrastructureTestLanes(settings.InfrastructureTestLanes, changes, policy), scheduled);
        var effective = GoalAcceptanceVerifier.BuildPolicyEffectiveChecks("in-memory candidate", manifest, changes,
            [], policy, lanes);
        var record = session.Observe(new(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", [], [])).SelectedExecution!;

        Assert.Equal("plan", record.Decision);
        Assert.Null(record.FullGateReason);
        Assert.Empty(record.UnresolvedObligationSelectors);
        Assert.Equal(mode == AcceptanceSelectedExecutionMode.Enforce, record.Applied);
        Assert.Contains(effective, check => check.Name == "core tests" && check.Project == CoreTestsProject);
        Assert.Equal(1, reads.Count(project => project == CoreTestsProject));
        Assert.Equal(new[] { NewClass }, record.ObligationClasses);
        Assert.Equal(new AcceptanceSelectedExecutionCheckCoverage(NewClass, NewClass, "core tests"),
            Assert.Single(record.WholeProjectCoverage!));
        Assert.DoesNotContain(record.Lanes.SelectMany(lane => lane.SelectedClasses), name => name == NewClass);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(record, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal("core tests", json.RootElement.GetProperty("wholeProjectCoverage")[0].GetProperty("checkName").GetString());
        Assert.False(json.RootElement.TryGetProperty("unresolvedObligationSelectors", out _));
    }

    [Fact]
    public void PolicySkippedCoreCheck_DoesNotCertifyCoreObligation()
    {
        string[] changes = ["src/Mcg.AgentOrchestrator.App/Rendering/Renderer.cs"];
        var settings = ReadManifest();
        var manifest = settings.DeclaredDotnetTestChecks;
        var policy = Policy(changes, manifest);
        Assert.True(policy.Applies);
        Assert.False(policy.ForceFull);
        Assert.False(policy.IncludesProject(CoreTestsProject));
        var scheduled = AcceptanceWholeProjectCheck.Scheduled(manifest, [], policy,
            DotnetShardDisposition.RunDotnetShards, changes);
        Assert.DoesNotContain(scheduled, check => check.Project == CoreTestsProject);
        var session = Session(changes, AcceptanceSelectedExecutionMode.Enforce, project =>
        {
            Assert.NotEqual(CoreTestsProject, project);
            return [];
        });
        var lanes = session.GateTransform!(settings.InfrastructureTestLanes, scheduled);
        var effective = GoalAcceptanceVerifier.BuildPolicyEffectiveChecks("in-memory candidate", manifest, changes,
            [], policy, lanes);
        var record = session.Observe(new(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", [], [])).SelectedExecution!;
        Assert.Equal("plan", record.Decision);
        Assert.Equal("Resolved", record.Outcome);
        Assert.True(record.Applied);
        Assert.Null(record.FullGateReason);
        Assert.Equal(NewClass, Assert.Single(record.UnresolvedObligationSelectors).Selector);
        Assert.Null(record.WholeProjectCoverage);
        Assert.DoesNotContain(effective, check => check.Project == CoreTestsProject);
    }

    [Fact]
    public void FocusedReplacement_DoesNotSupplyWholeProjectCoverage()
    {
        string[] changes = ["tests/Mcg.AgentOrchestrator.Core.Tests/NewCoreTests.cs"];
        var manifest = ReadManifest().DeclaredDotnetTestChecks;
        var policy = Policy(changes, manifest);
        Check[] required = [new() { Name = "focused core", Type = "dotnet-test", Project = CoreTestsProject,
            Arguments = ["--filter", "FullyQualifiedName=Other.Tests"] }];
        var scheduled = AcceptanceWholeProjectCheck.Scheduled(manifest, required, policy,
            DotnetShardDisposition.RunDotnetShards, changes);
        Assert.DoesNotContain(scheduled, check => check.Project == CoreTestsProject);
        var effective = GoalAcceptanceVerifier.BuildPolicyEffectiveChecks("in-memory candidate", manifest, changes, required, policy, []);
        Assert.Contains(required[0], effective);
        Assert.DoesNotContain(effective, check => check.Name == "core tests");
    }

    [Theory]
    [InlineData(DotnetShardDisposition.DocsTreeOnlyCandidate)]
    [InlineData(DotnetShardDisposition.KnownEmptyCandidate)]
    public void NoDotnetExecution_ProvidesNoCoverage(DotnetShardDisposition disposition)
    {
        Assert.Empty(AcceptanceWholeProjectCheck.Scheduled(ReadManifest().DeclaredDotnetTestChecks, [],
            PolicyShardPlan.NotApplicable("candidate"), disposition, []));
    }

    [Fact]
    public void SolutionScheduling_ProvidesNoWholeProjectCoverage()
    {
        string[] changes = ["tests/Mcg.AgentOrchestrator.Core.Tests/NewCoreTests.cs"];
        var manifest = ReadManifest().DeclaredDotnetTestChecks;
        var policy = Policy(changes, manifest);
        foreach (var project in new string?[] { null, "Mcg.AgentOrchestrator.sln" })
        {
            var solution = new Check { Name = "solution tests", Type = "dotnet-test", Project = project };
            Assert.Empty(AcceptanceWholeProjectCheck.Scheduled([.. manifest, solution], [], policy,
                DotnetShardDisposition.RunDotnetShards, changes));
        }
    }

    [Fact]
    public void FailedProjectScan_RecordsUnresolvedSelector()
    {
        var session = Session(["tests/Mcg.AgentOrchestrator.Core.Tests/NewCoreTests.cs"], AcceptanceSelectedExecutionMode.Enforce,
            _ => throw new IOException("Cannot read candidate sources"));
        session.Transform([new("Selectable", "FullyQualifiedName~Worker")], [new("core tests", CoreTestsProject)]);
        var record = session.Observe(new(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", [], [])).SelectedExecution!;
        Assert.Equal("plan", record.Decision);
        Assert.Equal("Resolved", record.Outcome);
        Assert.True(record.Applied);
        Assert.Null(record.FullGateReason);
        Assert.Equal(NewClass, Assert.Single(record.UnresolvedObligationSelectors).Selector);
        Assert.Null(record.WholeProjectCoverage);
    }

    private static AcceptanceGateEngineSettings ReadManifest() => AcceptanceGateEngineSettings.Parse(
        File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "config", "acceptance-manifest.json")));

    private static PolicyShardPlan Policy(IReadOnlyList<string> changes, IReadOnlyList<Check> manifest) =>
        BuildPolicyShardPlan(changes, new(FullShards: false, ChangeScoped: true), CandidateTreeProbe.AssumeAllPresent,
            manifest.Select(check => check.Project).OfType<string>());

    private static AcceptanceSelectedExecutionSession Session(IReadOnlyList<string> changes,
        AcceptanceSelectedExecutionMode mode, Func<string, IReadOnlyList<AcceptanceTestClassDescriptor>> readProject) =>
        new(mode, "in-memory candidate", changes, read: (_, _) =>
            new(ReverseDependencyTestSelection.Resolved(["WorkerProfileTests"]), LaneClasses, [],
                [NewClass], readProject));
}
