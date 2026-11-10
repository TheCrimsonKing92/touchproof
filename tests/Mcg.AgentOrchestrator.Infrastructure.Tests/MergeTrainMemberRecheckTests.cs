using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: scenario-owned TRX files and in-memory stores; no real gates or git.
public sealed class MergeTrainMemberRecheckTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ProperFailingSubset_ImplicatesAfterAllRuns_LeavesPassingMembers(int failingCount)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        fixture.Cache.BeforeSave = () =>
        {
            Assert.Equal(fixture.Members, fixture.Calls);
            Assert.Empty(fixture.Implicated);
            Assert.Empty(fixture.Ejections);
            Assert.Empty(fixture.Events);
        };
        var outcome = fixture.Run((member, _, selection, _) =>
        {
            Assert.Empty(fixture.Implicated);
            Assert.Empty(fixture.Ejections);
            Assert.Equal(new[] { "Ns.CulpritTests" }, selection.Identities);
            Assert.Equal("FullyQualifiedName~Ns.CulpritTests", selection.Request);
            var index = Array.IndexOf(fixture.Members, member);
            return fixture.Execution(selection, index < failingCount, (index + 1) * 10);
        })!;
        Assert.Null(outcome.FallbackReason);
        Assert.Equal(fixture.Members, fixture.Calls);
        Assert.Equal(fixture.Members.Take(failingCount).Select(member => member.GoalId), fixture.Implicated.Keys);
        Assert.All(fixture.Implicated.Values, source => Assert.Equal("member-recheck", source));
        Assert.Equal(fixture.Members.Skip(failingCount), outcome.Remaining(fixture.Receipt));
        Assert.Equal(failingCount, fixture.Ejections.Count);
        Assert.All(fixture.Ejections, ejection =>
        {
            Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
            Assert.Equal($"Dropped after RED receipt {fixture.Receipt.ReceiptId}.", ejection.Detail);
        });
        var sweep = Assert.Single(fixture.Events);
        Assert.Equal("train-attribution", sweep.Kind);
        Assert.Contains("attribution=member-recheck", sweep.Detail);
        Assert.Contains("reason=attributed cpu_ms=60", sweep.Detail);
        Assert.Equal(60, outcome.CpuMillisecondsTotal);
        for (var index = 0; index < 3; index++)
            Assert.Contains($"member={fixture.Members[index].GoalId.Value} " +
                $"candidate_revision={fixture.Members[index].CandidateRevision} " +
                $"result={(index < failingCount ? "fail" : "pass")} cpu_ms={(index + 1) * 10}", sweep.Detail);
    }

    [Fact]
    public void Replay_UsesSavedSweep_DoesNotRunOrAnnounceAgain()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var first = fixture.Run((member, _, selection, _) => fixture.Execution(selection, member == fixture.Members[0]))!;
        // Cached classification survives disposal of the original gate's transient files.
        foreach (var path in fixture.Receipt.GateTestResultPaths) File.Delete(path);
        var replayCalls = 0;
        var replay = fixture.Run((_, _, _, _) => { replayCalls++; return null; })!;
        Assert.Equal(0, replayCalls);
        Assert.Equal(first.Members, replay.Members);
        Assert.Equal(first.Implicated(fixture.Receipt), replay.Implicated(fixture.Receipt));
        Assert.Single(fixture.Events);
        Assert.Equal(3, fixture.Calls.Count);
        Assert.Single(fixture.Implicated);
        Assert.Equal(1, fixture.Cache.Count);
    }

    [Fact]
    public void MissingCpu_ReportsUnavailable_StillAttributesFromTestEvidence()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((member, _, selection, _) =>
            fixture.Execution(selection, member == fixture.Members[0], cpu: null))!;
        Assert.Single(fixture.Implicated);
        Assert.Null(outcome.CpuMillisecondsTotal);
        Assert.Contains("cpu_ms=unavailable", Assert.Single(fixture.Events).Detail);
    }

    [Fact]
    public void CpuAccounting_SumsResourceReceipts_RequiresEveryCheck()
    {
        AcceptanceCheckResult Check(string receipt) => new("focused", true, 0, null, ResultSummary: receipt);
        var first = Check("RESOURCE phase=gate cpu_ms=123 accounting_source=job;");
        var second = Check("RESOURCE phase=gate cpu_ms=456 accounting_source=snapshot;");
        Assert.Equal(579, MergeTrainMemberRecheckRunner.ReadCpuMilliseconds([first, second]));
        Assert.Null(MergeTrainMemberRecheckRunner.ReadCpuMilliseconds([first, Check("no resource line")]));
        Assert.Null(MergeTrainMemberRecheckRunner.ReadCpuMilliseconds(
            [Check("RESOURCE phase=gate cpu_ms=0 accounting_source=accounting-unavailable;")]));
        Assert.Null(MergeTrainMemberRecheckRunner.ReadCpuMilliseconds([]));
    }
}
