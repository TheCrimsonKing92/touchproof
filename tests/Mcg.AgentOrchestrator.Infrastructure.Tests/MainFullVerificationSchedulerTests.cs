using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.MainFullVerificationScheduler.Decision;

// Parallel-safe: private workspace and databases, injected clock and verifier.
public sealed class MainFullVerificationSchedulerTests
{
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
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
    public async Task RunExceptionRetriesFromCompletionAcrossRestartAndStopsAtThree()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.RunEventStorePath;
        var events = new SqliteRunEventStore(path);
        var store = new MainFullVerificationStore(path);
        var items = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        var now = Start;
        var tip = First;
        var runs = 0;
        MainFullVerificationCoordinator Create() => new(new MainFullVerificationStore(path), items, 21600, () => tip,
            (commit, _) => new((_, _) =>
            {
                runs++;
                now = now.AddMinutes(2);
                return commit == First ? throw new IOException("run fault") : new("passed");
            }), () => now, _ => { });

        using (var first = Create())
        {
            Assert.Equal(Admit, first.Service(0, () => true));
            await Finish(first);
            var initial = Assert.IsType<MainFullVerificationRecord>(store.Latest("completed"));
            Assert.Equal(Start.AddMinutes(2), initial.CompletedAt);
            Assert.True(initial.Result!.FromRunException);
        }

        using var restarted = Create();
        var lastAdmitted = Start;
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            now = now.AddMinutes(3).AddTicks(-1);
            Assert.Equal(NotDue, restarted.Service(0, () => true));
            now = now.AddTicks(1);
            Assert.Equal(Deferred, restarted.Service(1, () => true));
            Assert.Equal(Deferred, restarted.Service(0, () => false));
            lastAdmitted = now;
            Assert.Equal(Admit, restarted.Service(0, () => true));
            await Finish(restarted);
            Assert.Equal(attempt, store.Latest("completed")?.Attempt);
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            foreach (var operation in new[] { "admitted", "started", "completed" })
                Assert.NotNull(await events.ReadByEventIdAsync($"main-full-verification:{First}:{operation}:{attempt}"));
        }
        now = now.AddMinutes(3);
        Assert.Equal(NotDue, restarted.Service(0, () => true));
        now = lastAdmitted.AddHours(6).AddTicks(-1);
        tip = Second;
        Assert.Equal(NotDue, restarted.Service(0, () => true));
        now = now.AddTicks(1);
        tip = First;
        Assert.Equal(NotDue, restarted.Service(0, () => true));
        Assert.Equal(3, runs);
        tip = Second;
        Assert.Equal(Admit, restarted.Service(0, () => true));
        await Finish(restarted);
        Assert.Equal(1, store.Latest("admitted")?.Attempt);
    }

    [Theory]
    [InlineData("incomplete", false)]
    [InlineData("passed", false)]
    [InlineData("failed", false)]
    [InlineData("incomplete", true)]
    [InlineData("passed", true)]
    [InlineData("failed", true)]
    public async Task ReturnedOutcomeKeepsNormalInterval(string outcome, bool afterException)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.RunEventStorePath;
        _ = new SqliteRunEventStore(path);
        var store = new MainFullVerificationStore(path);
        var items = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        var now = Start;
        var tip = First;
        using var coordinator = new MainFullVerificationCoordinator(store, items, 21600, () => tip,
            (commit, _) => new((_, _) =>
            {
                if (commit == Second) return new("passed");
                if (afterException && now == Start) throw new IOException("run fault");
                return outcome switch
                {
                    "incomplete" => new(outcome, "checkout-unavailable"),
                    "failed" => new(outcome, Failures: [new("Example.Tests.Fail")]),
                    _ => new(outcome)
                };
            }), () => now, _ => { });

        Assert.Equal(Admit, coordinator.Service(0, () => true));
        await Finish(coordinator);
        if (afterException)
        {
            now = now.AddMinutes(3);
            Assert.Equal(Admit, coordinator.Service(0, () => true));
            await Finish(coordinator);
        }

        var completed = Assert.IsType<MainFullVerificationRecord>(store.Latest("completed"));
        Assert.Equal(outcome, completed.Result?.Outcome);
        Assert.False(completed.Result!.FromRunException);
        now = now.AddMinutes(3);
        Assert.Equal(NotDue, coordinator.Service(0, () => true));
        tip = Second;
        now = completed.AdmittedAt.AddHours(6).AddTicks(-1);
        Assert.Equal(NotDue, coordinator.Service(0, () => true));
        now = now.AddTicks(1);
        Assert.Equal(Admit, coordinator.Service(0, () => true));
        await Finish(coordinator);
    }

    [Fact]
    public async Task LegacyIncompleteIsAttemptOneAndDoesNotRetry()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.RunEventStorePath;
        var events = new SqliteRunEventStore(path);
        var admitted = $$"""{"Commit":"{{First}}","AdmittedAt":"{{Start:O}}","IntervalSeconds":21600}""";
        var completed = $$"""{"Commit":"{{First}}","AdmittedAt":"{{Start:O}}","IntervalSeconds":21600,"CompletedAt":"{{Start:O}}","Result":{"Outcome":"incomplete","Detail":"SqliteException: database is locked"} }""";
        await events.AppendAsync(new("conductor.main-full-verification", null, "admitted", null, null,
            admitted, Start, $"main-full-verification:{First}:admitted"));
        await events.AppendAsync(new("conductor.main-full-verification", null, "completed", "incomplete", null,
            completed, Start, $"main-full-verification:{First}:completed"));
        var store = new MainFullVerificationStore(path);
        using var coordinator = new MainFullVerificationCoordinator(store,
            CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory), 21600, () => First,
            (_, _) => throw new InvalidOperationException("Legacy incomplete must not be reserved."),
            () => Start.AddHours(7), _ => { });

        Assert.Equal(NotDue, coordinator.Service(0, () => true));
        var record = Assert.IsType<MainFullVerificationRecord>(store.Latest("completed"));
        Assert.Equal(1, record.Attempt);
        Assert.False(record.Result!.FromRunException);
    }

    [Fact]
    public void StaleReservationCannotReuseAnAttemptNumber()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.RunEventStorePath;
        _ = new SqliteRunEventStore(path);
        var first = new MainFullVerificationStore(path);
        var second = new MainFullVerificationStore(path);

        Assert.Equal(1, first.ReserveAdmission(First, 0, Start, 21600)?.Attempt);
        Assert.Null(second.ReserveAdmission(First, 0, Start, 21600));
    }

    [Fact]
    public void StartRecoversInterruptedRetryDespiteEarlierCompletion()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.RunEventStorePath;
        _ = new SqliteRunEventStore(path);
        var store = new MainFullVerificationStore(path);
        var earlier = new MainFullVerificationRecord(First, Start, 21600);
        store.Write("admitted", earlier, Start);
        store.Write("completed", earlier with
        {
            CompletedAt = Start,
            Result = new("incomplete", "run fault", FromRunException: true)
        }, Start);
        var retry = Start.AddMinutes(3);
        var record = new MainFullVerificationRecord(First, retry, 21600, StartedAt: retry.AddSeconds(5), Attempt: 2);
        store.Write("admitted", record with { StartedAt = null }, retry);
        store.Write("started", record, record.StartedAt!.Value);
        var requests = 0;
        using var coordinator = new MainFullVerificationCoordinator(store,
            CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory), 21600, () => First,
            (_, _) => { requests++; return null; }, () => Start.AddHours(7), _ => { });

        Assert.Equal(NotDue, coordinator.Service(0, () => true));
        Assert.Equal(0, requests);
        var completed = Assert.IsType<MainFullVerificationRecord>(store.Latest("completed"));
        Assert.Equal(2, completed.Attempt);
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
