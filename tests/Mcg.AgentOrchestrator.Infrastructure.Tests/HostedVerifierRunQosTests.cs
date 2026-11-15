using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique repositories/state/pipes/events; callbacks and decisions use async flow.
public sealed class HostedVerifierRunQosTests : HostCapacityBoundTestBase
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("missing", false)]
    [InlineData("invalid", false)]
    [InlineData("locked", false)]
    public void Begin_PolicyReadFailure_DisablesOnlyThisRun(string policy, bool expected)
    {
        var root = Directory.CreateTempSubdirectory("hosted-qos-").FullName;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var path = Path.Combine(Path.GetDirectoryName(workspace.SqliteStatePath)!, "conductor-policy.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (policy != "missing") File.WriteAllText(path, policy == "invalid" ? "broken json" :
                (ConductorAutonomyPolicy.Conservative with
                { AcceptanceChildrenOptOutExecutionThrottling = policy != "false" }).ToJson());
            using var locked = policy == "locked" ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            using var attemptFlag = AcceptanceChildQos.PushForTests(!expected);
            var calls = new List<(int, uint, uint)>();
            using (HostedVerifierRunQos.Begin(workspace))
            using (AcceptanceChildQos.StartSweep(100, false, _ => [],
                (pid, control, state) => { calls.Add((pid, control, state)); return true; })) { }
            Assert.Equal(expected ? [(100, 1u, 0u)] : Array.Empty<(int, uint, uint)>(), calls);
            Assert.Equal(!expected, AcceptanceChildQos.Enabled);
            Assert.Null(AcceptanceChildQosRun.Current);
            Assert.False(File.Exists(workspace.ConductEventsLogPath)); // No summary requested.
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Dispose_NestedScopeAndRepeatedDispose_RestoresAndCountsEachSweepOnce()
    {
        using var outer = new AcceptanceChildQosRun(true);
        using (var disabled = new AcceptanceChildQosRun(false))
            Assert.Null(AcceptanceChildQos.StartSweep(100, false));
        using var sweep = AcceptanceChildQos.StartSweep(100, false, _ => [101], (_, _, _) => true);
        Assert.NotNull(sweep);
        sweep.RunPass();
        sweep.Dispose();
        sweep.Dispose();
        Assert.Equal(2, outer.TriedCount);
        Assert.Equal(2, outer.OptedOutCount);
    }

    [Fact]
    public void Dispose_UnwritableLog_PreservesResultAndRestoresScope()
    {
        var root = Directory.CreateTempSubdirectory("hosted-qos-log-").FullName;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Directory.CreateDirectory(Path.GetDirectoryName(workspace.ConductEventsLogPath)!);
            Directory.CreateDirectory(workspace.ConductEventsLogPath); // A directory cannot be appended as a log.
            var qos = HostedVerifierRunQos.Begin(workspace, "full-suite");
            qos.Dispose();
            qos.Dispose();
            Assert.Null(AcceptanceChildQosRun.Current);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MainSuspectProbe_HostedInvocation_UsesPolicy(bool enabled)
    {
        using var fixture = new HostedVerifierQosFixture(enabled);
        Assert.Equal(MainSuspectReleaseProbeOutcome.Passed, (await fixture.RunProbe()).Outcome);
        Assert.Equal(1, fixture.FocusedCalls);
        fixture.AssertLane(Assert.Single(fixture.Lanes), enabled);
        Assert.Empty(fixture.Summaries());
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public void CliAcceptance_HostedInvocation_UsesPolicy(bool enabled)
    {
        using var fixture = new HostedVerifierQosFixture(enabled);
        Assert.True(fixture.RunCli());
        fixture.AssertLane(Assert.Single(fixture.Lanes), enabled);
        Assert.Empty(fixture.Summaries());
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void MultiRepository_ConductorAndCli_WrapHomeAndSecondary(bool cli, bool enabled)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        using var lanes = new HostedVerifierQosFixture(enabled);
        var workspace = fixture.Repositories.Workspace;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(workspace.SqliteStatePath)!, "conductor-policy.json"),
            (ConductorAutonomyPolicy.Conservative with { AcceptanceChildrenOptOutExecutionThrottling = enabled }).ToJson());
        fixture.OnCheck = _ => lanes.LaunchLane(lanes.Root).GetAwaiter().GetResult();
        if (cli) Assert.True(CliMultiRepositoryAcceptance.Apply(fixture.Context(), fixture.Goal));
        else Assert.NotNull(fixture.Gate.Run(fixture.Goal, GoalLifecycle.ResolveState(fixture.Goal), fixture.RunHome).Landable);
        Assert.Equal(2, lanes.Lanes.Count);
        Assert.All(lanes.Lanes, lane => lanes.AssertLane(lane, enabled));
        Assert.True(fixture.Gate.Store.TryRead(fixture.Goal.Id)!.Passed);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public void ConflictResolution_ManifestCommand_InheritsConductorPolicy(bool enabled)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Resolve TypeScript conflict",
            [new(TaskId.New(), "Implement", AgentRole.Developer), new(TaskId.New(), "Verify", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        ConductorDriverTests.PassVerification(kernel, goal, goal.Tasks.First());
        const string path = "src/engine.ts";
        using var fixture = new AdditiveConflictGitFixture([new(path, "base\n", "main\n", "goal\n")], goal.Id);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Repository);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(workspace.SqliteStatePath)!, "conductor-policy.json"),
            (ConductorAutonomyPolicy.Conservative with { AcceptanceChildrenOptOutExecutionThrottling = enabled }).ToJson());
        var home = Directory.CreateDirectory(Path.Combine(fixture.Repository, "project-home")).FullName;
        File.WriteAllText(Path.Combine(home, "acceptance-manifest.json"), JsonSerializer.Serialize(new
        {
            checks = new[] { new { name = "build", type = "command", command = "git", arguments = new[] { "--version" } } }
        }));
        using var attemptFlag = AcceptanceChildQos.PushForTests(!enabled);
        var calls = new List<(int Pid, uint Control, uint State)>();
        using var inputs = AcceptanceChildQos.PushSweepInputsForTests(_ => [],
            (pid, control, state) => { calls.Add((pid, control, state)); return true; }, TimeSpan.FromHours(1));
        var inner = new RatchetConflictDispatchIntegration(kernel, fixture.Repository, "main",
            workspace.ConductEventsLogPath);
        var integration = new ConductorConflictIntegration(kernel, fixture.Repository, "main",
            (g, t, n, k, c) => kernel.RetryTaskAutomatically(g, t, n, c,
                retryRoundKind: k, authoritativeRetryFeedback: true),
            inner.IntegrateMainBeforeDeveloperDispatch,
            projectHomeDirectory: home, statePath: workspace.SqliteStatePath);
        Assert.True(integration.TryRecord(goal).Started);
        Assert.Equal(DeveloperBranchIntegrationStatus.ConflictAdmitted, integration.IntegrateMainBeforeDeveloperDispatch(goal).Status);
        File.WriteAllText(Path.Combine(fixture.Worktree, path), "main\ngoal\n");
        Assert.True(new DispatchWorktreeCommitter().TryCommitWorktreeEdits(fixture.Worktree, "Resolve integration", [path]).Succeeded);
        Assert.Null(integration.Validate(goal));
        Assert.Equal(enabled ? 1 : 0, calls.Count);
        Assert.All(calls, call =>
        {
            Assert.True(call.Pid > 0);
            Assert.NotEqual(Environment.ProcessId, call.Pid);
            Assert.Equal((1u, 0u), (call.Control, call.State));
        });
    }
}
