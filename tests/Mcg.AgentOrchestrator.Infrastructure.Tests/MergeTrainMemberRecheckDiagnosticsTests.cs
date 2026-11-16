using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: private TRX files, SQLite databases and effect lists; no git or real gates.
public sealed class MergeTrainMemberRecheckDiagnosticsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("member-recheck-diagnostics-").FullName;
    private string Database => Path.Combine(_root, "train.db");

    [Fact]
    public void RunnerThrows_ReasonNamesExceptionType()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((_, _, _, _) => throw new IOException("cannot materialise"))!;
        var reason = AssertUnavailable(fixture, outcome, "runner-threw");
        Assert.Equal("IOException", reason.ExceptionType);
        Assert.Equal("cannot materialise", reason.Message);
        Assert.Contains("exception=IOException message=\"cannot materialise\"", fixture.Events[0].Detail);
        fixture.Run((_, _, _, _) => throw new InvalidOperationException("replay must not run"));
        Assert.Single(fixture.Events);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public void ExceptionMessage_CutTo200AndSanitisedToOneLine()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var message = "bad\r\n\t\0\u0085\u2028\u2029\\\";[]" + new string('x', 220) + "DISCARDED";
        var outcome = fixture.Run((_, _, _, _) => throw new IOException(message))!;
        var reason = AssertUnavailable(fixture, outcome, "runner-threw");
        var expected = "bad" + new string(' ', 7) + "\\\";[]" + new string('x', 185);
        Assert.Equal(expected, reason.Message);
        Assert.Equal(200, reason.Message!.Length);
        var detail = fixture.Events[0].Detail;
        Assert.DoesNotContain(detail, character => char.IsControl(character) || character is '\u2028' or '\u2029');
        Assert.Contains("message=\"bad" + new string(' ', 7) + new string('\\', 3) + "\"\\;\\[\\]", detail);
        Assert.DoesNotContain("DISCARDED", detail);
        var loaded = fixture.Cache.Read(fixture.Receipt)!;
        Assert.Equal(reason, loaded.Members[0].Unavailable);
        Assert.Equal(detail, loaded.EventDetail(fixture.Receipt));
    }

    [Theory]
    [InlineData("tree-mismatch", "before-run")]
    [InlineData("tree-mismatch", "after-run")]
    [InlineData("invalid-evidence", "missing-provenance")]
    [InlineData("invalid-evidence", "assembly-outside-root")]
    [InlineData("invalid-evidence", "bad\r\nevidence;[root]")]
    public void DiagnosedNoRun_ReasonNamesCause(string code, string detail)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((_, _, _, _) =>
            throw new MergeTrainMemberRecheckUnavailableException(new(code, detail: detail)))!;
        var reason = AssertUnavailable(fixture, outcome, code);
        Assert.Equal(detail.Replace('\r', ' ').Replace('\n', ' '), reason.Detail);
        var expectedDetail = detail == "bad\r\nevidence;[root]" ? "bad  evidence\\;\\[root\\]" : detail;
        Assert.Contains($"detail=\"{expectedDetail}\"", fixture.Events[0].Detail);
        Assert.DoesNotContain('\n', fixture.Events[0].Detail);
        Assert.DoesNotContain('\r', fixture.Events[0].Detail);
    }

    [Theory]
    [InlineData(false, "main-revision-mismatch")]
    [InlineData(true, "missing-tree-revision")]
    public void RunBoundElsewhere_ReasonSaysNotBound(bool missingTree, string detail)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((_, _, selection, _) =>
        {
            var run = fixture.Execution(selection, false);
            return missingTree ? run with { Verification = run.Verification with { TreeRevision = null } }
                : run with { MainRevision = new string('c', 40) };
        })!;
        Assert.Equal(detail, AssertUnavailable(fixture, outcome, "run-not-bound-to-observed-main").Detail);
        Assert.Contains($"detail=\"{detail}\"", fixture.Events[0].Detail);
    }

    [Theory]
    [InlineData("focused-bounds")]
    [InlineData("invalid-evidence")]
    [InlineData("not-run-selections")]
    [InlineData("recheck-failed")]
    public void ExecutionRefusal_ReasonCarriesRefusalValue(string refusal)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((_, _, selection, _) =>
        {
            var run = fixture.Execution(selection, false);
            return refusal switch
            {
                "focused-bounds" => run with { Verification = run.Verification with { Kind = ConductorCohortFocusedPassKind.SelectionRejected } },
                "invalid-evidence" => run with { AttemptId = "" },
                "not-run-selections" => run with { NotRunSelectionCount = 1 },
                _ => run with { Verification = run.Verification with { AllChecksPassed = false, FailedChecks = ["focused"] } }
            };
        })!;
        Assert.Equal(refusal, AssertUnavailable(fixture, outcome, "execution-refusal").Detail);
        Assert.Contains($"detail=\"{refusal}\"", fixture.Events[0].Detail);
    }

    [Fact]
    public void NullRun_ReasonIsNoRun()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        AssertUnavailable(fixture, fixture.Run((_, _, _, _) => null)!, "no-run");
    }

    [Fact]
    public void LeaseAcquisitionFails_ReasonNamesBuildLease()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var exception = new DotnetBuildSlotsBusyException(new("member-run", []));
        var acquisitions = 0;
        var outcome = fixture.Run((_, _, _, token) =>
        {
            using var lease = MergeTrainMemberRecheckRunner.AcquireLease((runId, passedToken) =>
            {
                Assert.Equal("member-run", runId);
                Assert.Equal(token, passedToken);
                acquisitions++;
                throw exception;
            }, "member-run", token);
            throw new InvalidOperationException("acquisition must fail");
        })!;
        Assert.Equal(1, acquisitions);
        var reason = AssertUnavailable(fixture, outcome, "build-lease-not-acquired");
        Assert.Equal(nameof(DotnetBuildSlotsBusyException), reason.ExceptionType);
        Assert.Equal(exception.Message, reason.Message);
        Assert.Contains($"exception={nameof(DotnetBuildSlotsBusyException)}", fixture.Events[0].Detail);
        Assert.Contains($"message=\"{exception.Message}\"", fixture.Events[0].Detail);
    }

    [Fact]
    public void LeaseAcquisitionCancelled_PropagatesWithoutDiagnosticsOrEffects()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var cancelled = new OperationCanceledException();
        Assert.Same(cancelled, Assert.Throws<OperationCanceledException>(() => fixture.Run((_, _, _, token) =>
        {
            using var lease = MergeTrainMemberRecheckRunner.AcquireLease((_, _) => throw cancelled, "run", token);
            return null;
        })));
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Implicated);
        Assert.Empty(fixture.Ejections);
        Assert.Equal(0, fixture.Cache.Count);
    }

    [Fact]
    public void CompletedAndUnrunMembers_CarryNoReason()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((member, _, selection, _) =>
            member == fixture.Members[0] ? fixture.Execution(selection, true) : null)!;
        Assert.Equal(new[] { "fail", "unavailable", "unrun" }, outcome.Members.Select(member => member.Result));
        Assert.Null(outcome.Members[0].Unavailable);
        Assert.Null(outcome.Members[2].Unavailable);
        Assert.Equal(1, fixture.Events[0].Detail.Split("unavailable_reason=").Length - 1);
        using var passing = new MergeTrainMemberRecheckFixture();
        var complete = passing.Run((member, _, selection, _) => passing.Execution(selection, member == passing.Members[0]))!;
        Assert.All(complete.Members, member => Assert.Null(member.Unavailable));
        Assert.DoesNotContain("unavailable_reason=", passing.Events[0].Detail);
    }

    [Theory]
    [InlineData("attributed", "already-attributed-member")]
    [InlineData("message", "message-subject-failure")]
    [InlineData("attempt", "non-first-attempt")]
    [InlineData("two", "member-count-not-three")]
    [InlineData("passed", "not-genuine-train-red")]
    [InlineData("infrastructure", "not-genuine-train-red")]
    [InlineData("no-fatal", "not-genuine-train-red")]
    [InlineData("precedence", "already-attributed-member")]
    public void SkippedBeforeRunning_OneLineNamingCondition_NoneOnReplay(string mode, string reason)
    {
        using var fixture = new MergeTrainMemberRecheckFixture(mode == "two" ? 2 : 3);
        if (mode is "passed" or "infrastructure") fixture.Receipt = fixture.Receipt with
            { Outcome = mode == "passed" ? MergeTrainGateOutcome.Passed : MergeTrainGateOutcome.InfrastructureFailure };
        if (mode == "no-fatal") fixture.Receipt = fixture.Receipt with
            { GateTestResultPaths = [fixture.WriteTrx(("Ns.CulpritTests.Case", "Passed"))] };
        for (var replay = 0; replay < 2; replay++)
        {
            Assert.Null(fixture.Run((_, _, _, _) => throw new InvalidOperationException("skip must not run"),
                attributed: mode is "attributed" or "precedence" ? fixture.Members[0] : null,
                messageSubject: mode is "message" or "precedence", attempt: mode is "attempt" or "precedence" ? 1 : 0,
                recordSkips: true));
            var logged = Assert.Single(fixture.Events);
            Assert.Equal("train-member-recheck", logged.Kind);
            Assert.Contains($"MEMBER_RECHECK_SKIPPED train={fixture.Receipt.Identity.Value} receipt={fixture.Receipt.ReceiptId} reason={reason} ", logged.Detail);
            Assert.DoesNotContain('\n', logged.Detail);
            Assert.Empty(fixture.Calls);
            Assert.Empty(fixture.Implicated);
            Assert.Empty(fixture.Ejections);
            Assert.Equal(1, fixture.Cache.Count);
        }
    }

    [Fact]
    public void SkipMarker_SqliteCache_OneLineAcrossInstances()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        Assert.Null(WithCache(fixture, new MergeTrainMemberRecheckOutcomeCache(Database), attributed: true));
        Assert.Null(WithCache(fixture, new MergeTrainMemberRecheckOutcomeCache(Database), attributed: true));
        Assert.Single(fixture.Events);
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Implicated);
        Assert.Empty(fixture.Ejections);
        Assert.Null(new MergeTrainMemberRecheckOutcomeCache(Database).Read(fixture.Receipt));
    }

    [Fact]
    public void SkipMarker_LeavesLaterEligibleSweepUnchanged()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        Assert.Null(fixture.Run((_, _, _, _) => null, attributed: fixture.Members[0], recordSkips: true));
        Assert.Null(fixture.Run((_, _, _, _) => null, messageSubject: true, recordSkips: true));
        Assert.Single(fixture.Events); // The first skip condition wins even when replay's condition differs.
        var outcome = fixture.Run((member, _, selection, _) =>
            fixture.Execution(selection, member == fixture.Members[0]), recordSkips: true)!;
        Assert.Null(outcome.FallbackReason);
        Assert.Equal(fixture.Members, fixture.Calls);
        Assert.Equal(fixture.Members[0].GoalId, Assert.Single(fixture.Ejections).GoalId);
        Assert.Equal(2, fixture.Cache.Count);
        Assert.Single(fixture.Events);
    }

    [Fact]
    public void RecheckedReceipt_NowAttributed_DoesNotAnnounceSkip()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        fixture.Run((member, _, selection, _) => fixture.Execution(selection, member == fixture.Members[0]), recordSkips: true);
        Assert.Single(fixture.Events);
        Assert.Null(fixture.Run((_, _, _, _) => null, attributed: fixture.Members[0], recordSkips: true));
        Assert.Single(fixture.Events);
        Assert.Equal(1, fixture.Cache.Count);
        Assert.Equal(3, fixture.Calls.Count);
    }

    [Theory]
    [InlineData(2, 0, true, "member-count-not-three")]
    [InlineData(3, 1, true, "non-first-attempt")]
    [InlineData(2, 0, false, null)]
    public void DriverEarlyReturn_RedReceipt_LogsSkipOnce(int count, int attempt, bool red, string? reason)
    {
        using var fixture = new MergeTrainMemberRecheckFixture(count);
        if (!red) fixture.Receipt = fixture.Receipt with { Outcome = MergeTrainGateOutcome.Passed };
        for (var replay = 0; replay < 2; replay++)
            MergeTrainMemberRecheckSkip.RecordEarlyReturn(fixture.Receipt, null, false, attempt, fixture.Cache,
                (kind, detail) => fixture.Events.Add((kind, detail)));
        if (red)
        {
            var logged = Assert.Single(fixture.Events);
            Assert.Equal("train-member-recheck", logged.Kind);
            Assert.Contains($"MEMBER_RECHECK_SKIPPED train={fixture.Receipt.Identity.Value} receipt={fixture.Receipt.ReceiptId} reason={reason} ", logged.Detail);
        }
        else Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Implicated);
        Assert.Empty(fixture.Ejections);
    }

    [Fact]
    public void SkipMarker_SaveFails_PreservesSkipWithoutEffects()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        fixture.Cache.BeforeSave = () => throw new IOException("diagnostic store unavailable");
        Assert.Null(fixture.Run((_, _, _, _) => null, attributed: fixture.Members[0], recordSkips: true));
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Implicated);
        Assert.Empty(fixture.Ejections);
        Assert.Equal(0, fixture.Cache.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyOutcomeWithoutReason_ValidatesAndReplaysSameEjections(bool unavailable, bool sqlite)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var members = fixture.Members;
        // Hand-written old shape: intentionally does not use today's serializer.
        var json = $$"""
            {"Members":[
              {"GoalId":"{{members[0].GoalId.Value}}","CandidateRevision":"{{members[0].CandidateRevision}}",
               "BranchRevision":"{{members[0].BranchRevision}}","BriefVersion":1,"Result":"fail","CpuMilliseconds":10},
              {"GoalId":"{{members[1].GoalId.Value}}","CandidateRevision":"{{members[1].CandidateRevision}}",
               "BranchRevision":"{{members[1].BranchRevision}}","BriefVersion":1,"Result":"{{(unavailable ? "unavailable" : "pass")}}","CpuMilliseconds":null},
              {"GoalId":"{{members[2].GoalId.Value}}","CandidateRevision":"{{members[2].CandidateRevision}}",
               "BranchRevision":"{{members[2].BranchRevision}}","BriefVersion":1,"Result":"{{(unavailable ? "unrun" : "pass")}}","CpuMilliseconds":null}
            ],"FallbackReason":{{(unavailable ? "\"execution-failure\"" : "null")}}}
            """;
        MergeTrainMemberRecheck.IOutcomeCache cache = fixture.Cache;
        if (sqlite)
        {
            MergeTrainAcceptanceStore.Setup(Database);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO merge_train_member_rechecks(receipt_id, train_id, outcome_json) VALUES($receipt,$train,$json);";
            command.Parameters.AddWithValue("$receipt", fixture.Receipt.ReceiptId);
            command.Parameters.AddWithValue("$train", fixture.Receipt.Identity.Value);
            command.Parameters.AddWithValue("$json", json);
            Assert.Equal(1, command.ExecuteNonQuery());
            cache = new MergeTrainMemberRecheckOutcomeCache(Database);
        }
        else fixture.Cache.Seed(fixture.Receipt, json);
        var loaded = cache.Read(fixture.Receipt)!;
        MergeTrainMemberRecheck.Validate(fixture.Receipt, loaded);
        Assert.All(loaded.Members, member => Assert.Null(member.Unavailable));
        foreach (var path in fixture.Receipt.GateTestResultPaths) File.Delete(path);
        var replay = WithCache(fixture, cache)!;
        Assert.Equal(loaded.Members, replay.Members);
        Assert.Equal(unavailable ? "execution-failure" : null, replay.FallbackReason);
        Assert.Equal(unavailable ? Array.Empty<GoalId>() : [members[0].GoalId], fixture.Implicated.Keys.ToArray());
        Assert.Equal(unavailable ? Array.Empty<GoalId>() : [members[0].GoalId], fixture.Ejections.Select(ejection => ejection.GoalId).ToArray());
        Assert.All(fixture.Ejections, ejection => Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason));
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Events);
    }

    private static MergeTrainMemberRecheckUnavailable AssertUnavailable(MergeTrainMemberRecheckFixture fixture,
        MergeTrainMemberRecheck.Outcome outcome, string code)
    {
        Assert.Equal("execution-failure", outcome.FallbackReason);
        Assert.Equal("unavailable", outcome.Members[0].Result);
        var reason = Assert.IsType<MergeTrainMemberRecheckUnavailable>(outcome.Members[0].Unavailable);
        Assert.Equal(code, reason.Code);
        Assert.All(outcome.Members.Skip(1), member =>
        {
            Assert.Equal("unrun", member.Result);
            Assert.Null(member.Unavailable);
        });
        var logged = Assert.Single(fixture.Events).Detail;
        Assert.Contains($"member={fixture.Members[0].GoalId.Value} candidate_revision={fixture.Members[0].CandidateRevision} result=unavailable ", logged);
        Assert.Contains($" unavailable_reason={code}", logged);
        Assert.Empty(fixture.Implicated);
        Assert.Empty(fixture.Ejections);
        return reason;
    }

    private static MergeTrainMemberRecheck.Outcome? WithCache(MergeTrainMemberRecheckFixture fixture,
        MergeTrainMemberRecheck.IOutcomeCache cache, bool attributed = false) =>
        MergeTrainMemberRecheck.Run(fixture.Receipt, attributed ? fixture.Members[0] : null, false, 0,
            (member, _, _, _) =>
            {
                fixture.Calls.Add(member);
                throw new InvalidOperationException("cached or skipped run must not execute");
            }, cache, (member, source) => fixture.Implicated[member.GoalId] = source, fixture.Ejections.Add,
            (kind, detail) => fixture.Events.Add((kind, detail)), default, recordSkips: true);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
