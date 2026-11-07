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
        var stored = goal.Timeline.Where(evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
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
    public void Lint_CensusFailure_EmitsAdvisoryAndKeepsOtherFindings()
    {
        var findings = BriefLint.Lint("## What to build\nRemoved names: A\nCreate .git-keep.", CreateTempDirectory());
        var census = Assert.Single(findings.Where(finding => finding.Kind == "removal-reference-census"));
        Assert.Equal(BriefLintSeverity.Advisory, census.Severity);
        Assert.StartsWith("Removal reference census unavailable:", census.Message);
        Assert.Contains(findings, finding => finding.Kind == "git-directory-reference");
    }
}
