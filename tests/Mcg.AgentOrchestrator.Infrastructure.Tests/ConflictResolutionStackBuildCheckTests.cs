using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorDriverTests;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

// Parallel-safe: each case owns its real git repository, worktree, project home and journal.
public sealed class ConflictResolutionStackBuildCheckTests
{
    private const string TypeScriptPath = "src/engine.ts";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypeScript_RunsCommandsInOrderAndStopsAtFailure(bool failSecond)
    {
        using var fixture = TypeScriptFixture();
        var secondArguments = failSecond
            ? new[] { "rev-parse", "--verify", "--quiet", "refs/heads/stack-check-missing" }
            : new[] { "rev-parse", "--show-toplevel" };
        var checks = new List<AcceptanceManifestCheck>
        {
            new() { Type = "crosswalk-test", Command = "must-not-run" },
            new() { Name = "first", Command = "git", Arguments = ["--version"] },
            new() { Name = "second", Type = "COMMAND", Command = "git", Arguments = secondArguments }
        };
        if (failSecond) checks.Add(new() { Name = "third", Command = "must-not-run" });
        var home = WriteManifest(fixture, checks);
        var calls = new List<string[]>();

        var result = Check(fixture, home, (arguments, directory, timeout) =>
        {
            Assert.Equal(fixture.Worktree, directory);
            calls.Add(arguments);
            return ManifestCommandBuildCheck.RunCommand(arguments, directory, timeout);
        });

        Assert.True(result.Ran, result.Output);
        Assert.Equal(failSecond ? 1 : 0, result.ExitCode);
        Assert.False(ManifestCommandBuildCheck.IsNoBuildDeclared(result));
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "git", "--version" }, calls[0]);
        Assert.Equal(new[] { "git" }.Concat(secondArguments), calls[1]);
        var lines = result.Output.Split(Environment.NewLine);
        Assert.Equal("first: git --version (exit 0)", lines[0]);
        Assert.Equal($"second: git {string.Join(' ', secondArguments)} (exit {(failSecond ? 1 : 0)})", lines[1]);
        Assert.DoesNotContain("third", result.Output);
        Assert.DoesNotContain("must-not-run", result.Output);
    }

    [Fact]
    public void CrosswalkOnly_NoBuildDeclaredCompletesConductorIntegration()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Resolve the TypeScript conflict",
            [new(TaskId.New(), "Implement", AgentRole.Developer), new(TaskId.New(), "Verify", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.First();
        PassVerification(kernel, goal, developer);
        using var fixture = TypeScriptFixture(goal.Id);
        var home = WriteManifest(fixture, [new() { Type = "crosswalk-test", Command = "must-not-run" }]);
        var result = Check(fixture, home, (_, _, _) => throw new InvalidOperationException("Test check was run."));
        Assert.False(result.Ran);
        Assert.Equal(ManifestCommandBuildCheck.NoBuildDeclared, result);
        Assert.Equal("no build check declared for this project; the acceptance gate builds it", result.Output);

        var ratchet = new RatchetConflictDispatchIntegration(kernel, fixture.Repository, "main",
            Path.Combine(fixture.Repository, ".orchestrator", "conduct-events.log"), projectHomeDirectory: home);
        var integration = new ConductorConflictIntegration(kernel, fixture.Repository, "main",
            (goalId, taskId, note, kind, cause) => kernel.RetryTaskAutomatically(goalId, taskId, note, cause,
                retryRoundKind: kind, authoritativeRetryFeedback: true),
            ratchet.IntegrateMainBeforeDeveloperDispatch, projectHomeDirectory: home);
        var recorded = integration.TryRecord(goal);
        Assert.True(recorded.Started, recorded.Detail);
        Assert.Equal(DeveloperBranchIntegrationStatus.ConflictAdmitted,
            integration.IntegrateMainBeforeDeveloperDispatch(goal).Status);
        File.WriteAllText(Path.Combine(fixture.Worktree, TypeScriptPath), "main\ngoal\n");
        var committed = new DispatchWorktreeCommitter().TryCommitWorktreeEdits(fixture.Worktree,
            "Resolve TypeScript integration", [TypeScriptPath]);
        Assert.True(committed.Succeeded, committed.ToString());

        Assert.Null(integration.Validate(goal));
        var entry = Assert.Single(GoalOperationJournal.Read(fixture.Repository, goal.Id).LatestByOperation,
            item => item.Operation == ConductorConflictIntegration.OperationName);
        Assert.Equal(GoalOperationStatus.Completed, entry.Status);
        using var detail = JsonDocument.Parse(entry.Detail!);
        Assert.Equal(result.Output, detail.RootElement.GetProperty("BuildCheck").GetString());
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("wrong-exit")]
    [InlineData("extra-text")]
    public void Ratchet_AcceptsOnlyExactNoBuildOutcome(string outcome)
    {
        var (kernel, goal) = SimpleGoal();
        using var fixture = new AdditiveConflictGitFixture(RatchetFiles(), goal.Id);
        var build = NoBuildOutcome(outcome);
        var calls = 0;
        var integration = new RatchetConflictDispatchIntegration(kernel, fixture.Repository, "main",
            Path.Combine(fixture.Repository, ".orchestrator", "conduct-events.log"), directory =>
            {
                Assert.Equal(fixture.Worktree, directory);
                calls++;
                return build;
            });

        var result = integration.IntegrateMainBeforeDeveloperDispatch(goal);

        Assert.Equal(1, calls);
        Assert.Equal(outcome == "exact" ? DeveloperBranchIntegrationStatus.Integrated
            : DeveloperBranchIntegrationStatus.Conflict, result.Status);
        Assert.Contains(build.Output, result.Message);
        if (outcome != "exact") fixture.AssertRestored();
    }

    [Theory]
    [InlineData("wrong-exit")]
    [InlineData("extra-text")]
    public void Conductor_RejectsSimilarNoBuildOutcomes(string outcome)
    {
        using var fixture = new ConductorConflictIntegrationRecordTests.Fixture();
        fixture.Admit();
        fixture.Resolve();
        var calls = 0;
        var integration = new ConductorConflictIntegration(fixture.Kernel, fixture.GitFixture.Repository, "main",
            fixture.Retry, _ => throw new InvalidOperationException("Unexpected dispatch."), directory =>
            {
                Assert.Equal(fixture.GitFixture.Worktree, directory);
                calls++;
                return NoBuildOutcome(outcome);
            });

        var failure = integration.Validate(fixture.Goal);

        Assert.Contains("build check: " + NoBuildOutcome(outcome).Output, failure);
        Assert.Equal(1, calls);
        Assert.Equal(GoalOperationStatus.Failed, fixture.Entry.Status);
        fixture.GitFixture.AssertRestored();
    }

    [Fact]
    public void CSharp_UsesDotnetBuildWithoutRunningManifestCommands()
    {
        const string project = "<Project Sdk=\"Microsoft.NET.Sdk\" />";
        using var fixture = new AdditiveConflictGitFixture([
            new(WidgetPath, "base\n", "main\n", "goal\n"),
            new("src/Stack.csproj", project, project, project)]);
        var home = WriteManifest(fixture, [new() { Command = "must-not-run" }]);
        var calls = new List<OrchestratorBuildCheckRequest>();
        var expected = new OrchestratorBuildCheckResult(true, 0, "recorded dotnet build");

        var result = RatchetConflictDispatchIntegration.BuildChangedProjects(fixture.Worktree,
            fixture.OriginalHead, fixture.MainHead, home, request =>
            {
                calls.Add(request);
                return expected;
            }, (_, _, _) => throw new InvalidOperationException("Manifest command ran on .NET path."));

        Assert.Same(expected, result);
        var call = Assert.Single(calls);
        Assert.Equal(fixture.Worktree, call.WorktreeRoot);
        Assert.Equal(new[] { "src/Stack.csproj" }, call.Projects);
        Assert.Equal(TimeSpan.FromMinutes(7), call.Timeout);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("missing-command")]
    public void InvalidManifest_FailsWithoutNoBuildException(string kind)
    {
        using var fixture = TypeScriptFixture();
        var home = WriteManifest(fixture, [new() { Name = "missing command", Type = "command" }]);
        if (kind == "malformed") File.WriteAllText(Path.Combine(home, "acceptance-manifest.json"), "{");
        if (kind == "missing")
        {
            File.Delete(Path.Combine(home, "acceptance-manifest.json"));
            Assert.False(File.Exists(Path.Combine(fixture.Worktree, "config", "acceptance-manifest.json")));
            Assert.False(File.Exists(Path.Combine(fixture.Worktree, ".orchestrator", "acceptance-manifest.json")));
        }

        var result = Check(fixture, home, (_, _, _) => throw new InvalidOperationException("Unexpected command."));

        Assert.False(result.Ran);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(ManifestCommandBuildCheck.IsNoBuildDeclared(result));
        Assert.Contains(kind == "missing-command" ? "missing command" : "Could not load build-check manifest", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimeoutOrLaunchFailure_StopsBeforeNextCommand(bool timedOut)
    {
        using var fixture = TypeScriptFixture();
        var home = WriteManifest(fixture, [
            new() { Name = "first", Command = "git", Arguments = ["--version"] },
            new() { Name = "second", Command = "must-not-run" }]);
        var calls = 0;

        var result = Check(fixture, home, (_, _, _) =>
        {
            calls++;
            if (!timedOut) throw new InvalidOperationException("launch sentinel");
            return new(0, "timeout sentinel", TimedOut: true);
        });

        Assert.Equal(1, calls);
        Assert.False(result.Ran);
        Assert.False(ManifestCommandBuildCheck.IsNoBuildDeclared(result));
        Assert.Contains(timedOut ? "timed out" : "failed to launch", result.Output);
        Assert.Contains(timedOut ? "timeout sentinel" : "launch sentinel", result.Output);
    }

    private static AdditiveConflictGitFixture TypeScriptFixture(GoalId? goalId = null) =>
        new([new(TypeScriptPath, "base\n", "main\n", "goal\n")], goalId);

    private static string WriteManifest(AdditiveConflictGitFixture fixture, IReadOnlyList<AcceptanceManifestCheck> checks)
    {
        var home = Path.Combine(fixture.Repository, "project-home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "acceptance-manifest.json"), JsonSerializer.Serialize(new AcceptanceManifest { Checks = checks }));
        return home;
    }

    private static OrchestratorBuildCheckResult Check(AdditiveConflictGitFixture fixture, string home,
        Func<string[], string, TimeSpan, CommandResult> runner) =>
        RatchetConflictDispatchIntegration.BuildChangedProjects(fixture.Worktree, fixture.OriginalHead, fixture.MainHead,
            home, _ => throw new InvalidOperationException("Unexpected .NET build for TypeScript."), runner);

    private static OrchestratorBuildCheckResult NoBuildOutcome(string outcome) => outcome switch
    {
        "exact" => new(false, ManifestCommandBuildCheck.NoBuildDeclared.ExitCode, ManifestCommandBuildCheck.NoBuildDeclared.Output),
        "wrong-exit" => ManifestCommandBuildCheck.NoBuildDeclared with { ExitCode = -1 },
        _ => ManifestCommandBuildCheck.NoBuildDeclared with { Output = "failure: " + ManifestCommandBuildCheck.NoBuildDeclared.Output }
    };
}
