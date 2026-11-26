using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class UnsupervisedSelfRelaunchGuardTestsConductorBatchLoopTests : ConductorBatchLoopTests
{
    public UnsupervisedSelfRelaunchGuardTestsConductorBatchLoopTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void LandingDelegatesOnlyWithALiveSupervisor(bool leaseExists, bool ownerAlive)
    {
        using var fixture = new LeaseFixture();
        var owner = new ConductorSupervisorProcessIdentity(900,
            new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        if (leaseExists) fixture.WriteLease(owner);
        var probe = new FakeProbe(owner, ownerAlive);
        var attention = new List<(string Title, string Body, string Key)>();
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update verifier");
        var landed = false;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"]);
        var windowId = Guid.NewGuid();
        var now = PreTesterResolverObservationTests.Start;
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            PreTesterResolverObservationWindowId = ownerAlive ? windowId : null
        };
        driver.PreTesterResolverObservation = PreTesterResolverObservationTests.Window(fixture.Root);

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var loop = new ConductorBatchLoop(
                utcNow: () => now,
                selfRelaunch: SupervisedSelfRelaunchGuard.Create(fixture.Root,
                    ConductorSelfRelaunch.CreateSupervisorDelegated(),
                    (title, body, key) => attention.Add((title, body, key)), probe),
                selfRelaunchEnabled: true);
            var summary = loop.Run(kernel, driver, policy,
                NoStopPath(), maxIterations: 4);
            var artifact = ConductorContinuityExitArtifactFactory.FromLoopSummary(summary, true);
            if (ownerAlive)
            {
                Assert.Equal("delegated-to-continuity-supervisor", summary.Handoff?.Reason);
                Assert.Equal("self-relaunch-handoff", summary.StopReason);
                Assert.True(artifact.RestartRequested);
                var checkpoint = Assert.IsType<string>(artifact.ResolverObservationHandoff);
                var path = PreTesterResolverObservationTests.CorpusPath(fixture.Root, windowId);
                Assert.False(File.Exists(path + ".receipt.json"));
                now = now.AddHours(1);
                using var next = PreTesterResolverObservationTests.Window(fixture.Root, handoff: checkpoint);
                using (var tick = Assert.IsType<PreTesterReverseDependencyObservation.Tick>(next.BeginTick(windowId, () => now)))
                    tick.Complete();
                now = PreTesterResolverObservationTests.Start + PreTesterReverseDependencyObservation.Duration;
                Assert.Null(next.BeginTick(windowId, () => now));
                var seal = PreTesterResolverObservationTests.Receipt(path);
                var corpus = PreTesterResolverCorpus.Read(path, seal.Sha256!);
                Assert.Equal(now, corpus.Header.Deadline);
                Assert.Equal(2, corpus.Producers.Select(producer => producer.SessionId).Distinct().Count());
                Assert.Equal(summary.Ticks + 1, corpus.Manifest.CompleteTickCount);
            }
            else
            {
                Assert.Null(summary.Handoff);
                Assert.NotEqual("self-relaunch-handoff", summary.StopReason);
                Assert.False(artifact.RestartRequested);
            }
        });

        Assert.True(landed);
        if (ownerAlive)
        {
            Assert.Empty(attention);
        }
        else
        {
            Assert.Contains("LOOP_HANDOFF_FAILED", output);
            Assert.Contains("phase=handoff", output);
            Assert.Contains("rolledBack=true continuing=true", output);
            Assert.Contains("supervisor-absent", output);
            var item = Assert.Single(attention);
            Assert.Equal("Conductor self-relaunch held: no live continuity supervisor", item.Title);
            Assert.Equal($"conductor-supervisor-absent:{goal.Id.Value}", item.Key);
            Assert.Contains(leaseExists ? "owner-not-running" : "lease-missing", item.Body);
        }
        if (leaseExists) Assert.Equal(owner, probe.Checked);
    }

    [Xunit.Theory]
    [Xunit.InlineData("", "absent", "lease-empty")]
    [Xunit.InlineData("{invalid", "unknown", "invalid-json")]
    [Xunit.InlineData("{}", "unknown", "invalid-json")]
    public void ProbeClassifiesEmptyAndInvalidLease(string json, string kind, string reason)
    {
        using var fixture = new LeaseFixture();
        File.WriteAllText(fixture.Path, json);
        var result = SupervisedSelfRelaunchGuard.Probe(fixture.Root, new ConductLockPidProbe());
        Assert.Equal(kind, result.Kind);
        Assert.Equal(reason, result.Reason);
    }

    [Xunit.Fact]
    public void ProbeReportsUnknownAfterSharingRetriesAreExhausted()
    {
        using var fixture = new LeaseFixture();
        File.WriteAllText(fixture.Path, "{}");
        using var held = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var calls = new List<int>();
        var presence = SupervisedSelfRelaunchGuard.Probe(fixture.Root, new ConductLockPidProbe(), calls.Add);
        Assert.Equal("unknown", presence.Kind);
        Assert.Equal("lease-unreadable", presence.Reason);
        Assert.Equal(Enumerable.Range(1, 39), calls);
    }

    [Xunit.Fact]
    public void ProbeRecognizesCurrentProcessPidAndStartTime()
    {
        using var fixture = new LeaseFixture();
        using var process = Process.GetCurrentProcess();
        var identity = new ConductorSupervisorProcessIdentity(process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
        fixture.WriteLease(identity);
        Assert.Equal("present", SupervisedSelfRelaunchGuard.Probe(fixture.Root, new ConductLockPidProbe()).Kind);
        fixture.WriteLease(identity with { StartedAt = identity.StartedAt.AddSeconds(-1) });
        Assert.Equal("owner-not-running", SupervisedSelfRelaunchGuard.Probe(fixture.Root, new ConductLockPidProbe()).Reason);
    }

    private sealed class FakeProbe(ConductorSupervisorProcessIdentity expected, bool alive) : IConductLockPidProbe
    {
        internal ConductorSupervisorProcessIdentity? Checked { get; private set; }
        public bool IsRunning(int processId) => throw new NotSupportedException("Use pid plus start time.");
        public bool IsSameProcess(int processId, DateTimeOffset startedAt)
        {
            Checked = new(processId, startedAt);
            return alive && Checked == expected;
        }
    }

    private sealed class LeaseFixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        internal string Path => System.IO.Path.Combine(Root, "conduct-supervisor.lock");
        internal LeaseFixture() => Directory.CreateDirectory(Root);
        internal void WriteLease(ConductorSupervisorProcessIdentity identity) => File.WriteAllText(Path,
            JsonSerializer.Serialize(new { Process = identity, Build = new { CommitSha = "fixture", RunDirectory = "C:\\run-a" } }));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
