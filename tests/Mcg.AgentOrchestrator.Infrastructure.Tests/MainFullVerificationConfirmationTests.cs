using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.MainFullVerificationRerunStatus;

public sealed class MainFullVerificationConfirmationTests
{
    private const string First = "Example.Tests.First";
    private const string Second = "Example.Tests.Second";
    private const string Third = "Example.Tests.Third";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryDistinctFailureIsRerunOnce()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var names = Enumerable.Range(1, 6).Select(i => $"Example.Tests.Failure{i}").ToArray();
        var verification = ReadFailures(fixture, [.. names, names[0]]);
        var reruns = new List<string>();
        Conclude(verification, (test, _) =>
        {
            reruns.Add(test);
            return new(Confirmed);
        });

        Assert.Equal(names, reruns);
    }

    [Fact]
    public void MixedRerunsPreserveOnlyConfirmedFailuresWithAttribution()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var verification = ReadFailures(fixture, First, Second, Third);
        var result = Conclude(verification, (test, _) => new(test == Second ? Confirmed : Unconfirmed));

        Assert.Equal("failed", result.Outcome);
        Assert.Equal(new MainFullVerificationFailure(Second, "landing", "goal"), Assert.Single(result.Failures!));
        Assert.Equal([First, Third], result.Unconfirmed);
    }

    [Fact]
    public void AllPassingRerunsProducePassedWithUnconfirmed()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var verification = ReadFailures(fixture, First, Second);
        var result = Conclude(verification, (_, _) => new(Unconfirmed));

        Assert.Equal("passed-with-unconfirmed", result.Outcome);
        Assert.Empty(result.Failures!);
        Assert.Equal([First, Second], result.Unconfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCheckWithoutTestIdentityKeepsRunIncompleteAfterRerun(bool rerunFails)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        const string unnamedCheck = "tests-with-missing-results";
        var absent = Path.Combine(fixture.Workspace.ExecutionDirectory, "absent.trx");
        var verification = ReadFailures(fixture, First);
        verification = verification with
        {
            Checks = [.. verification.Checks!, new(unnamedCheck, false, 1, null, TestResultPaths: [absent])]
        };
        var reruns = new List<string>();

        var result = Conclude(verification, (test, _) =>
        {
            reruns.Add(test);
            return new(rerunFails ? Confirmed : Unconfirmed);
        });

        Assert.Equal("incomplete", result.Outcome);
        Assert.Contains(unnamedCheck, result.Detail);
        Assert.Equal([First], reruns);
        Assert.Equal(rerunFails ? Array.Empty<string>() : [First], result.Unconfirmed);
        Assert.Null(result.Failures);
    }

    [Fact]
    public async Task CoordinatorReportsUnconfirmedNamesWithoutPublishingHold()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var (store, items) = Stores(fixture);
        var verification = ReadFailures(fixture, First, Second);
        var lines = new List<string>();
        using var coordinator = new MainFullVerificationCoordinator(store, items, 21600, () => "current",
            (_, _) => new((_, token) => Conclude(verification, (_, _) => new(Unconfirmed), token)), () => Now, lines.Add);

        Assert.Equal(MainFullVerificationScheduler.Decision.Admit, coordinator.Service(0, () => true));
        // An older failure can still be awaiting publication when the current run completes.
        store.Write("completed", new("older", Now.AddDays(-1), 21600, CompletedAt: Now.AddDays(-1),
            Result: new("failed", Failures: [new(Third)], Unconfirmed: [])), Now.AddDays(-1));
        await Finish(coordinator);

        var result = store.Latest("completed")!.Result!;
        Assert.Equal("passed-with-unconfirmed", result.Outcome);
        Assert.Equal([First, Second], result.Unconfirmed);
        Assert.DoesNotContain(lines, line => line.StartsWith("MAIN_FULL_VERIFICATION_HOLD ", StringComparison.Ordinal));
        Assert.Empty(await items.ListAsync());
        var progress = Assert.Single(lines.Where(line =>
            line.StartsWith("MAIN_FULL_VERIFICATION result=passed-with-unconfirmed ", StringComparison.Ordinal)));
        Assert.Contains(First, progress);
        Assert.Contains(Second, progress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableRerunMakesRunIncompleteWithoutPublishingHold(bool executionThrows)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var (store, items) = Stores(fixture);
        var verification = ReadFailures(fixture, First, Second, Third);
        var lines = new List<string>();
        using var coordinator = new MainFullVerificationCoordinator(store, items, 21600, () => "current",
            (_, _) => new((_, token) => Conclude(verification, (test, _) => test switch
            {
                First when executionThrows => throw new IOException("Rerun artifacts are unavailable."),
                First => new(Unavailable, "no readable result"),
                Second => new(Confirmed),
                _ => new(Unconfirmed)
            }, token)), () => Now, lines.Add);

        Assert.Equal(MainFullVerificationScheduler.Decision.Admit, coordinator.Service(0, () => true));
        await Finish(coordinator);

        var result = store.Latest("completed")!.Result!;
        Assert.Equal("incomplete", result.Outcome);
        Assert.Contains(First, result.Detail);
        Assert.Null(result.Failures);
        Assert.Equal([Third], result.Unconfirmed);
        Assert.DoesNotContain(lines, line => line.StartsWith("MAIN_FULL_VERIFICATION_HOLD ", StringComparison.Ordinal));
        Assert.Empty(await items.ListAsync());
    }

    [Fact]
    public async Task PassedWithUnconfirmedIsTheLatestCleanCommitForAttribution()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var (store, items) = Stores(fixture);
        var verification = ReadFailures(fixture, First);
        store.Write("completed", new("older", Now.AddDays(-1), 21600, CompletedAt: Now.AddDays(-1),
            Result: new("passed")), Now.AddDays(-1));
        var tip = "first";
        var now = Now;
        string? attributedFrom = null;
        using var coordinator = new MainFullVerificationCoordinator(store, items, 21600, () => tip,
            (commit, _) => new((lastPassed, token) =>
            {
                if (commit == "first")
                    return Conclude(verification, (_, _) => new(Unconfirmed), token);
                attributedFrom = lastPassed;
                return new("passed");
            }), () => now, _ => { });

        Assert.Equal(MainFullVerificationScheduler.Decision.Admit, coordinator.Service(0, () => true));
        await Finish(coordinator);
        Assert.Equal("passed-with-unconfirmed", store.Latest("completed")!.Result!.Outcome);
        tip = "second";
        now = now.AddHours(6);
        Assert.Equal(MainFullVerificationScheduler.Decision.Admit, coordinator.Service(0, () => true));
        await Finish(coordinator);

        Assert.Equal("first", attributedFrom);
    }

    [Theory]
    [InlineData(First, "Passed", true, "Unconfirmed")]
    [InlineData(First, "Failed", false, "Confirmed")]
    [InlineData(First, "Skipped", true, "Unavailable")]
    [InlineData(First, "Inconclusive", false, "Unavailable")]
    [InlineData(Second, "Passed", true, "Unavailable")]
    [InlineData(Second, "Failed", false, "Unavailable")]
    public void FocusedEvidenceMustShowTheRequestedTestOutcome(
        string reportedTest, string outcome, bool passed, string expected)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var trx = WriteTrx(fixture, outcome, [reportedTest]);
        var result = new FocusedEvidenceRunResult("request", true, passed, "focused",
            [new("tests", passed, passed ? 0 : 1, null, TestResultPaths: [trx], ExecutedTestCount: 1)]);

        var confirmation = MainFullVerificationConfirmation.Classify(result, new("request", [First]), First);

        Assert.Equal(expected, confirmation.Status.ToString());
    }

    private static MainFullVerificationResult Conclude(AcceptanceVerificationResult verification,
        Func<string, CancellationToken, MainFullVerificationRerun> rerun, CancellationToken token = default) =>
        MainFullVerificationRunner.Conclude(verification,
            tests => tests.Select(test => new MainFullVerificationFailure(test, "landing", "goal")).ToArray(), rerun, token);

    private static AcceptanceVerificationResult ReadFailures(ConductorVerbStartTests.Fixture fixture, params string[] tests) =>
        new(false, false, 1, null, Checks: [new("tests", false, 1, null, TestResultPaths: [WriteTrx(fixture, "Failed", tests)])]);

    private static string WriteTrx(ConductorVerbStartTests.Fixture fixture, string outcome, IReadOnlyList<string> tests)
    {
        var path = Path.Combine(fixture.Workspace.ExecutionDirectory, "test-results.trx");
        new XElement("TestRun", new XElement("Results", tests.Select((test, i) =>
            new XElement("UnitTestResult", new XAttribute("testId", i), new XAttribute("testName", test),
                new XAttribute("outcome", outcome))))).Save(path);
        return path;
    }

    private static (MainFullVerificationStore Store, CollaborationItemStore Items) Stores(ConductorVerbStartTests.Fixture fixture)
    {
        var workspace = fixture.Workspace;
        _ = new SqliteRunEventStore(workspace.RunEventStorePath);
        return (new(workspace.RunEventStorePath), CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
    }

    private static async Task Finish(MainFullVerificationCoordinator coordinator)
    {
        var execution = Assert.IsAssignableFrom<Task>(coordinator.ActiveExecution);
        await execution.WaitAsync(TimeSpan.FromSeconds(30));
        coordinator.Poll();
        coordinator.Poll();
    }
}
