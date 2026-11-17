using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static AcceptanceSelectedExecutionProjectFixture;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: injected inputs and individually owned repository fixtures; no child processes.
public sealed class AcceptanceSelectedExecutionProjectCheckTests
{
    [Fact]
    public void EffectivePlan_UnreachedProjects_ExcludesBothChecks()
    {
        using var repository = Repository();
        var session = Session();
        var plan = EffectivePlan(repository.Root, session);
        Assert.True(session.Applied);
        Assert.Equal(new[] { "cli tests", "owner console tests" }, session.ProjectChecks.Select(entry => entry.Name));
        Assert.All(session.ProjectChecks, entry => Assert.Equal("skipped", entry.Lane.Disposition));
        Assert.DoesNotContain(plan.Checks, check => Checks.Any(original => original.Name == check.Name));
    }

    [Theory]
    [InlineData("Candidate.CliReached", "Candidate.CliGuard", "cli tests")]
    [InlineData("Candidate.ConsoleReached", "Candidate.ConsoleGuard", "owner console tests")]
    public void EffectivePlan_ReachedProject_NarrowsToReachedAndGuard(string reached, string guard, string name)
    {
        using var repository = Repository();
        var session = Session(reached: ["A", reached], guards: ["Candidate.Guard", guard]);
        var plan = EffectivePlan(repository.Root, session);
        var check = Assert.Single(plan.Checks, check => check.Name == name);
        var expected = new[] { guard, reached }.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "--verbosity", "minimal", "--filter",
            "(" + string.Join('|', expected.Select(item => "FullyQualifiedName=" + item)) + ")" }, check.Arguments);
        Assert.True(check.IsResolvedInfrastructureLane);
        Assert.True(check.AllowsEmptySelection);
        Assert.Equal(expected, AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(check.Arguments[^1])
            .Where(token => token != "--filter-class"));
        Assert.DoesNotContain(plan.Checks, check => Checks.Any(other => other.Name != name && other.Name == check.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retention_GuardOrObligation_ActivatesUnreachedProject(bool obligation)
    {
        var session = Session(guards: obligation ? [] : ["Candidate.CliGuard"],
            obligations: obligation ? ["Candidate.CliGuard"] : []);
        var observation = Observe(session);
        var entry = Assert.Single(observation.SelectedExecution!.Lanes, lane => lane.Name == "cli tests");
        Assert.Equal("narrowed", entry.Disposition);
        Assert.Equal(new[] { "Candidate.CliGuard" }, entry.SelectedClasses);
        Assert.Contains("cli tests", observation.Scheduled);
    }

    [Theory]
    [InlineData("missing-changes")]
    [InlineData("unmapped-file")]
    [InlineData("unresolved-class")]
    [InlineData("attribution")]
    [InlineData("Shadow")]
    [InlineData("Off")]
    public void EffectivePlan_FullGateOrNonEnforce_PreservesOriginalArguments(string scenario)
    {
        using var repository = Repository();
        var mode = scenario == "Shadow" ? AcceptanceSelectedExecutionMode.Shadow : scenario == "Off"
            ? AcceptanceSelectedExecutionMode.Off : AcceptanceSelectedExecutionMode.Enforce;
        var session = new AcceptanceSelectedExecutionSession(mode, repository.Root,
            scenario == "missing-changes" ? null : AcceptanceSelectedExecutionFixture.Changes,
            attributionScoped: scenario == "attribution", read: (_, _) => new(
                scenario == "unmapped-file" ? ReverseDependencyTestSelection.Abandoned(
                    ReverseDependencyDegradationKind.ChangedFileUnmapped, "unmapped file") :
                    ReverseDependencyTestSelection.Resolved(scenario == "unresolved-class" ? ["Missing"] : ["A"]),
                AcceptanceSelectedExecutionFixture.Classes, [], ProjectClasses: project => Inventories.Single(item => item.Project == project).Classes));
        var plan = EffectivePlan(repository.Root, session);
        Assert.False(session.Applied);
        foreach (var original in Checks)
            Assert.Equal(original.Arguments, Assert.Single(plan.Checks, check => check.Name == original.Name).Arguments);
        if (mode == AcceptanceSelectedExecutionMode.Shadow)
        {
            var record = session.Observe(new(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", [], [])).SelectedExecution!;
            foreach (var original in Checks)
            {
                var entry = Assert.Single(record.Lanes, lane => lane.Name == original.Name);
                Assert.Equal(AcceptanceGateLaneSkip.ShadowLaneNotSelected, entry.LaneSkipReason);
                Assert.Equal(AcceptanceGateLaneSkip.ShadowClassNotSelected, entry.ClassSkipReason);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inventory_EmptyOrUnreadable_KeepsCheckWhole(bool unreadable)
    {
        var session = Session(projectClasses: _ => unreadable ? throw new IOException("inventory unavailable") : []);
        session.Transform(AcceptanceSelectedExecutionFixture.Lanes, Scheduled);
        Assert.True(session.Applied);
        Assert.All(session.ProjectChecks, entry => Assert.Equal("retained", entry.Lane.Disposition));
        Assert.Equal(Checks, session.TransformProjectChecks(Checks));
    }

    [Fact]
    public void Entry_FloorOrUnavailableReach_KeepsWholeBeforeEmptyRetention()
    {
        var emptyRetention = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in new[]
        {
            AcceptanceSelectedExecutionProjectCheckEntry.Create(Inventories[0], emptyRetention, ["cli tests"]),
            AcceptanceSelectedExecutionProjectCheckEntry.Create(Inventories[0], emptyRetention, [], reachAvailable: false)
        })
        {
            Assert.Equal("retained", entry.Lane.Disposition);
            Assert.Empty(entry.Lane.SkippedClasses);
            Assert.Same(Checks[0], entry.Apply(Checks[0]));
        }
    }

    [Fact]
    public void Entry_Narrowing_PreservesMetadataAndExcludesFocusedChecks()
    {
        var original = new Check { Name = "cli tests", Type = "dotnet-test", Project = CliTestsProject,
            Arguments = ["--verbosity", "minimal"], Command = "command", Reports = ["report"], Pattern = "pattern",
            FilePath = "path", TimeoutMinutes = 3, Advisory = true, Runner = "mtp", EstimatedSerialSeconds = 4,
            ExclusiveResourceKeys = ["key"], RequiresBuildSystemChange = true };
        var entry = AcceptanceSelectedExecutionProjectCheckEntry.Create(Inventories[0],
            new HashSet<string> { "Candidate.CliReached" }, []);
        var narrowed = entry.Apply(original);
        Assert.True(entry.Matches(original));
        Assert.False(entry.Matches(new() { Name = "cli tests", Type = "dotnet-test", Project = CliTestsProject,
            Arguments = ["--filter", "FullyQualifiedName~Other"] }));
        Assert.Equal(original.Name, narrowed.Name);
        Assert.Equal(original.Project, narrowed.Project);
        Assert.Equal(original.Type, narrowed.Type);
        Assert.Equal(original.Command, narrowed.Command);
        Assert.Equal(original.Reports, narrowed.Reports);
        Assert.Equal(original.Pattern, narrowed.Pattern);
        Assert.Equal(original.FilePath, narrowed.FilePath);
        Assert.Equal(original.TimeoutMinutes, narrowed.TimeoutMinutes);
        Assert.Equal(original.Advisory, narrowed.Advisory);
        Assert.Equal(original.Runner, narrowed.Runner);
        Assert.Equal(original.EstimatedSerialSeconds, narrowed.EstimatedSerialSeconds);
        Assert.Equal(original.ExclusiveResourceKeys, narrowed.ExclusiveResourceKeys);
        Assert.True(narrowed.RequiresBuildSystemChange);
    }

    [Fact]
    public void Resolution_OverlappingNames_RetainsEveryProjectMembership()
    {
        var session = Session(reached: ["Shared"], projectClasses: _ => [new("Candidate.Shared", null)]);
        session.Transform(AcceptanceSelectedExecutionFixture.Lanes, Scheduled);
        Assert.True(session.Applied);
        Assert.All(session.ProjectChecks, entry => Assert.Equal(new[] { "Candidate.Shared" }, entry.Lane.SelectedClasses));
    }
}
