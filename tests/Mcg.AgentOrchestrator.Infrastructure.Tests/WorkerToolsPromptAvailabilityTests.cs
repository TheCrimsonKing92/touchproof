using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerToolsPromptAvailabilityTests
{
    [Fact]
    public void PlannerAndDeveloper_AdvertiseOnlyInstalledToolsAndReportFailureOnce()
    {
        using var fixture = new WorkerToolsDispatchFixture();
        var failedNotes = new List<string>();
        foreach (var role in new[] { AgentRole.Planner, AgentRole.Developer })
        {
            var success = fixture.Prepare(role);
            var install = Assert.IsType<string>(success.Task.LastDispatch?.WorkerToolsDirectory);
            Assert.Contains("### Worker tools", success.Prompt);
            Assert.Contains("wt read --root . src/File.cs:10-25 src/Other.cs:42-57", success.Prompt);
            Assert.Contains("wt find --root . --definition WorkerProfileDispatcher", success.Prompt);
            Assert.Contains("`--references`", success.Prompt);
            Assert.Contains("`wt --help`", success.Prompt);
            Assert.Contains("fall back to ordinary reads", success.Prompt);

            var failure = fixture.Prepare(role, fails: true);
            Assert.Null(failure.Task.LastDispatch?.WorkerToolsDirectory);
            Assert.DoesNotContain("### Worker tools", failure.Prompt);
            var environment = fixture.CaptureStart(failure.Kernel, failure.Goal, failure.Task).Environment;
            Assert.DoesNotContain(install, environment["PATH"]!.Split(Path.PathSeparator));
            Assert.NotNull(environment["WORKER_TOOLS_TELEMETRY"]);
            failedNotes.AddRange(failure.Goal.Timeline.Select(item => item.Message)
                .Where(message => message.StartsWith("WORKER_TOOLS_INSTALL_FAILED", StringComparison.Ordinal)));
        }
        Assert.Contains("stage=materialize", Assert.Single(failedNotes));
    }

    [Fact]
    public void ClaudeReadOnlyDispatch_GrantsWorkerToolsOnlyToPlannerWithInstall()
    {
        using var fixture = new WorkerToolsDispatchFixture();
        var installedPlanner = fixture.Prepare(AgentRole.Planner, useClaude: true);
        var installedDispatch = Assert.IsType<TaskDispatchRecord>(installedPlanner.Task.LastDispatch);
        Assert.NotNull(installedDispatch.WorkerToolsDirectory);
        Assert.Contains("--permission-mode 'dontAsk'", installedDispatch.Command);
        var baseAllowlist = ProviderCommandBuilder.ClaudeReadOnlyAllowedTools;
        Assert.Contains($"--allowed-tools '{baseAllowlist},Bash(wt read *),Bash(wt find *),Bash(wt --help*)'", installedDispatch.Command);

        var unavailablePlanner = fixture.Prepare(AgentRole.Planner, fails: true, useClaude: true);
        var unavailableDispatch = Assert.IsType<TaskDispatchRecord>(unavailablePlanner.Task.LastDispatch);
        Assert.Null(unavailableDispatch.WorkerToolsDirectory);
        Assert.Contains($"--allowed-tools '{baseAllowlist}'", unavailableDispatch.Command);

        foreach (var role in new[] { AgentRole.Reviewer, AgentRole.Researcher })
        {
            var control = fixture.Prepare(role, useClaude: true);
            var dispatch = Assert.IsType<TaskDispatchRecord>(control.Task.LastDispatch);
            Assert.Contains($"--allowed-tools '{baseAllowlist}'", dispatch.Command);
        }
    }
}
