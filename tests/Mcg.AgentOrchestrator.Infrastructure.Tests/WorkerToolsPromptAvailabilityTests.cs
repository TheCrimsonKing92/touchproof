using Mcg.AgentOrchestrator.Core;

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
}
