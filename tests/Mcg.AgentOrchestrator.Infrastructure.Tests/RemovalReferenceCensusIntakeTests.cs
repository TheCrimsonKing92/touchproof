using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// CLI console capture is routed per async context; repositories and stores are fixture-owned.
public sealed class RemovalReferenceCensusIntakeTests : CliCommandTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Goal_OptionalDeclaration_PersistsCensusIntoWorkerBriefs(bool declared)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(workspace.ExecutionDirectory));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src/Registry.cs"), "var name = \"Fixture.Removed\";\n// Fixture.Removed\n");
        Git("init", "-q");
        Git("add", "src");
        Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false",
            "-c", "core.hooksPath=" + Path.Combine(root, "no-hooks"), "commit", "-qm", "Fixture");
        var brief = "## What to build\nUpdate the registry.\n" +
            (declared ? "Removed names: Fixture.Removed, Absent\n" : "") +
            "## Acceptance criteria\n1. The requested registry change is visible.\n";
        var briefPath = Path.Combine(root, "brief.md");
        File.WriteAllText(briefPath, brief);
        var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel());
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var changed = false;

        var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["goal", "--brief-file", briefPath, "--pipeline", "scout"], repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));

        Assert.True(changed);
        var restored = await repository.LoadAsync();
        var goal = Assert.Single(restored.Goals);
        // Goal normalizes surrounding whitespace when storing the authoritative brief.
        Assert.Equal(brief.Trim(), goal.Objective);
        var stored = goal.Timeline.Where(evt => evt.Kind == ProgressKind.RemovalReferenceCensusRecorded &&
            evt.Message.StartsWith(RemovalReferenceCensusBriefSection.StoredPrefix, StringComparison.Ordinal)).ToArray();
        const string expected = """
            Removed names: Fixture.Removed, Absent
            - `src/Registry.cs`
              - line 1 (Fixture.Removed): var name = "Fixture.Removed";
            - Absent: no references in src, tests, config, README.md or docs.
            Leave hits: 1
            """;
        if (declared)
        {
            Assert.Contains("BRIEF-LINT advisory removal-reference-census:", output);
            Assert.Contains(expected, output.ReplaceLineEndings("\n"));
            Assert.Equal(RemovalReferenceCensusBriefSection.StoredPrefix + "\n" + expected,
                Assert.Single(stored).Message.ReplaceLineEndings("\n"));
        }
        else
        {
            Assert.DoesNotContain("removal-reference-census", output);
            Assert.Empty(stored);
        }

        // Changing the checkout after filing must not replace the persisted snapshot in briefs.
        File.WriteAllText(Path.Combine(root, "src/Registry.cs"), "replacement with no references");
        foreach (var role in new[] { AgentRole.Planner, AgentRole.Developer })
        {
            var task = Assert.Single(goal.Tasks.Where(task => task.RequiredRole == role));
            var workerBrief = restored.BuildTaskBrief(goal.Id, task.Id).Content.ReplaceLineEndings("\n");
            if (declared) Assert.Contains("## Removal reference census\n" + expected, workerBrief);
            else Assert.DoesNotContain("## Removal reference census", workerBrief);
        }

        void Git(params string[] args)
        {
            var result = GitCli.RunForRepository(root, args);
            Assert.True(result.ProcessStarted && !result.DrainTimedOut && result.ExitCode == 0, result.Error);
        }
    }

    [Fact]
    public async Task Goal_VerifiedCensusText_PreservesAcceptanceOrderAfterSqliteReload()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        const string reference = "var name = \"Fixture.Removed Verified risk labels: large\";";
        File.WriteAllText(Path.Combine(root, "src/Registry.cs"), reference + "\n");
        foreach (var args in new[] { new[] { "init", "-q" }, new[] { "add", "src" } })
        {
            var result = GitCli.RunForRepository(root, args);
            Assert.True(result.ProcessStarted && !result.DrainTimedOut && result.ExitCode == 0, result.Error);
        }

        var clock = new CensusClock { UtcNow = DateTimeOffset.Parse("2026-10-01T00:00:00Z") };
        var kernel = new AgentOrchestratorKernel(clock);
        var plan = GoalObjectivePlanner.Build(
            "## What to build\nRemove the registry entry.\nRemoved names: Fixture.Removed\n" +
            "## Acceptance criteria\n1. The registry entry is removed.\n", GoalIntakePipeline.Scout);
        var removal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, AgentCatalog.Default().Agents,
            plan, workspace, new InMemoryModelProviderRegistry([]));
        var census = Assert.Single(removal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.RemovalReferenceCensusRecorded));
        Assert.Contains(reference, census.Message);
        Assert.DoesNotContain(removal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision && evt.Message.Contains(reference));

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var peer = kernel.CreateGoal("Earlier verified peer");
        kernel.RecordGoalPolicyDecision(peer.Id, "Verified");
        var peerVerifiedAt = clock.UtcNow;
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        kernel.RecordGoalPolicyDecision(removal.Id, "Verified");
        var removalVerifiedAt = clock.UtcNow;
        var snapshot = kernel.ExportSnapshot();
        // Isolate queue ordering from task execution; both candidates are ready for acceptance.
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Verified }).ToArray()
        }, clock);
        Assert.Equal(new[] { peer.Id, removal.Id },
            GoalSchedulingOrder.ForAcceptance(kernel.Goals).Select(goal => goal.Id));

        var repository = new SqliteOrchestratorStateRepository(Path.Combine(root, "state.db"));
        await repository.SaveAsync(kernel);
        var facts = await repository.ListAcceptanceQueueFactsAsync();
        Assert.Equal(peerVerifiedAt, Assert.Single(facts.Where(fact => fact.GoalId == peer.Id.Value)).VerifiedAt);
        Assert.Equal(removalVerifiedAt, Assert.Single(facts.Where(fact => fact.GoalId == removal.Id.Value)).VerifiedAt);
        Assert.Equal(new[] { peer.Id.Value, removal.Id.Value },
            facts.OrderBy(fact => fact.VerifiedAt).Select(fact => fact.GoalId));

        var restored = await repository.LoadAsync();
        Assert.Equal(new[] { peer.Id, removal.Id },
            GoalSchedulingOrder.ForAcceptance(restored.Goals).Select(goal => goal.Id));
        var restoredRemoval = restored.GetGoal(removal.Id);
        Assert.Equal(census.Message, Assert.Single(restoredRemoval.Timeline.Where(evt =>
            evt.Kind == ProgressKind.RemovalReferenceCensusRecorded)).Message);
        var developer = Assert.Single(restoredRemoval.Tasks.Where(task => task.RequiredRole == AgentRole.Developer));
        var workerBrief = restored.BuildTaskBrief(removal.Id, developer.Id).Content;
        Assert.Contains("## Removal reference census", workerBrief);
        Assert.Contains(reference, workerBrief);
    }

    private sealed class CensusClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    [Fact]
    public void Lint_CensusFailure_EmitsAdvisoryAndKeepsOtherFindings()
    {
        var findings = BriefLint.Lint("## What to build\nRemoved names: A\nCreate .git-keep.", CreateTempDirectory());
        var census = Assert.Single(findings.Where(finding => finding.Kind == "removal-reference-census"));
        Assert.Equal(BriefLintSeverity.Advisory, census.Severity);
        Assert.StartsWith("Removal reference census unavailable:", census.Message);
        Assert.Contains(findings, finding => finding.Kind == "git-directory-reference");
    }
}
