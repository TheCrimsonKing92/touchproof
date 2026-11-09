using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private TRX files and in-memory stores; no subprocesses or databases.
public sealed class MergeTrainMemberRecheckFallbackTests
{
    [Theory]
    [InlineData(false, "interaction")]
    [InlineData(true, "all-members-failed")]
    public void UniformResults_KeepFallback_WithoutImplicatingAnyone(bool fail, string reason)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((_, _, selection, _) => fixture.Execution(selection, fail))!;
        AssertFallback(fixture, outcome, reason);
        Assert.Equal(3, fixture.Calls.Count);
        Assert.All(outcome.Members, member => Assert.Equal(fail ? "fail" : "pass", member.Result));
        var sweep = Assert.Single(fixture.Events);
        Assert.Equal("train-attribution", sweep.Kind);
        Assert.Contains("cpu_ms=30", sweep.Detail);
        Assert.All(fixture.Members, member => Assert.Contains($"member={member.GoalId.Value}", sweep.Detail));
    }

    [Theory]
    [InlineData("unreadable", "unreadable-identities")]
    [InlineData("count", "focused-bounds")]
    [InlineData("filter-length", "focused-bounds")]
    [InlineData("malformed-identity", "focused-bounds")]
    public void UnselectableFailures_RecordReason_WithoutRunning(string mode, string reason)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        if (mode == "unreadable")
        {
            var missing = fixture.WriteTrx(("Ns.MissingTests.Case", "Failed"));
            File.Delete(missing);
            fixture.Receipt = fixture.Receipt with { GateTestResultPaths = [.. fixture.Receipt.GateTestResultPaths, missing] };
        }
        else
        {
            var failures = mode switch
            {
                "count" => Enumerable.Range(0, AcceptanceFailureAttributionPlanner.MaxCandidateRerunIdentities + 1)
                    .Select(index => ($"Ns.CulpritTests.Case{index}", "Failed")).ToArray(),
                "filter-length" => new[] { ($"Ns.{new string('X', 1100)}.Case", "Failed") },
                _ => new[] { ("Invalid test identity", "Failed") }
            };
            fixture.Receipt = fixture.Receipt with { GateTestResultPaths = [fixture.WriteTrx(failures)] };
        }
        var outcome = fixture.Run((_, _, _, _) => throw new InvalidOperationException("must not run"))!;
        AssertFallback(fixture, outcome, reason);
        Assert.Empty(fixture.Calls);
        Assert.All(outcome.Members, member => Assert.Equal("unrun", member.Result));
        Assert.Equal("train-member-recheck", Assert.Single(fixture.Events).Kind);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("wrong-main")]
    [InlineData("missing-tree")]
    [InlineData("apparatus")]
    [InlineData("not-run")]
    [InlineData("uncovered")]
    [InlineData("unrelated-failure")]
    [InlineData("failed-check-only")]
    [InlineData("selected-apparatus")]
    public void IncompleteSweep_DiscardsPartialFailures_RecordsUnavailable(string mode)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((member, _, selection, _) =>
        {
            if (member == fixture.Members[0]) return fixture.Execution(selection, true);
            if (mode == "null") return null;
            if (mode == "throw") throw new IOException("member cannot be materialized");
            var run = fixture.Execution(selection, false);
            return mode switch
            {
                "wrong-main" => run with { MainRevision = new string('c', 40) },
                "missing-tree" => run with { Verification = run.Verification with { TreeRevision = null } },
                "apparatus" => run with { Verification = run.Verification with { Kind = ConductorCohortFocusedPassKind.InfrastructureFailure } },
                "not-run" => run with { NotRunSelectionCount = 1 },
                "uncovered" => run with { Verification = run.Verification with {
                    TestResultPaths = [fixture.WriteTrx(("Ns.OtherTests.Case", "Passed"))] } },
                "unrelated-failure" => run with { Verification = run.Verification with {
                    AllChecksPassed = false, FailingIdentities = ["Ns.OtherTests.Case"], FailedChecks = ["focused"],
                    TestResultPaths = [fixture.WriteTrx(("Ns.CulpritTests.Case", "Passed"), ("Ns.OtherTests.Case", "Failed"))] } },
                "failed-check-only" => run with { Verification = run.Verification with { AllChecksPassed = false, FailedChecks = ["focused"] } },
                _ => run with { Verification = run.Verification with { Kind = ConductorCohortFocusedPassKind.InfrastructureFailure,
                    AllChecksPassed = false, TestResultPaths = [fixture.WriteTrx(("Ns.CulpritTests.Case", "Failed"))] } }
            };
        })!;
        AssertFallback(fixture, outcome, "execution-failure");
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(new[] { "fail", "unavailable", "unrun" }, outcome.Members.Select(member => member.Result));
        Assert.Null(outcome.CpuMillisecondsTotal);
        Assert.Equal("train-attribution", Assert.Single(fixture.Events).Kind);
        Assert.Contains("result=unavailable", fixture.Events[0].Detail);
        Assert.Contains("result=unrun cpu_ms=unavailable", fixture.Events[0].Detail);
    }

    [Fact]
    public void NoCompletedMember_UsesAttemptEvent_WithoutAttributionEvent()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((_, _, _, _) => null)!;
        AssertFallback(fixture, outcome, "execution-failure");
        Assert.Single(fixture.Calls);
        Assert.Equal("train-member-recheck", Assert.Single(fixture.Events).Kind);
        Assert.Equal(new[] { "unavailable", "unrun", "unrun" }, outcome.Members.Select(member => member.Result));
    }

    [Fact]
    public void Cancellation_Propagates_WithoutSavingOrApplyingPartialResults()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        Assert.Throws<OperationCanceledException>(() => fixture.Run((member, _, selection, _) =>
            member == fixture.Members[0] ? fixture.Execution(selection, true) : throw new OperationCanceledException()));
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Empty(fixture.Implicated);
        Assert.Empty(fixture.Ejections);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Cache.Count);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("all-fail")]
    [InlineData("reason")]
    public void UnsafeCachedOutcome_FailsLoudly_WithoutEffects(string defect)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        fixture.Run((member, _, selection, _) => fixture.Execution(selection, member == fixture.Members[0]));
        fixture.Implicated.Clear();
        fixture.Ejections.Clear();
        fixture.Events.Clear();
        fixture.Cache.OnRead = saved => defect switch
        {
            "member" => saved with { Members = saved.Members.Select(member => member with { CandidateRevision = "wrong" }).ToArray() },
            "all-fail" => saved with { Members = saved.Members.Select(member => member with { Result = "fail" }).ToArray() },
            _ => saved with { FallbackReason = "unknown" }
        };
        Assert.Throws<InvalidDataException>(() => fixture.Run((_, _, _, _) => null));
        Assert.Empty(fixture.Implicated);
        Assert.Empty(fixture.Ejections);
        Assert.Empty(fixture.Events);
        Assert.Equal(3, fixture.Calls.Count);
    }

    private static void AssertFallback(MergeTrainMemberRecheckFixture fixture,
        MergeTrainMemberRecheck.Outcome outcome, string reason)
    {
        Assert.Equal(reason, outcome.FallbackReason);
        Assert.Empty(outcome.Implicated(fixture.Receipt));
        Assert.Empty(outcome.Ejections(fixture.Receipt));
        Assert.Equal(fixture.Members, outcome.Remaining(fixture.Receipt));
        Assert.Empty(fixture.Implicated);
        Assert.Empty(fixture.Ejections);
        Assert.Contains($"reason={reason}", Assert.Single(fixture.Events).Detail);
    }
}
