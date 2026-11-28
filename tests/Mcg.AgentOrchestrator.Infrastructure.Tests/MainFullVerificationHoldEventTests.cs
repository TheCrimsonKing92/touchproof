using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.MainFullVerificationRerunStatus;

// Parallel-safe: private TRX files and databases, injected clock and verifier.
public sealed class MainFullVerificationHoldEventTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FailingTest = "Example.RedTests.Regression";
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("matched", "newest", "goal-1", true)]
    [InlineData("unmatched", "undetermined", "none", true)]
    [InlineData("unreadable", "undetermined", "none", true)]
    [InlineData("matched", "newest", "goal-1", false)]
    public async Task FailedTrxProducesOperatorHoldWithPinnedCommitAndTest(string reach, string landing, string goal, bool tipMoves)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        var path = workspace.RunEventStorePath;
        _ = new SqliteRunEventStore(path);
        var store = new MainFullVerificationStore(path);
        var items = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var trx = Path.Combine(workspace.ExecutionDirectory, "red.trx");
        File.WriteAllText(trx, """
            <TestRun>
              <TestDefinitions>
                <UnitTest id="test-id"><TestMethod className="Example.RedTests" name="Regression"/></UnitTest>
              </TestDefinitions>
              <Results><UnitTestResult testId="test-id" testName="Example.RedTests.Regression" outcome="Failed"/></Results>
            </TestRun>
            """);
        var lines = new List<string>();
        var tip = Commit;
        var verification = new AcceptanceVerificationResult(false, false, 1, null,
            Checks: [new("tests", false, 1, null, TestResultPaths: [trx])]);
        var visited = new List<string>();
        var reruns = new List<string>();
        using var coordinator = new MainFullVerificationCoordinator(store, items, 21600, () => tip,
            (_, _) => new((_, token) => MainFullVerificationRunner.Conclude(verification, tests =>
                MainFullVerificationLandingAttribution.Resolve(tests, ["newest", "older"], sha =>
                {
                    visited.Add(sha);
                    return reach switch
                    {
                        "matched" or "unreadable" when sha == "older" => new HashSet<string> { "RedTests" },
                        "matched" => new HashSet<string> { "RedTests" },
                        "unreadable" => null,
                        _ => new HashSet<string> { "OtherTests" }
                    };
                }, sha => sha == "newest" ? "goal-1" : "goal-2"), (test, _) =>
                {
                    reruns.Add(test);
                    return new(Confirmed);
                }, token)), () => Now, lines.Add);

        Assert.Equal(MainFullVerificationScheduler.Decision.Admit, coordinator.Service(0, () => true));
        if (tipMoves) tip = "later-main-tip";
        var execution = Assert.IsAssignableFrom<Task>(coordinator.ActiveExecution);
        await execution.WaitAsync(TimeSpan.FromSeconds(30));
        coordinator.Poll();
        coordinator.Poll();

        var hold = Assert.Single(lines.Where(line => line.StartsWith("MAIN_FULL_VERIFICATION_HOLD ", StringComparison.Ordinal)));
        var reason = tipMoves ? "tip-moved" : "confirmed-by-rerun";
        Assert.Equal($"MAIN_FULL_VERIFICATION_HOLD commit={Commit} test={FailingTest} landing={landing} landingGoal={goal} revert=withheld reason={reason}", hold);
        Assert.Equal([FailingTest], reruns);
        Assert.Equal("decision", ConductEventOperatorClassifier.Classify("main-full-verification", hold));
        Assert.True(ConductEventDurabilityPolicy.IsRequired("main-full-verification", hold));
        Assert.Equal(reach == "unmatched" ? new[] { "newest", "older" } : ["newest"], visited);
        Assert.Equal("failed", store.Latest("completed")?.Result?.Outcome);
        var item = Assert.Single(await items.ListAsync());
        Assert.Equal(CollaborationItemType.Verify, item.Type);
        Assert.Equal($"main-full-verification-failure:{Commit}", item.CorrelationKey);
        Assert.Contains(FailingTest, item.Body);
    }

    [Theory]
    [InlineData(true, "IOException: decision read failed", 3)]
    [InlineData(false, "checkout-unavailable", 1)]
    public async Task IncompleteRaisesOneVerifyItemPerCommitAcrossPollsAndRetries(
        bool throws, string reason, int attempts)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.RunEventStorePath;
        _ = new SqliteRunEventStore(path);
        var items = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        const string failedCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var tip = Commit;
        var now = Now;
        var lines = new List<string>();
        MainFullVerificationCoordinator Create() => new(new MainFullVerificationStore(path), items, 21600, () => tip,
            (commit, _) => new((_, _) =>
            {
                if (commit == failedCommit) return new("failed", Failures: [new(FailingTest)], Unconfirmed: []);
                if (throws) throw new IOException("decision read failed");
                return new("incomplete", reason);
            }), () => now, lines.Add);

        string? itemId = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var coordinator = Create();
            Assert.Equal(MainFullVerificationScheduler.Decision.Admit, coordinator.Service(0, () => true));
            await Assert.IsAssignableFrom<Task>(coordinator.ActiveExecution).WaitAsync(TimeSpan.FromSeconds(30));
            coordinator.Poll();
            coordinator.Poll();
            var item = Assert.Single(await items.ListAsync());
            Assert.Equal(CollaborationItemType.Verify, item.Type);
            Assert.Equal($"main-full-verification-incomplete:{Commit}", item.CorrelationKey);
            Assert.Contains(reason, item.Body);
            Assert.Contains("attempt=1/3", item.Body);
            itemId ??= item.Id;
            Assert.Equal(itemId, item.Id);
            now = now.AddMinutes(3);
        }
        Assert.DoesNotContain(lines, line => line.StartsWith("MAIN_FULL_VERIFICATION_HOLD ", StringComparison.Ordinal));

        tip = failedCommit;
        now = now.AddHours(6);
        using var next = Create();
        Assert.Equal(MainFullVerificationScheduler.Decision.Admit, next.Service(0, () => true));
        await Assert.IsAssignableFrom<Task>(next.ActiveExecution).WaitAsync(TimeSpan.FromSeconds(30));
        next.Poll();
        var attention = await items.ListAsync();
        Assert.Equal(2, attention.Count);
        Assert.Single(attention, item => item.CorrelationKey == $"main-full-verification-failure:{failedCommit}");
        Assert.Single(attention, item => item.CorrelationKey == $"main-full-verification-incomplete:{Commit}");
    }

    [Fact]
    public void InfrastructureFailureWithoutReadableTestIdentityIsIncomplete()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var absent = Path.Combine(fixture.Workspace.ExecutionDirectory, "absent.trx");
        var verification = new AcceptanceVerificationResult(false, false, 1, null,
            Checks: [new("tests", false, 1, null, TestResultPaths: [absent])]);

        var result = MainFullVerificationRunner.Interpret(verification,
            _ => throw new InvalidOperationException("Attribution requires a failing test."));

        Assert.Equal("incomplete", result.Outcome);
        Assert.Null(result.Failures);
    }
}
