using Mcg.AgentOrchestrator.App.Orchestration;
using System.Text.Json;

// Parallel-safe: injected faults, cancellation, time, and checkouts are scoped to the private fixture.
public sealed class ImpactReplayInterruptionTests
{
    [Theory]
    [InlineData(true, "caller", false, "cancelled")]
    [InlineData(true, "caller", true, "cancelled")]
    [InlineData(false, "caller", false, "failed")]
    [InlineData(false, "caller", true, "failed")]
    [InlineData(true, "foreign", false, "failed")]
    [InlineData(true, "foreign", true, "failed")]
    [InlineData(false, "foreign", false, "failed")]
    [InlineData(false, "foreign", true, "failed")]
    [InlineData(true, "unbound", false, "failed")]
    [InlineData(true, "unbound", true, "failed")]
    [InlineData(false, "unbound", false, "failed")]
    [InlineData(false, "unbound", true, "failed")]
    public void Cancellation_TokenProvenance_DeterminesTerminalState(
        bool cancelCaller, string provenance, bool cleanupFails, string expected)
    {
        using var fixture = new ImpactReplayObservationFixture();
        using var caller = new CancellationTokenSource();
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        fixture.Source.OnCase = (_, _, _, token) =>
        {
            if (cancelCaller) caller.Cancel();
            throw new OperationCanceledException("injected cancellation: " + provenance, provenance switch
            {
                "caller" => token,
                "foreign" => foreign.Token,
                _ => CancellationToken.None
            });
        };
        fixture.Source.OnCleanup = _ => cleanupFails ? "cleanup after cancellation fault" : null;

        Assert.Equal(1, fixture.Run(caller.Token));

        var status = fixture.Status();
        Assert.Equal(expected, status.Completion);
        Assert.Equal(0, status.Progress.Completed);
        Assert.Equal(0L, fixture.Sql("SELECT count(*) FROM cases"));
        Assert.Equal(1, fixture.Source.Cleaned);
        Assert.Equal(cleanupFails ? "failed" : "succeeded", Assert.Single(status.Checkouts).CleanupStatus);
        Assert.Contains("injected cancellation: " + provenance, status.Errors[0]);
        Assert.Contains("injected cancellation: " + provenance, fixture.Error.ToString());
        if (cleanupFails) Assert.Contains("cleanup after cancellation fault", status.Errors[0]);
        Assert.Equal("", fixture.Output.ToString());
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "impact-replay.json")));
    }

    [Fact]
    public void Checkout_PartiallyRegisteredInputFailure_StillRunsOwnedCleanup()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.Attempts = [ImpactReplayObservationFixture.Attempt("partial")];
        fixture.Source.OnCase = (_, context, _, _) =>
        {
            context.Registered = false;
            throw new InvalidDataException("detached checkout creation fault");
        };
        fixture.Source.OnCleanup = context =>
        {
            Assert.NotNull(context.Checkout);
            Assert.False(context.Registered);
            Assert.Equal("pending", Assert.Single(fixture.Status().Checkouts).CleanupStatus);
            Assert.Equal(1L, fixture.Sql("SELECT count(*) FROM cases WHERE kind='Unreplayable'"));
            return null;
        };

        Assert.Equal(0, fixture.Run());

        Assert.Equal(1, fixture.Source.Cleaned);
        Assert.Equal(1, fixture.Status().Progress.Unreplayable);
        Assert.Equal("succeeded", Assert.Single(fixture.Status().Checkouts).CleanupStatus);
    }

    [Fact]
    public void Cancellation_AfterFirstCase_PreservesEvidenceAndCleansCheckout()
    {
        using var fixture = new ImpactReplayObservationFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Source.OnCase = (attempt, _, _, token) =>
        {
            if (attempt.AttemptId == "second")
            {
                Assert.Equal(1L, fixture.Sql("SELECT count(*) FROM cases"));
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return ImpactReplayObservationFixture.Replayed(attempt.AttemptId);
        };

        Assert.Equal(1, fixture.Run(cancellation.Token));

        Assert.Equal(1L, fixture.Sql("SELECT count(*) FROM cases"));
        var status = fixture.Status();
        Assert.Equal("cancelled", status.Completion);
        Assert.Equal(1, status.Progress.Completed);
        Assert.Equal(2, fixture.Source.Cleaned);
        Assert.All(status.Checkouts, c => Assert.Equal("succeeded", c.CleanupStatus));
        Assert.Equal("", fixture.Output.ToString());
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "impact-replay.json")));
    }

    [Fact]
    public void Cancellation_CleanupAlsoFails_RetainsCancellationAndCleanupEvidence()
    {
        using var fixture = new ImpactReplayObservationFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Source.OnCase = (_, _, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new Xunit.Sdk.XunitException("Cancellation must throw at this boundary.");
        };
        fixture.Source.OnCleanup = _ => "cleanup failed after cancellation";

        Assert.Equal(1, fixture.Run(cancellation.Token));

        Assert.Equal("cancelled", fixture.Status().Completion);
        Assert.Contains("cleanup failed after cancellation", fixture.Status().Errors[0]);
        Assert.Equal("failed", Assert.Single(fixture.Status().Checkouts).CleanupStatus);
        Assert.Equal("", fixture.Output.ToString());
    }

    [Fact]
    public void Heartbeat_WriteFails_StopsReplayAndCleansCheckout()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.OnCase = (attempt, _, sink, _) =>
        {
            fixture.Sql("""
                CREATE TRIGGER fail_heartbeat BEFORE UPDATE OF progress_json ON runs
                WHEN json_extract(NEW.progress_json,'$.kind')='heartbeat'
                BEGIN SELECT RAISE(FAIL,'injected heartbeat fault'); END;
                """);
            fixture.Clock.Advance(30);
            sink.Phase(ImpactReplayPhase.EvidenceReading); // observes the timer's retained failure
            return ImpactReplayObservationFixture.Replayed(attempt.AttemptId);
        };

        Assert.Equal(1, fixture.Run());

        Assert.Equal(1, fixture.Source.Cleaned);
        Assert.Equal("failed", fixture.Status().Completion);
        Assert.Equal(0, fixture.Status().Progress.Unreplayable);
        Assert.Equal(0L, fixture.Sql("SELECT count(*) FROM cases"));
        Assert.Equal("succeeded", Assert.Single(fixture.Status().Checkouts).CleanupStatus);
        Assert.Contains("injected heartbeat fault", fixture.Error.ToString());
        Assert.Contains("durability unconfirmed", fixture.Error.ToString());
        Assert.Equal("", fixture.Output.ToString());
    }

    [Fact]
    public void Failure_CleanupAlsoFails_PreservesBothErrors()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.OnCase = (_, _, _, _) => throw new NotSupportedException("primary execution fault");
        fixture.Source.OnCleanup = _ => "owned checkout cleanup fault";

        Assert.Equal(1, fixture.Run());

        Assert.Equal(1, fixture.Source.Cleaned);
        var status = fixture.Status();
        Assert.Equal("failed", status.Completion);
        Assert.Contains("primary execution fault", status.Errors[0]);
        Assert.Contains("owned checkout cleanup fault", status.Errors[0]);
        Assert.Equal("failed", Assert.Single(status.Checkouts).CleanupStatus);
        Assert.Equal("", fixture.Output.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Cleanup_Fails_PublishesReportsAndRejectsCompletion(int caseCount)
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.Attempts = fixture.Source.Attempts.Take(caseCount).ToArray();
        fixture.Source.OnCleanup = context => $"injected cleanup failure: path={context.Checkout}";

        Assert.Equal(1, fixture.Run());

        Assert.Equal((long)caseCount, fixture.Sql("SELECT count(*) FROM cases"));
        Assert.Equal(caseCount, fixture.Source.Cleaned);
        var status = fixture.Status();
        Assert.Equal(caseCount, status.Progress.Completed);
        Assert.Equal(caseCount, status.Progress.Replayable);
        Assert.Equal("failed", status.Completion);
        Assert.Equal("Failed", status.StoredState);
        Assert.Equal(caseCount, status.Checkouts.Count);
        var cleanupErrors = status.Checkouts.Select(c => $"injected cleanup failure: path={c.Path}").ToArray();
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Destination, "impact-replay.json")));
        Assert.Equal(caseCount, report.RootElement.GetProperty("attempts").GetArrayLength());
        Assert.Equal(cleanupErrors, report.RootElement.GetProperty("cleanupErrors").EnumerateArray()
            .Select(e => e.GetString()).ToArray());
        var markdown = File.ReadAllText(Path.Combine(fixture.Destination, "impact-replay.md"));
        Assert.All(status.Checkouts, checkout =>
        {
            var expected = $"injected cleanup failure: path={checkout.Path}";
            Assert.Equal("failed", checkout.CleanupStatus);
            Assert.Equal(expected, checkout.Error);
            Assert.Contains(expected, markdown);
            Assert.Contains(expected, fixture.Error.ToString());
            Assert.Contains(expected, Assert.Single(status.Errors));
        });
        Assert.DoesNotContain("durability unconfirmed", fixture.Error.ToString());
        Assert.Equal($"Impact replay: {caseCount} attempts; reports: {fixture.Destination}{Environment.NewLine}",
            fixture.Output.ToString());
    }

    [Fact]
    public void Publication_Fails_PreservesCasesAndRejectsCompletion()
    {
        using var fixture = new ImpactReplayObservationFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Destination, "impact-replay.json"));

        Assert.Equal(1, fixture.Run());

        Assert.Equal(2L, fixture.Sql("SELECT count(*) FROM cases"));
        Assert.Equal("failed", fixture.Status().Completion);
        Assert.All(fixture.Status().Checkouts, c => Assert.Equal("succeeded", c.CleanupStatus));
        Assert.Equal("", fixture.Output.ToString());
    }

    [Fact]
    public void Failure_TerminalWriteFails_ReportsOriginalAndDurabilityFaults()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.OnCase = (_, _, _, _) =>
        {
            fixture.Sql("""
                CREATE TRIGGER fail_terminal BEFORE UPDATE OF state ON runs
                BEGIN SELECT RAISE(FAIL,'injected terminal persistence fault'); END;
                """);
            throw new NotSupportedException("original operation fault");
        };

        Assert.Equal(1, fixture.Run());

        Assert.Equal("incomplete/unknown", fixture.Status().Completion);
        Assert.Contains("original operation fault", fixture.Error.ToString());
        Assert.Contains("injected terminal persistence fault", fixture.Error.ToString());
        Assert.Contains("durability unconfirmed", fixture.Error.ToString());
        Assert.Equal(1, fixture.Source.Cleaned);
        Assert.Equal("", fixture.Output.ToString());
    }
}
