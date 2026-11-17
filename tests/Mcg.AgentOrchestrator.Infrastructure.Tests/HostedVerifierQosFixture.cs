using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Private repositories/children and async-flow seams; no machine-global mutation or census.
internal sealed class HostedVerifierQosFixture : GoalWorktreeTestBase, IDisposable
{
    internal string Root { get; } = CreateReducedAcceptanceCohortRepository();
    internal OrchestratorWorkspace Workspace { get; }
    internal ConductorDriver Driver { get; }
    internal ConcurrentQueue<Lane> Lanes { get; } = new();
    internal bool ExpectedEnabled { get; set; }
    internal bool FailFirst { get; set; }
    internal bool ThrowAfterLane { get; set; }
    internal bool CancelAfterLane { get; set; }
    internal bool FailMemberSetter { get; set; }
    internal int FocusedCalls { get; private set; }
    internal Func<Task>? BeforeLane { get; set; }
    internal Action? BeforeRerun { get; set; }
    private readonly string tip;
    private readonly CancellationTokenSource cancellation = new(TestHangGuard.HangSafetyBound);
    internal string PolicyPath => Path.Combine(Path.GetDirectoryName(Workspace.SqliteStatePath)!, "conductor-policy.json");

    internal HostedVerifierQosFixture(bool enabled)
    {
        Workspace = OrchestratorWorkspace.ForDirectory(Root);
        WritePolicy(enabled);
        ExpectedEnabled = enabled;
        tip = RunGitOutput(Root, "rev-parse", "main").Trim();
        Driver = new(new AgentOrchestratorKernel(), Workspace, new Verifier(this),
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
            cleanupHooks: CreateIsolatedCleanupContext(Root).Hooks);
        Driver.CohortPartitionStableSlotLeaseSource = (_, _) =>
            AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(Root);
    }

    internal void WritePolicy(bool enabled) => File.WriteAllText(PolicyPath,
        (ConductorAutonomyPolicy.Conservative with { AcceptanceChildrenOptOutExecutionThrottling = enabled }).ToJson());

    internal string[] Summaries() => File.Exists(Workspace.ConductEventsLogPath)
        ? File.ReadAllLines(Workspace.ConductEventsLogPath).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.GetProperty("detail").GetString()!;
        }).Where(line => line.StartsWith("BUILD_QOS kind=full-suite ", StringComparison.Ordinal)).ToArray() : [];

    internal void AssertLane(Lane lane, bool enabled)
    {
        Assert.True(lane.RootPid > 0, "The invoker never reported its started root.");
        var expected = enabled ? new[] { lane.RootPid, lane.MemberA, lane.MemberB } : [];
        Assert.Equal(expected.Order(), lane.Calls.Select(call => call.Pid).Order());
        Assert.All(lane.Calls, call =>
        {
            Assert.Equal((1u, 0u), (call.Control, call.State));
            Assert.NotEqual(Environment.ProcessId, call.Pid);
        });
    }

    internal async Task LaunchLane(string checkout)
    {
        if (BeforeLane is { } before) await before();
        var lane = new Lane(Lanes.Count);
        Lanes.Enqueue(lane);
        var eventName = "mcg-qos-" + Guid.NewGuid().ToString("N");
        using var exit = new EventWaitHandle(!ExpectedEnabled, EventResetMode.ManualReset, eventName);
        using var inputs = AcceptanceChildQos.PushSweepInputsForTests(
            root => [root, lane.MemberA, lane.MemberA, lane.MemberB],
            (pid, control, state) =>
            {
                lane.Calls.Enqueue((pid, control, state));
                if (pid == lane.MemberB) exit.Set();
                return !FailMemberSetter || pid != lane.MemberA;
            }, TimeSpan.FromMilliseconds(1));
        // Controlled lane launcher, through the production invoker's sweep start. The
        // child cannot exit before the member pass; timeout is only a missing-event guard.
        var result = await AcceptanceProcessRunner.RunProcessAsync(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command",
                $"$gate = [System.Threading.EventWaitHandle]::OpenExisting('{eventName}'); " +
                "try { [void]$gate.WaitOne() } finally { $gate.Dispose() }"],
            checkout, TestHangGuard.HangSafetyBound, false, cancellation.Token,
            cleanupObserver: observation =>
            {
                if (observation.Stage == "started") lane.RootPid = observation.ProcessId;
            });
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    internal sealed class Lane(int index)
    {
        internal int RootPid { get; set; }
        internal int MemberA => 10000000 + index * 2;
        internal int MemberB => MemberA + 1;
        internal ConcurrentQueue<(int Pid, uint Control, uint State)> Calls { get; } = new();
    }

    internal bool RunCli()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateCompletedGoal(kernel, "CLI hosted QoS", Root);
        var checkout = GoalWorktrees.Ensure(Root, goal.Id);
        File.WriteAllText(Path.Combine(checkout, "qos.txt"), "candidate");
        RunGit(checkout, "add", "qos.txt");
        RunGit(checkout, "commit", "-m", "QoS candidate");
        var cleanup = CreateIsolatedCleanupContext(Root);
        var context = new CliExecutionContext(kernel, Workspace, new InMemoryModelProviderRegistry([]),
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), goal)
        {
            AcceptanceVerifier = new Verifier(this),
            RunInjectedAcceptanceVerifierInCurrentProcess = true,
            CleanupContext = cleanup,
            StableSlotSelector = (_, _) => AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(
                cleanup.Hooks.BuildStorageRoot?.RootPath ?? Root)
        };
        return CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context);
    }

    internal Task<MainSuspectReleaseProbeResult> RunProbe() => Driver.CreateMainSuspectReleaseProbe()
        .RunAsync(tip, ["Fixture.Tests.Fails"], cancellation.Token);

    private sealed class Verifier(HostedVerifierQosFixture fixture) : IGoalAcceptanceVerifier
    {
        public async Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            await fixture.LaunchLane(worktreePath);
            if (fixture.ThrowAfterLane) throw new IOException("controlled verifier failure");
            if (fixture.CancelAfterLane) throw new OperationCanceledException("controlled cancellation");
            var passed = !fixture.FailFirst;
            return new(passed, false, passed ? 0 : 1, "controlled lane",
                Checks: [new("lane", passed, passed ? 0 : 1, "controlled",
                    ExecutedTestCount: 1, FailingTestIdentities: passed ? [] : ["Fixture.Tests.Fails"])]);
        }

        public async Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            fixture.FocusedCalls++;
            fixture.BeforeRerun?.Invoke();
            await fixture.LaunchLane(worktreePath);
            return new(request, true, true, "controlled rerun",
                [new("focused", true, 0, "controlled", ExecutedTestCount: 1)]);
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        DeleteDirectory(Root);
    }
}
