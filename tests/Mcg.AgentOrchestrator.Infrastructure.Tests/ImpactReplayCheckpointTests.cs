using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using System.Text.Json;

// Parallel-safe: per-test fake sources and private SQLite files, including independent reader connections.
public sealed class ImpactReplayCheckpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Checkpoint_InputAndWriteFail_PreservesErrorsAndPriorCase(bool cleanupFails)
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.OnCase = (attempt, _, _, _) =>
        {
            if (attempt.AttemptId == "second")
            {
                fixture.Sql("""
                    CREATE TRIGGER fail_unreplayable BEFORE UPDATE OF progress_json ON runs
                    WHEN json_extract(NEW.progress_json,'$.completed')=2
                    BEGIN SELECT RAISE(FAIL,'checkpoint fault after input failure'); END;
                    """);
                throw new InvalidDataException("original recoverable case fault");
            }
            return ImpactReplayObservationFixture.Replayed(attempt.AttemptId);
        };
        fixture.Source.OnCleanup = context => cleanupFails && Path.GetFileName(context.Checkout) == "second"
            ? "cleanup after checkpoint fault" : null;

        Assert.Equal(1, fixture.Run());

        var status = fixture.Status();
        Assert.Equal("failed", status.Completion);
        Assert.Equal(1L, fixture.Sql("SELECT count(*) FROM cases"));
        Assert.Equal("first", fixture.Sql("SELECT json_extract(attempt_json,'$.attemptId') FROM cases"));
        Assert.Equal(1, status.Progress.Completed);
        Assert.Equal(0, status.Progress.Unreplayable);
        Assert.Equal(2, fixture.Source.Cleaned);
        Assert.Equal(2, status.Checkouts.Count);
        Assert.Contains(status.Checkouts, c => Path.GetFileName(c.Path) == "second" &&
            c.CleanupStatus == (cleanupFails ? "failed" : "succeeded"));
        Assert.Contains("original recoverable case fault", status.Errors[0]);
        Assert.Contains("checkpoint fault after input failure", status.Errors[0]);
        Assert.Contains("original recoverable case fault", fixture.Error.ToString());
        Assert.Contains("checkpoint fault after input failure", fixture.Error.ToString());
        if (cleanupFails)
        {
            Assert.Contains("cleanup after checkpoint fault", status.Errors[0]);
            Assert.Contains("cleanup after checkpoint fault", fixture.Error.ToString());
        }
        Assert.Equal("", fixture.Output.ToString());
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "impact-replay.json")));
    }

    [Fact]
    public void Inventory_MalformedAndDateExcludedInputs_CountsBothWithoutCases()
    {
        using var fixture = new ImpactReplayObservationFixture();
        using var sink = fixture.Sink();
        var inventory = Path.Combine(fixture.Root, "acceptance-gate-attempts");
        Directory.CreateDirectory(inventory);
        File.WriteAllText(Path.Combine(inventory, "bad.attempt.json"), "{");
        File.WriteAllText(Path.Combine(inventory, "old.attempt.json"), """
            {"attemptId":"old","goalId":"g","goalPrefix":"g","slotIndex":0,
             "branchHeadSha":null,"mainHeadSha":null,"startedAt":"2020-01-01T00:00:00Z",
             "lastHeartbeatAt":"2020-01-01T00:00:00Z","ownerProcessId":0,"outcome":0,
             "stdoutPath":"","stderrPath":"","exitCodePath":"","heartbeatPath":"",
             "resultPath":"","metadataPath":"","kind":"gate"}
            """);
        var diagnostics = new ImpactReplayDiagnostics(sink);
        sink.Phase(ImpactReplayPhase.InventoryLoading);

        var attempts = ImpactReplayAttemptSource.Read(fixture.Root,
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"), diagnostics, sink);

        Assert.Empty(attempts);
        Assert.Single(diagnostics);
        Assert.Equal(2, fixture.Status().Progress.DiscoveredInputs);
        Assert.Equal(2, fixture.Status().Progress.ExaminedInputs);
        Assert.Equal(1, fixture.Status().Progress.Skipped);
        Assert.Equal(0, fixture.Status().Progress.Completed);
        Assert.Equal(1L, fixture.Sql("SELECT count(*) FROM diagnostics"));
    }

    [Fact]
    public void Writer_SecondInvocationInSameDirectory_RejectsOverlappingOwnership()
    {
        using var fixture = new ImpactReplayObservationFixture();
        using var sink = fixture.Sink();

        var failure = Record.Exception(() => { using var competing = fixture.Sink(); });

        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(sink.RunId, fixture.Status().Identity.RunId);
        sink.Phase(ImpactReplayPhase.HistoryLoading); // rejected contender cannot invalidate its owner
        Assert.Equal(ImpactReplayPhase.HistoryLoading, fixture.Status().Progress.Phase);
    }

    [Fact]
    public void Checkpoint_NextCaseExecuting_ExposesPriorCommittedCase()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.OnCase = (attempt, _, _, _) =>
        {
            if (attempt.AttemptId == "second")
            {
                Assert.Equal(1L, fixture.Sql("SELECT count(*) FROM cases"));
                Assert.Equal("Replayable", fixture.Sql("SELECT kind FROM cases"));
                Assert.Equal(1, fixture.Status().Progress.Completed);
                Assert.Equal("incomplete/unknown", fixture.Status().Completion);
                Assert.Equal("Running", fixture.Status().StoredState);
                Assert.Equal("", fixture.Output.ToString());
                Assert.False(File.Exists(Path.Combine(fixture.Destination, "impact-replay.json")));
            }
            return ImpactReplayObservationFixture.Replayed(attempt.AttemptId);
        };

        Assert.Equal(0, fixture.Run());

        var status = fixture.Status();
        Assert.Equal(2, status.Progress.Completed);
        Assert.All(status.Checkouts, c => Assert.Equal("succeeded", c.CleanupStatus));
        var report = JsonSerializer.Deserialize<ImpactReplayReport>(
            File.ReadAllText(Path.Combine(fixture.Destination, "impact-replay.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(report.Attempts.Count, status.Progress.Replayable);
        Assert.Equal(report.Run!.RunId, status.Identity.RunId);
        Assert.Equal(fixture.Checkpoint, report.Run.CheckpointPath);
    }

    [Fact]
    public void Checkpoint_MixedOutcomes_PreservesLimitAndReconciles()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.Attempts = [ImpactReplayObservationFixture.Attempt("missing"), ImpactReplayObservationFixture.Attempt("bad"),
            ImpactReplayObservationFixture.Attempt("abandoned"), ImpactReplayObservationFixture.Attempt("over-limit")];
        fixture.Source.LoadingDiagnostics = diagnostics => diagnostics.Add(new("receipt", "Malformed inventory receipt."));
        fixture.Source.OnCase = (attempt, _, _, _) => attempt.AttemptId switch
        {
            "missing" => new(ImpactReplayCaseKind.Skipped, Diagnostic: new(attempt.AttemptId, "No Infrastructure.Tests lane evidence.")),
            "bad" => throw new InvalidDataException("Bad source"),
            "abandoned" => ImpactReplayObservationFixture.Replayed(attempt.AttemptId, "Abandoned"),
            _ => throw new Xunit.Sdk.XunitException("The replay limit must stop before this case.")
        };

        Assert.Equal(0, fixture.Run(limit: 2));

        var p = fixture.Status().Progress;
        Assert.Equal(2, p.RequestedLimit);
        Assert.Equal(4, p.DiscoveredInputs);
        Assert.Equal(4, p.ExaminedInputs);
        Assert.Equal(4, p.EligibleAttempts);
        Assert.Equal(3, p.ExaminedCases);
        Assert.Equal(3, p.Completed);
        Assert.Equal(1, p.Replayable); // abandoned selection remains replayable
        Assert.Equal(1, p.Unreplayable);
        Assert.Equal(2, p.Skipped); // loading diagnostic plus completed missing-evidence case
        Assert.Equal("completed", fixture.Status().Completion);
        Assert.Equal(3L, fixture.Sql("SELECT count(*) FROM cases"));
        Assert.Equal(1L, fixture.Sql("SELECT count(*) FROM diagnostics"));
    }

    [Fact]
    public void Checkpoint_CounterWriteFails_RollsBackCaseAndPreservesPriorCase()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.OnCase = (attempt, _, _, _) =>
        {
            if (attempt.AttemptId == "second") fixture.Sql("""
                CREATE TRIGGER fail_second BEFORE UPDATE OF progress_json ON runs
                WHEN json_extract(NEW.progress_json,'$.completed')=2
                BEGIN SELECT RAISE(FAIL,'injected checkpoint write fault'); END;
                """);
            return ImpactReplayObservationFixture.Replayed(attempt.AttemptId);
        };

        Assert.Equal(1, fixture.Run());

        Assert.Equal(1L, fixture.Sql("SELECT count(*) FROM cases"));
        Assert.Equal(1, fixture.Status().Progress.Completed);
        Assert.Equal("failed", fixture.Status().Completion);
        Assert.Contains("injected checkpoint write fault", fixture.Error.ToString());
        Assert.Equal(2, fixture.Source.Cleaned);
        Assert.All(fixture.Status().Checkouts, c => Assert.Equal("succeeded", c.CleanupStatus));
        Assert.Equal("", fixture.Output.ToString());
    }

    [Fact]
    public void Diagnostic_CounterWriteFails_RollsBackDiagnosticAtomically()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.BeforeHistory = () => fixture.Sql("""
            CREATE TRIGGER fail_diagnostic BEFORE UPDATE OF progress_json ON runs
            WHEN json_extract(NEW.progress_json,'$.skipped')=1
            BEGIN SELECT RAISE(FAIL,'injected diagnostic fault'); END;
            """);
        fixture.Source.LoadingDiagnostics = diagnostics => diagnostics.Add(new("receipt", "malformed"));

        Assert.Equal(1, fixture.Run());

        var status = fixture.Status();
        Assert.Equal(0L, fixture.Sql("SELECT count(*) FROM diagnostics"));
        Assert.Equal(0L, fixture.Sql("SELECT count(*) FROM cases"));
        Assert.Equal(0, status.Progress.Skipped);
        Assert.Equal(0, status.Progress.Completed);
        Assert.Equal("failed", status.Completion);
        Assert.Contains("\"source\":\"receipt\"", fixture.Error.ToString());
        Assert.Contains("\"reason\":\"malformed\"", fixture.Error.ToString());
        Assert.Contains("injected diagnostic fault", fixture.Error.ToString());
        var failure = Assert.Single(status.Errors);
        Assert.Contains("\"source\":\"receipt\"", failure);
        Assert.Contains("\"reason\":\"malformed\"", failure);
        Assert.Contains("injected diagnostic fault", failure);
        Assert.Contains("durability unconfirmed", failure);
        Assert.Equal(fixture.Error.ToString(), fixture.Error.Flushed);
        Assert.Equal("", fixture.Output.ToString());
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "impact-replay.json")));
    }

    [Fact]
    public void Status_RepeatedInvocation_PreservesRunsWithoutWriting()
    {
        using var fixture = new ImpactReplayObservationFixture();
        Assert.Equal(0, fixture.Run());
        var first = fixture.Status().Identity.RunId;
        Assert.Equal(0, fixture.Run());
        var runs = ImpactReplayCheckpointStore.ReadStatus(fixture.Checkpoint);
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.Identity.RunId == first && r.Completion == "completed");
        Assert.Equal(4L, fixture.Sql("SELECT count(*) FROM cases"));
        var before = File.ReadAllBytes(fixture.Checkpoint);
        using var output = new StringWriter();
        using var error = new StringWriter();
        CliCommandHelp.ThrowIfInvalidFlags(["impact-replay", "--status", "--checkpoint-file", fixture.Checkpoint]);
        Assert.Equal(0, CliImpactReplayCommand.Run(["impact-replay", "--status", "--checkpoint-file", fixture.Checkpoint],
            fixture.Root, fixture.Root, null, output, error));
        Assert.Equal(before, File.ReadAllBytes(fixture.Checkpoint));
        Assert.Contains(first, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Status_StaleOrInvalid_NeverInfersCompletionOrCreatesDatabase()
    {
        using var fixture = new ImpactReplayObservationFixture();
        using (fixture.Sink()) { } // process disappearance can leave exactly this persisted state
        fixture.Clock.Advance(3600);
        Assert.Equal("Running", fixture.Status().StoredState);
        Assert.Equal("incomplete/unknown", fixture.Status().Completion);
        var missing = Path.Combine(fixture.Root, "missing.db");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, CliImpactReplayCommand.Run(["impact-replay", "--status", "--checkpoint-file", missing],
            fixture.Root, fixture.Root, null, output, error));
        Assert.False(File.Exists(missing));
        fixture.Sql("PRAGMA user_version=999");
        Assert.Throws<InvalidDataException>(() => ImpactReplayCheckpointStore.ReadStatus(fixture.Checkpoint));
        var corrupt = Path.Combine(fixture.Root, "corrupt.db");
        File.WriteAllText(corrupt, "invalid sqlite contents");
        var before = File.ReadAllBytes(corrupt);
        Assert.Equal(1, CliImpactReplayCommand.Run(["impact-replay", "--status", "--checkpoint-file", corrupt],
            fixture.Root, fixture.Root, null, output, error));
        Assert.Equal(before, File.ReadAllBytes(corrupt));
    }

    [Fact]
    public void Finalization_MismatchedReport_RejectsCompletion()
    {
        using var fixture = new ImpactReplayObservationFixture();
        using var sink = fixture.Sink();
        var report = ImpactReplay.Read(fixture.Root, fixture.Root, observation: sink, source: fixture.Source);
        var error = Assert.Throws<ImpactReplayObservationException>(() => sink.FinalizeReport(report with { Attempts = [] }));
        sink.Fail(error, false);
        Assert.Equal("failed", fixture.Status().Completion);
        Assert.Contains("do not reconcile", fixture.Status().Errors[0]);
    }
}
