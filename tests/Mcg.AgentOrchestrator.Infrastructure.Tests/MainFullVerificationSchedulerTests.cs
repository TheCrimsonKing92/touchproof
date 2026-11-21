using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.MainFullVerificationScheduler.Decision;

// Parallel-safe: private workspace and databases, injected clock and verifier.
public sealed class MainFullVerificationSchedulerTests
{
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RequestsFullGateOncePerCommitAndIntervalAcrossRestarts()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        var path = workspace.RunEventStorePath;
        _ = new SqliteRunEventStore(path);
        var store = new MainFullVerificationStore(path);
        var items = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var verifier = new RecordingVerifier();
        var checkout = workspace.ExecutionDirectory;
        Git(checkout, "init", "-b", "main");
        var firstCommit = Commit(checkout, "first");
        var secondCommit = Commit(checkout, "second");
        var now = Start;
        var tip = firstCommit;
        MainFullVerificationCoordinator Create() => new(store, items, 21600, () => tip,
            (commit, _) => new((_, token) =>
            {
                Git(checkout, "checkout", "--detach", commit);
                MainFullVerificationRunner.RunAttempt(verifier, checkout, commit, null, token, null, "main");
                return new("passed");
            }), () => now, _ => { });

        using (var first = Create())
        {
            Assert.Equal(Admit, first.Service(0, () => true));
            await Finish(first);
            Assert.Equal(NotDue, first.Service(0, () => true));
        }
        using var restarted = Create();
        tip = secondCommit;
        now = Start.AddHours(6).AddSeconds(-1);
        Assert.Equal(NotDue, restarted.Service(0, () => true));
        Assert.False(store.Contains(secondCommit, "admitted"));
        now = Start.AddHours(6);
        Assert.Equal(Admit, restarted.Service(0, () => true));
        await Finish(restarted);

        tip = firstCommit;
        now = Start.AddHours(12);
        Assert.Equal(NotDue, restarted.Service(0, () => true));
        Assert.Collection(verifier.Calls,
            call => AssertFullSuite(call, firstCommit),
            call => AssertFullSuite(call, secondCommit));
    }

    [Fact]
    public async Task WaitingGoalAndUnavailableCapacityDeferWithoutConsumingCommit()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var home = fixture.Workspace.OrchestratorDirectory;
        var path = fixture.Workspace.RunEventStorePath;
        _ = new SqliteRunEventStore(path);
        var store = new MainFullVerificationStore(path);
        var reservations = 0;
        using var coordinator = new MainFullVerificationCoordinator(store, CollaborationItemStore.ForDirectory(home),
            21600, () => First, (_, _) =>
            {
                reservations++;
                return new((_, _) => new("passed"));
            }, () => Start, _ => { });
        var capacityProbes = 0;

        Assert.Equal(Deferred, coordinator.Service(1, () => { capacityProbes++; return true; }));
        Assert.Equal(0, capacityProbes);
        Assert.Equal(Deferred, coordinator.Service(0, () => false));
        Assert.Equal(0, reservations);
        Assert.Null(store.Latest("admitted"));
        Assert.Equal(Admit, coordinator.Service(0, () => true));
        await Finish(coordinator);
        Assert.Equal(1, reservations);
        Assert.Equal(First, store.Latest("completed")?.Commit);
    }

    [Fact]
    public void StartRecoversInterruptedAttemptWithoutRequestingTheCommitAgain()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.RunEventStorePath;
        _ = new SqliteRunEventStore(path);
        var store = new MainFullVerificationStore(path);
        var record = new MainFullVerificationRecord(First, Start, 21600, StartedAt: Start.AddSeconds(5));
        store.Write("admitted", record with { StartedAt = null }, Start);
        store.Write("started", record, record.StartedAt!.Value);
        var requests = 0;
        using var coordinator = new MainFullVerificationCoordinator(store,
            CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory), 21600, () => First,
            (_, _) => { requests++; return null; }, () => Start.AddHours(7), _ => { });

        Assert.Equal(NotDue, coordinator.Service(0, () => true));
        Assert.Equal(0, requests);
        var completed = Assert.IsType<MainFullVerificationRecord>(store.Latest("completed"));
        Assert.Equal("interrupted", completed.Result?.Outcome);
        Assert.Equal(record.StartedAt, completed.StartedAt);
    }

    [Theory]
    [InlineData("{}", 21600, false)]
    [InlineData("{\"mainFullVerificationIntervalSeconds\":3600}", 3600, false)]
    [InlineData("{\"mainFullVerificationIntervalSeconds\":0}", 21600, true)]
    [InlineData("{\"mainFullVerificationIntervalSeconds\":1.5}", 21600, true)]
    public void PolicyIntervalRequiresPositiveWholeSeconds(string json, int expected, bool warns)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = Path.Combine(fixture.Workspace.ExecutionDirectory, "conductor-policy.json");
        var lines = new List<string>();
        File.WriteAllText(path, json);
        Assert.Equal(expected, MainFullVerificationSettings.ReadIntervalSeconds(path, lines.Add));
        Assert.Equal(warns ? 1 : 0, lines.Count);
    }

    private static async Task Finish(MainFullVerificationCoordinator coordinator)
    {
        var task = Assert.IsAssignableFrom<Task>(coordinator.ActiveExecution);
        await task.WaitAsync(TimeSpan.FromSeconds(30));
        coordinator.Poll();
    }

    private static void AssertFullSuite(
        (string RunId, string MainSha, GoalId? Goal, IReadOnlyList<string>? ChangedFiles) call, string commit)
    {
        Assert.Equal(MainFullVerificationRunner.RunId(commit), call.RunId);
        Assert.Equal(commit, call.MainSha);
        Assert.Null(call.Goal);
        Assert.Null(call.ChangedFiles);
    }

    private static string Commit(string checkout, string message)
    {
        Git(checkout, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "--allow-empty", "--no-verify", "--no-gpg-sign", "-m", message);
        return Git(checkout, "rev-parse", "HEAD");
    }

    private static string Git(string checkout, params string[] arguments)
    {
        var result = GitCli.Run(checkout, arguments);
        if (!result.Succeeded || result.DrainTimedOut)
            throw new InvalidOperationException($"Git {arguments[0]} failed: {result.Error}");
        return result.Output.Trim();
    }

    private sealed class RecordingVerifier : IGoalAcceptanceVerifier
    {
        internal List<(string RunId, string MainSha, GoalId? Goal, IReadOnlyList<string>? ChangedFiles)> Calls { get; } = [];

        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            var identity = ((AcceptanceAttemptExecutionOwner)executionOwner).Identity;
            Calls.Add((identity.AttemptId, identity.MainSha, goalId, changedFiles));
            return Task.FromResult(new AcceptanceVerificationResult(true, false, 0, null));
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) =>
            throw new InvalidOperationException("A main full verification must run the whole gate.");
    }
}
