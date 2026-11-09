using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class StorageRetentionAgedTerminalGoalReductionTests
{
    private const string GoalId = RetentionReclaimFixture.CompletedId;
    private const string GateStore = "acceptance-gate-attempts";
    private const string ReviewStore = "pre-review-evidence-attempts";
    private static readonly DateTimeOffset Now = RetentionReclaimFixture.Now;
    private const string PassedTrx = """
        <TestRun><ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
        <Results><UnitTestResult testName="Example.Passes" outcome="Passed" /></Results></TestRun>
        """;
    private const string FailedTrx = """
        <TestRun><ResultSummary><Counters total="1" executed="1" passed="0" failed="1" /></ResultSummary>
        <Results><UnitTestResult testName="Example.Fails" outcome="Failed"><Output><ErrorInfo>
        <Message>Expected success</Message><StackTrace>at Example.Fails()</StackTrace>
        </ErrorInfo></Output></UnitTestResult></Results></TestRun>
        """;

    [Xunit.Theory]
    [Xunit.InlineData(GateStore, GoalStatus.Completed)]
    [Xunit.InlineData(ReviewStore, GoalStatus.Completed)]
    [Xunit.InlineData(GateStore, GoalStatus.Cancelled)]
    [Xunit.InlineData(ReviewStore, GoalStatus.Cancelled)]
    [Xunit.InlineData(GateStore, GoalStatus.Superseded)]
    [Xunit.InlineData(ReviewStore, GoalStatus.Superseded)]
    public void FinalAndLastFailingTrxReducedForGoalTerminal20Days(string store, GoalStatus status)
    {
        using var fixture = new RetentionReclaimFixture();
        var failure = WriteAttempt(fixture, store, "failure", 1, failed: true);
        var final = WriteAttempt(fixture, store, "final", 2);
        var before = ReadTree(Path.Combine(fixture.AttemptRoot(store), GoalId));

        var result = fixture.Run(null, TerminalGoal(fixture, 20, status));

        foreach (var attempt in new[] { failure, final })
        {
            Assert.False(File.Exists(attempt.Trx));
            using var receipt = JsonDocument.Parse(File.ReadAllText(attempt.Trx + ".test-identities.json"));
            Assert.Equal(GoalId, receipt.RootElement.GetProperty("goalId").GetString());
            Assert.Equal(attempt.Id, receipt.RootElement.GetProperty("attemptId").GetString());
            var identity = Assert.Single(receipt.RootElement.GetProperty("testIdentities").EnumerateArray());
            Assert.Equal(attempt == failure ? "Example.Fails" : "Example.Passes", identity.GetProperty("name").GetString());
            if (attempt == failure)
            {
                Assert.Equal("Expected success", identity.GetProperty("failureMessage").GetString());
                Assert.Equal("at Example.Fails()", identity.GetProperty("topStackFrame").GetString());
            }
            Assert.Contains(result.Decisions, decision => decision.Path == attempt.Trx + ".test-identities.json" &&
                decision.Action == EvidenceRetentionAction.ReceiptWritten);
            Assert.Contains(result.Decisions, decision => decision.Path == attempt.Trx &&
                decision.Action == EvidenceRetentionAction.Deleted && decision.Reason == "trx-reduced-to-receipt" &&
                decision.OwnershipSource == EvidenceOwnershipSource.Declared);
        }
        // Every original artifact other than the two TRX must survive byte-for-byte, including results and verdicts.
        foreach (var (path, content) in before.Where(file => file.Key != failure.Trx && file.Key != final.Trx))
            Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(2, result.SuccessfulTrxReceiptsWritten);
        Assert.All(result.Decisions.Where(decision => decision.Action == EvidenceRetentionAction.Deleted),
            decision => Assert.Contains(decision.Path, new[] { failure.Trx, final.Trx }));
    }

    [Xunit.Theory]
    [Xunit.InlineData(GateStore, 10)]
    [Xunit.InlineData(ReviewStore, 10)]
    [Xunit.InlineData(GateStore, 14)]
    [Xunit.InlineData(ReviewStore, 14)]
    public void GoalTerminalTenDaysKeepsFinalAttemptTrx(string store, int terminalDays)
    {
        using var fixture = new RetentionReclaimFixture();
        WriteAttempt(fixture, store, "failure", 1, failed: true);
        WriteAttempt(fixture, store, "final", 2);
        var directory = Path.Combine(fixture.AttemptRoot(store), GoalId);
        var before = ReadTree(directory);

        var result = fixture.Run(null, TerminalGoal(fixture, terminalDays));

        AssertTreeUnchanged(before, directory);
        Assert.DoesNotContain(result.Decisions, decision => decision.Reason == "trx-reduced-to-receipt");
    }

    [Xunit.Theory]
    [Xunit.InlineData(GateStore)]
    [Xunit.InlineData(ReviewStore)]
    public void NonTerminalGoalKeepsEverything(string store)
    {
        using var fixture = new RetentionReclaimFixture();
        WriteAttempt(fixture, store, "final", 1);
        var directory = Path.Combine(fixture.AttemptRoot(store), GoalId);
        var before = ReadTree(directory);

        var result = fixture.Run(null, TerminalGoal(fixture, 20, GoalStatus.Active));

        AssertTreeUnchanged(before, directory);
        Assert.Contains(result.Decisions, decision => decision.Reason == "non-terminal-evidence-unbounded-by-policy");
    }

    [Xunit.Theory]
    [Xunit.InlineData(GateStore)]
    [Xunit.InlineData(ReviewStore)]
    public void FileUnderUnknownGoalDirectoryIsRetained(string store)
    {
        using var fixture = new RetentionReclaimFixture();
        WriteAttempt(fixture, store, "final", 1);
        var directory = Path.Combine(fixture.AttemptRoot(store), GoalId);
        var before = ReadTree(directory);

        var result = fixture.Run(null, fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active));

        AssertTreeUnchanged(before, directory);
        Assert.DoesNotContain(result.Decisions, decision => decision.Reason == "trx-reduced-to-receipt");
    }

    [Xunit.Theory]
    [Xunit.InlineData(GateStore)]
    [Xunit.InlineData(ReviewStore)]
    public void UnresolvedOwnerUnderGoalAttemptDirectoryIsAttributedToGoal(string store)
    {
        using var fixture = new RetentionReclaimFixture();
        var attempt = WriteAttempt(fixture, store, "final", 1, declareTrx: false);
        var shadowLog = Path.Combine(Path.GetDirectoryName(attempt.Trx)!, "final-console.log");
        WriteFresh(shadowLog, "undeclared log");
        var directTrx = Path.Combine(fixture.AttemptRoot(store), GoalId, "unresolved.trx");
        WriteFresh(directTrx, PassedTrx);

        var result = fixture.Run(null, TerminalGoal(fixture, 20));

        Assert.False(File.Exists(attempt.Trx));
        using var receipt = JsonDocument.Parse(File.ReadAllText(attempt.Trx + ".test-identities.json"));
        var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(attempt.Trx)));
        Assert.Equal(folder, receipt.RootElement.GetProperty("attemptId").GetString());
        Assert.Contains(result.Decisions, decision => decision.Path == attempt.Trx &&
            decision.Reason == "trx-reduced-to-receipt" &&
            decision.OwnershipSource == EvidenceOwnershipSource.InferredFromDirectory);
        Assert.Contains(result.Decisions, decision => decision.Path == shadowLog &&
            decision.Action == EvidenceRetentionAction.Preserved && decision.Reason == "goal-attributed-artifact-retained" &&
            decision.GoalId == GoalId && decision.OwnershipSource == EvidenceOwnershipSource.InferredFromDirectory);
        Assert.Equal("undeclared log", File.ReadAllText(shadowLog));
        Assert.Equal("result", File.ReadAllText(attempt.Result));
        Assert.Equal(PassedTrx, File.ReadAllText(directTrx));
        Assert.Contains(result.Decisions, decision => decision.Path == directTrx &&
            decision.Reason == "trx-attempt-ownership-unresolved");
    }

    [Xunit.Theory]
    [Xunit.InlineData(10)]
    [Xunit.InlineData(14)]
    public void RecentTerminalGoalKeepsUnresolvedOwnership(int terminalDays)
    {
        using var fixture = new RetentionReclaimFixture();
        var attempt = WriteAttempt(fixture, GateStore, "final", 1, declareTrx: false);
        var shadowLog = Path.Combine(Path.GetDirectoryName(attempt.Trx)!, "final-console.log");
        WriteFresh(shadowLog, "undeclared log");

        var result = fixture.Run(null, TerminalGoal(fixture, terminalDays));

        Assert.Equal(PassedTrx, File.ReadAllText(attempt.Trx));
        Assert.Contains(result.Decisions, decision => decision.Path == attempt.Trx &&
            decision.Reason == "trx-attempt-ownership-unresolved");
        Assert.Contains(result.Decisions, decision => decision.Path == shadowLog &&
            decision.Action == EvidenceRetentionAction.RetainedUndecidable && decision.Reason == "artifact-owner-unresolved");
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void UnreadableTrxKeepsSourceAndAttemptRecords(bool declared)
    {
        using var fixture = new RetentionReclaimFixture();
        var attempt = WriteAttempt(fixture, GateStore, "final", 1, declareTrx: declared);
        WriteFresh(attempt.Trx, "<broken");
        var directory = Path.Combine(fixture.AttemptRoot(), GoalId);
        var before = ReadTree(directory);

        var result = fixture.Run(null, TerminalGoal(fixture, 20));

        AssertTreeUnchanged(before, directory);
        Assert.Contains(result.Decisions, decision => decision.Path == attempt.Trx &&
            decision.Action == EvidenceRetentionAction.Preserved && decision.Reason == "trx-is-unreadable-or-has-no-test-identities");
    }

    [Xunit.Fact]
    public void ReceiptPersistenceFailureKeepsSource()
    {
        using var fixture = new RetentionReclaimFixture();
        var attempt = WriteAttempt(fixture, GateStore, "final", 1);
        Directory.CreateDirectory(attempt.Trx + ".test-identities.json");

        var result = fixture.Run(null, TerminalGoal(fixture, 20));

        Assert.Equal(PassedTrx, File.ReadAllText(attempt.Trx));
        Assert.Equal("result", File.ReadAllText(attempt.Result));
        Assert.Equal(0, result.SuccessfulTrxReceiptsWritten);
        Assert.Contains(result.Decisions, decision => decision.Path == attempt.Trx &&
            decision.Reason == "trx-is-unreadable-or-has-no-test-identities");
    }

    [Xunit.Fact]
    public void AmbiguousDeclaredOwnerKeepsTrx()
    {
        using var fixture = new RetentionReclaimFixture();
        var first = WriteAttempt(fixture, GateStore, "first", 1);
        WriteAttempt(fixture, GateStore, "second", 2, sharedTrx: first.Trx);

        var result = fixture.Run(null, TerminalGoal(fixture, 20));

        Assert.Equal(PassedTrx, File.ReadAllText(first.Trx));
        Assert.False(File.Exists(first.Trx + ".test-identities.json"));
        Assert.Contains(result.Decisions, decision => decision.Path == first.Trx &&
            decision.Reason == "trx-attempt-ownership-ambiguous");
    }

    [Xunit.Theory]
    [Xunit.InlineData("unknown/attempt/run.trx", false)]
    [Xunit.InlineData(GoalId + "/run.trx", false)]
    [Xunit.InlineData("../" + GoalId + "/attempt/run.trx", false)]
    [Xunit.InlineData("unknown/" + GoalId + "/attempt/run.trx", false)]
    [Xunit.InlineData(GoalId + "/attempt/nested/run.trx", true)]
    public void DirectoryFallbackRequiresDirectKnownGoal(string relativePath, bool expected)
    {
        using var fixture = new RetentionReclaimFixture();
        Assert.Equal(expected, AttemptDirectoryGoalOwner.TryResolve(fixture.AttemptRoot(),
            Path.Combine(fixture.AttemptRoot(), relativePath), GoalId, out var segment));
        if (expected) Assert.Equal("attempt", segment);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Failed, 20, 21)]
    [Xunit.InlineData(GoalStatus.Completed, null, 21)]
    [Xunit.InlineData(GoalStatus.Completed, 20, 14)]
    public void MissingOrContradictoryAgeKeepsProtectedTrx(GoalStatus status, int? terminalDays, int startedDays)
    {
        using var fixture = new RetentionReclaimFixture();
        var attempt = WriteAttempt(fixture, GateStore, "final", 1, startedDays: startedDays);
        var goal = fixture.Goal(GoalId, status) with { TerminalAt = terminalDays is { } days ? Now.AddDays(-days) : null };

        fixture.Run(null, goal);

        Assert.Equal(PassedTrx, File.ReadAllText(attempt.Trx));
        Assert.False(File.Exists(attempt.Trx + ".test-identities.json"));
    }

    [Xunit.Fact]
    public void SnapshotTerminalTimeUsesMetadataOrLatestTimeline()
    {
        var snapshot = new GoalSnapshot(GoalId, "goal", GoalStatus.Completed, [],
            [new(GoalId, null, ProgressKind.GoalCreated, "latest", Now.AddDays(-20)),
             new(GoalId, null, ProgressKind.GoalCreated, "earlier", Now.AddDays(-30))]);
        Assert.Equal(Now.AddDays(-20), StorageRetentionGoal.FromSnapshot(snapshot).TerminalAt);
        Assert.Equal(Now.AddDays(-25), StorageRetentionGoal.FromSnapshot(snapshot with
        {
            Timeline = [], IsMetadataOnly = true, TerminatedAt = Now.AddDays(-25)
        }).TerminalAt);
        Assert.Null(StorageRetentionGoal.FromSnapshot(snapshot with { Timeline = [] }).TerminalAt);
        Assert.Null(StorageRetentionGoal.FromSnapshot(snapshot with { Status = GoalStatus.Active }).TerminalAt);
    }

    private static StorageRetentionGoal TerminalGoal(RetentionReclaimFixture fixture, int days,
        GoalStatus status = GoalStatus.Completed) => fixture.Goal(GoalId, status) with { TerminalAt = Now.AddDays(-days) };

    private static AttemptFiles WriteAttempt(RetentionReclaimFixture fixture, string store, string id, int ordinal,
        bool failed = false, bool declareTrx = true, int startedDays = 21, string? sharedTrx = null)
    {
        var goalDirectory = Path.Combine(fixture.AttemptRoot(store), GoalId);
        var directory = Path.Combine(goalDirectory, GoalId[..8] + "-acceptance-" + id);
        var trx = sharedTrx ?? Path.Combine(directory, "nested", "run.trx");
        var result = Path.Combine(directory, "result.json");
        WriteFresh(trx, failed ? FailedTrx : PassedTrx);
        WriteFresh(result, "result");
        WriteFresh(Path.Combine(directory, "receipt.json"), "existing receipt");
        WriteFresh(Path.Combine(directory, "gate-verdict.json"), "verdict");
        WriteFresh(Path.Combine(directory, "payload.txt"), "other content");
        WriteFresh(Path.Combine(goalDirectory, id + ".attempt.json"), JsonSerializer.Serialize(new
        {
            attemptId = id, goalId = GoalId, ordinal, startedAt = Now.AddDays(-startedDays),
            outcome = failed ? 2 : 1, reconciledAt = Now.AddDays(-startedDays), resultPath = result,
            testResultPaths = declareTrx ? new[] { trx } : Array.Empty<string>()
        }));
        return new(id, trx, result);
    }

    private static void WriteFresh(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-1).UtcDateTime);
    }

    private static Dictionary<string, string> ReadTree(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);

    private static void AssertTreeUnchanged(Dictionary<string, string> before, string directory)
    {
        var after = ReadTree(directory);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, content) in before) Assert.Equal(content, after[path]);
    }

    private sealed record AttemptFiles(string Id, string Trx, string Result);
}
