using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private TRX files and databases, injected clock and verifier.
public sealed class MainFullVerificationHoldEventTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FailingTest = "Example.RedTests.Regression";
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("matched", "newest", "goal-1")]
    [InlineData("unmatched", "undetermined", "none")]
    [InlineData("unreadable", "undetermined", "none")]
    public async Task FailedTrxProducesOperatorHoldWithPinnedCommitAndTest(string reach, string landing, string goal)
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
        using var coordinator = new MainFullVerificationCoordinator(store, items, 21600, () => tip,
            (_, _) => new((_, _) => MainFullVerificationRunner.Interpret(verification, tests =>
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
                }, sha => sha == "newest" ? "goal-1" : "goal-2"))), () => Now, lines.Add);

        Assert.Equal(MainFullVerificationScheduler.Decision.Admit, coordinator.Service(0, () => true));
        tip = "later-main-tip";
        var execution = Assert.IsAssignableFrom<Task>(coordinator.ActiveExecution);
        await execution.WaitAsync(TimeSpan.FromSeconds(30));
        coordinator.Poll();
        coordinator.Poll();

        var hold = Assert.Single(lines.Where(line => line.StartsWith("MAIN_FULL_VERIFICATION_HOLD ", StringComparison.Ordinal)));
        Assert.Equal($"MAIN_FULL_VERIFICATION_HOLD commit={Commit} test={FailingTest} landing={landing} landingGoal={goal} revert=withheld reason=tip-moved", hold);
        Assert.Equal("decision", ConductEventOperatorClassifier.Classify("main-full-verification", hold));
        Assert.True(ConductEventDurabilityPolicy.IsRequired("main-full-verification", hold));
        Assert.Equal(reach == "unmatched" ? new[] { "newest", "older" } : ["newest"], visited);
        Assert.Equal("failed", store.Latest("completed")?.Result?.Outcome);
        var item = Assert.Single(await items.ListAsync());
        Assert.Equal(CollaborationItemType.Verify, item.Type);
        Assert.Equal($"main-full-verification-failure:{Commit}", item.CorrelationKey);
        Assert.Contains(FailingTest, item.Body);
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
