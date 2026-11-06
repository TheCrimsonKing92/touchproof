using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure runner fixtures and privately owned history files.
public sealed class MergeTrainPrefixBisectionTests
{
    internal const string TestName = "Ns.UnchangedTests.Fails";
    private const string Main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourMembersFindThirdWithinThreePrefixesIncludingInteraction(bool interaction)
    {
        var members = Members(4);
        bool Fails(IReadOnlyList<MergeTrainMemberBinding> prefix) => prefix.Contains(members[2]) &&
            (!interaction || prefix.Contains(members[0]));
        if (interaction)
        {
            Assert.False(Fails([members[0]]));
            Assert.False(Fails([members[2]]));
            Assert.True(Fails([members[0], members[2]]));
        }
        var events = new List<MergeTrainPrefixBisection.Run>();
        var result = MergeTrainPrefixBisection.Search(members, Main, [TestName], false, (prefix, main, tests, _) =>
        {
            Assert.Equal(Main, main);
            Assert.Equal(new[] { TestName }, tests);
            Assert.Equal(members.Take(prefix.Count), prefix);
            return Execution(tests, Fails(prefix), prefix.Count);
        }, events.Add, CancellationToken.None);
        Assert.Equal("culprit", result.Outcome);
        Assert.Equal(members[2], result.Member);
        Assert.Equal(new[] { 0, 2, 3 }, events.Select(run => run.PrefixLength));
        Assert.All(events, run => Assert.Equal(1, run.Repetition));
        Assert.Equal(events, result.Runs);
        Assert.False(result.OrdinaryFallback);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryPossibleFirstFailingMemberHasPassingPredecessor(int guilty)
    {
        var members = Members(4);
        var result = MergeTrainPrefixBisection.Search(members, Main, [TestName], false,
            (prefix, _, tests, _) => Execution(tests, prefix.Count > guilty, prefix.Count), _ => { }, CancellationToken.None);
        Assert.Equal(members[guilty], result.Member);
        Assert.Contains(result.Runs, run => run.PrefixLength == guilty && run.Verdict == "passing");
        Assert.InRange(result.Runs.Count, 1, 3);
    }

    [Theory]
    [InlineData("absent", "runner-absent")]
    [InlineData("exception", "runner-failed:IOException")]
    [InlineData("incomplete", "unusable-result")]
    [InlineData("skipped", "unusable-result")]
    [InlineData("apparatus", "unusable-result")]
    [InlineData("unrelated", "unusable-result")]
    [InlineData("widened", "unusable-result")]
    public void UnavailableEvidenceNeverNamesMember(string mode, string reason)
    {
        var events = new List<MergeTrainPrefixBisection.Run>();
        var result = MergeTrainPrefixBisection.Search(Members(3), Main, [TestName], false,
            mode == "absent" ? null : (_, _, tests, _) => mode switch
            {
                "exception" => throw new IOException("composition unavailable"),
                "incomplete" => new(new([], []), [], "incomplete"),
                "skipped" => new(new(tests, [new(TestName, "NotExecuted", "skipped", null)]), [], "skip"),
                "apparatus" => null,
                "widened" => new(new([.. tests, "Ns.OtherTests.Passes"], []), [], "widened"),
                _ => new(new(tests, [new("Ns.OtherTests.Fails", "Failed", "other", null)]), [], "unrelated")
            }, events.Add, CancellationToken.None);
        Assert.Equal("prefix-bisection unavailable", result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Member);
        Assert.Equal(mode == "absent" ? 0 : 1, events.Count);
    }

    [Fact]
    public void UnrepresentableSelectionNeverInvokesRunner()
    {
        var result = MergeTrainPrefixBisection.Search(Members(3), Main, ["not a fully qualified identity"], false,
            (_, _, _, _) => throw new Xunit.Sdk.XunitException("runner must not execute"),
            _ => throw new Xunit.Sdk.XunitException("no invocation event"), CancellationToken.None);
        Assert.Equal("unselectable-tests", result.Reason);
        Assert.Null(result.Member);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlakeDisagreementAndConsistentSplitStopWithoutAttribution(bool split)
    {
        string[] tests = [TestName, "Ns.OtherTests.Fails"];
        var invocation = 0;
        var result = MergeTrainPrefixBisection.Search(Members(3), Main, tests, true, (_, _, selected, _) =>
        {
            invocation++;
            return new(new(selected, split || invocation == 1 ? [new(TestName, "Failed", "failed", null)] : []),
                [$"run-{invocation}.trx"], $"run-{invocation}");
        }, _ => { }, CancellationToken.None);
        Assert.Equal(split ? "inconclusive-split" : "inconclusive-flake", result.Outcome);
        Assert.True(result.OrdinaryFallback);
        Assert.Null(result.Member);
        Assert.Equal(2, invocation);
        Assert.Contains(TestName, result.Reason);
        Assert.Contains("run-1", result.Reason);
        Assert.Contains("run-2", result.Reason);
    }

    [Fact]
    public void RedMainCannotBeAttributedToAnyMember()
    {
        var result = MergeTrainPrefixBisection.Search(Members(3), Main, [TestName], false,
            (prefix, _, tests, _) => Execution(tests, true, prefix.Count), _ => { }, CancellationToken.None);
        Assert.Equal("main-baseline-red", result.Outcome);
        Assert.True(result.OrdinaryFallback);
        Assert.Null(result.Member);
        Assert.Equal(0, Assert.Single(result.Runs).PrefixLength);
    }

    [Theory]
    [InlineData(false, false, 0, false)]
    [InlineData(false, true, 0, true)]
    [InlineData(true, true, 14, true)]
    [InlineData(true, true, 15, false)]
    [InlineData(false, true, -1, false)]
    public void HistoryFlagsOnlyOtherGoalsWithinTheInclusiveWindow(bool ledger, bool external, int days, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "prefix-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var members = Members(3);
            var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
            var goal = external ? GoalId.New() : members[1].GoalId;
            var census = Path.Combine(root, "census.jsonl");
            if (ledger)
                new AcceptanceLaneFlakeLedger(root).Append(new(goal.Value, "attempt", "Remainder", "tree", "first", [],
                    [TestName], "failed", "rerun", [], [], null, "flake", now.AddDays(-days)));
            else
                File.WriteAllText(census, JsonSerializer.Serialize(new AcceptanceFailingTestIndexRecord(1, "gate-failure",
                    goal.Value, now.AddDays(-days), TestIdentity: TestName, InsideChangedPaths: false),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
            var history = new CrossGoalTestFailureHistory(census, root, () => []);
            Assert.Equal(expected, MergeTrainPrefixBisection.HasExternalHistory(history, members, [TestName], now));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CancellationPropagates()
    {
        var events = new List<MergeTrainPrefixBisection.Run>();
        Assert.Throws<OperationCanceledException>(() => MergeTrainPrefixBisection.Search(Members(3), Main,
            [TestName], false, (_, _, _, _) => throw new OperationCanceledException(), events.Add, CancellationToken.None));
        Assert.Equal("cancelled", Assert.Single(events).Verdict);
    }

    internal static MergeTrainMemberBinding[] Members(int count) => Enumerable.Range(1, count).Select(index =>
        new MergeTrainMemberBinding(new GoalId(index.ToString("x8").PadRight(32, '0')), new string((char)('b' + index), 40),
            new string((char)('b' + index), 40), [$"src/Member{index}.cs"], [$"member:{index}"], ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected")).ToArray();

    internal static MergeTrainPrefixBisection.RunResult Execution(IReadOnlyList<string> tests, bool fails, int prefix) =>
        new(new(tests, fails ? tests.Select(test => new AcceptanceTrxFailure(test, "Failed", "confirmed failure", null)).ToArray() : []),
            [$"prefix-{prefix}.trx"], $"prefix-{prefix}");
}
