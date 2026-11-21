using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the installation and worktrees are fixture-owned, and worker starts are intercepted.
public sealed class WorkerToolsRoleNegativeControlTests
{
    [Fact]
    public void InstalledTools_AreAvailableToDeveloperAndExcludedFromVerificationRoles()
    {
        using var fixture = new WorkerToolsDispatchFixture();
        var developer = fixture.Prepare(AgentRole.Developer);
        var developerDispatch = Assert.IsType<TaskDispatchRecord>(developer.Task.LastDispatch);
        var install = Assert.IsType<string>(developerDispatch.WorkerToolsDirectory);
        Assert.Equal("pinned", File.ReadAllText(Path.Combine(install, "version.txt")));
        Assert.Contains("### Worker tools", developer.Prompt);
        var inherited = ConductorChildEnvironment.Capture().Variables;
        var developerEnvironment = fixture.CaptureStart(developer.Kernel, developer.Goal, developer.Task).Environment;
        Assert.Equal(install, developerEnvironment["PATH"]!.Split(Path.PathSeparator)[0]);
        var developerTelemetry = Assert.IsType<string>(developerEnvironment["WORKER_TOOLS_TELEMETRY"]);
        Assert.NotEqual(inherited.GetValueOrDefault("WORKER_TOOLS_TELEMETRY"), developerTelemetry);

        // Reviewer preflight resolves the default integration ref in the fixture's trunk-based repo.
        InProcessGitFixture.Run(developerDispatch.WorkingDirectory, "branch", TrunkBranchName.Default);
        foreach (var role in new[] { AgentRole.Tester, AgentRole.Reviewer })
        {
            var dispatch = fixture.Prepare(role);
            Assert.Null(dispatch.Task.LastDispatch?.WorkerToolsDirectory);
            Assert.DoesNotContain("### Worker tools", dispatch.Prompt);
            Assert.DoesNotContain("`wt read`", dispatch.Prompt);
            Assert.DoesNotContain("`wt find`", dispatch.Prompt);
            var environment = fixture.CaptureStart(dispatch.Kernel, dispatch.Goal, dispatch.Task).Environment;
            environment.TryGetValue("PATH", out var path);
            Assert.Equal(inherited.GetValueOrDefault("PATH"), path);
            Assert.DoesNotContain(install, (path ?? string.Empty).Split(Path.PathSeparator));
            Assert.Equal(inherited.TryGetValue("WORKER_TOOLS_TELEMETRY", out var inheritedTelemetry),
                environment.TryGetValue("WORKER_TOOLS_TELEMETRY", out var telemetry));
            Assert.Equal(inheritedTelemetry, telemetry);
        }
    }

    [Fact]
    public void ContextUsage_ExcludesWtCountsFromVerificationRowsEvenWhenRecorded()
    {
        var kernel = new AgentOrchestratorKernel();
        var roles = new[] { AgentRole.Developer, AgentRole.Reviewer, AgentRole.Tester };
        var tasks = roles.Select(role => new TaskSpec(TaskId.New(), "Exercise usage reporting", role)).ToArray();
        var goal = kernel.CreateGoal("Check worker-tools role boundaries", tasks);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var usage = DispatchProviderUsage.From(new ProviderReportedUsage(80, 20, 50, ToolCallCount: 4), null) with
        {
            WtCalls = new Dictionary<string, long> { ["read"] = 2 }
        };
        foreach (var task in tasks)
        {
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "fixture-worker", "fixture command", "work", ContextUsageTestFixture.Start,
                ModelName: "fixture-model", ProviderUsage: usage));
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliContextUsageCommand.Run(["context-usage", "--json"], () => [goal], output, error));
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var rows = json.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(row => row.GetProperty("role").GetString()!);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2L, rows["Developer"].GetProperty("wtCalls").GetProperty("bySubcommand").GetProperty("read").GetInt64());
        foreach (var role in new[] { "Tester", "Reviewer" })
        {
            var row = rows[role];
            Assert.Equal(1, row.GetProperty("dispatchCount").GetInt32());
            Assert.Equal(4, row.GetProperty("toolCalls").GetProperty("median").GetDouble());
            Assert.False(row.TryGetProperty("wtCalls", out _));
        }

        output.GetStringBuilder().Clear();
        Assert.Equal(0, CliContextUsageCommand.Run(["context-usage"], () => [goal], output, error));
        Assert.Empty(error.ToString());
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var developerLine = Assert.Single(lines, line => line.StartsWith("Developer | ", StringComparison.Ordinal));
        Assert.Contains("wt-calls=", developerLine);
        foreach (var role in new[] { "Tester", "Reviewer" })
        {
            var line = Assert.Single(lines, line => line.StartsWith($"{role} | ", StringComparison.Ordinal));
            Assert.Contains("tool-calls=median=4,p90=4", line);
            Assert.DoesNotContain("wt-calls=", line);
        }
    }
}
