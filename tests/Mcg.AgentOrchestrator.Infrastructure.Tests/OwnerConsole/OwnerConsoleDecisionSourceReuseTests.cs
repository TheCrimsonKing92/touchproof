using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: per-test stores, counted state sources, virtual time and event gates.
public sealed class OwnerConsoleDecisionSourceReuseTests
{
    private static OwnerQuestionReadModel Source(OwnerConsoleRefreshReuseFixture f) =>
        OwnerQuestionReadModelFactory.Create(f.State, f.Root,
            changeMarker: () => f.BoardMarker is not null && f.Marker is not null ? f.BoardMarker + "/" + f.Marker : null,
            stateMarker: () => f.BoardMarker);

    [Fact]
    public async Task CollaborationCreateAndAnswerAppearOnNextReadWithoutReloadingUnchangedState()
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        var source = Source(f);
        var first = await source.ReadAsync(CancellationToken.None);
        Assert.Empty(first.Live);
        var store = CollaborationItemStore.ForDirectory(f.Root);
        var question = await store.RaiseAsync(CollaborationItemType.Clarification, "11111111",
            "New decision", "Question: New decision", "spec-clarification:reuse");
        f.Marker = "collaboration:2";
        f.Clock.Now = f.Clock.Now.AddMinutes(5);
        var changed = await source.ReadAsync(CancellationToken.None);
        Assert.Equal(question.Id, Assert.Single(changed.Live).ItemId);
        Assert.Equal("New decision", Assert.Single(changed.Live).Text);
        Assert.Same(changed, await source.ReadAsync(CancellationToken.None));
        Assert.True(await store.TryResolveAsync(question.CorrelationKey!, "answered"));
        f.Marker = "collaboration:3";
        Assert.Empty((await source.ReadAsync(CancellationToken.None)).Live);
        Assert.Equal((1, 1), (f.State.Boards, f.State.RequestReads));
        Assert.All(f.State.Loads.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task FailedCollaborationReadRetriesWithoutDiscardingHealthyStateProjection()
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        var source = Source(f);
        await source.ReadAsync(CancellationToken.None);
        var path = Path.Combine(f.Root, "collaboration-items.db");
        File.WriteAllText(path, "not a SQLite database");
        f.Marker = "collaboration:2";
        var error = await Assert.ThrowsAsync<SqliteException>(() => source.ReadAsync(CancellationToken.None));
        Assert.Equal(26, error.SqliteErrorCode); // SQLITE_NOTADB, not an unrelated source failure.
        File.Delete(path);
        var store = CollaborationItemStore.ForDirectory(f.Root);
        var question = await store.RaiseAsync(CollaborationItemType.Clarification, "11111111",
            "Recovered decision", "Question: Recovered decision", "spec-clarification:recovered");
        // Keep the attempted marker: a failed read must not acknowledge it.
        Assert.Equal(question.Id, Assert.Single((await source.ReadAsync(CancellationToken.None)).Live).ItemId);
        Assert.Equal((1, 1), (f.State.Boards, f.State.RequestReads));
        Assert.All(f.State.Loads.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task StateChangeAndOwnerForceReloadEvenWhenGoalTimestampsAreUnchanged()
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        var source = Source(f);
        await source.ReadAsync(CancellationToken.None);
        Hold(f, "New state question");
        f.BoardMarker = "state:2";
        Assert.Equal("New state question", Assert.Single((await source.ReadAsync(CancellationToken.None)).Live).Text);
        Hold(f, "Forced state question");
        source.Invalidate();
        Assert.Equal("Forced state question", Assert.Single((await source.ReadAsync(CancellationToken.None)).Live).Text);
        Assert.Equal((3, 3), (f.State.Boards, f.State.RequestReads));
        Assert.All(f.State.Loads.Values, count => Assert.Equal(3, count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationDuringStateReadCannotCertifyOldStateForLaterCollaborationReads(bool force)
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        var source = Source(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.State.AfterLoad = async () => { entered.TrySetResult(); await release.Task; };
        var reading = source.ReadAsync(CancellationToken.None);
        try
        {
            await TestHangGuard.WaitAsync(entered.Task, "independent decision state captured");
            Hold(f, "Concurrent question");
            if (force) source.Invalidate();
            else f.BoardMarker = "state:2";
        }
        finally { release.TrySetResult(); }
        Assert.Empty((await TestHangGuard.WaitAsync(reading, "state read released")).Live);
        var fresh = await source.ReadAsync(CancellationToken.None);
        Assert.Equal("Concurrent question", Assert.Single(fresh.Live).Text);
        f.Marker = "collaboration:2";
        Assert.Equal("Concurrent question", Assert.Single((await source.ReadAsync(CancellationToken.None)).Live).Text);
        Assert.Equal(2, f.State.RequestReads);
    }

    [Fact]
    public async Task UnknownStateAndFailedStateReadsStayRetryable()
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        var source = Source(f);
        await source.ReadAsync(CancellationToken.None);
        f.BoardMarker = "state:2";
        f.State.FailLoad = true;
        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(CancellationToken.None));
        Assert.Equal((0, 0, 0), source.RetainedState);
        f.State.FailLoad = false;
        Hold(f, "Recovered question");
        Assert.Equal("Recovered question", Assert.Single((await source.ReadAsync(CancellationToken.None)).Live).Text);
        f.BoardMarker = null;
        await source.ReadAsync(CancellationToken.None);
        await source.ReadAsync(CancellationToken.None);
        Assert.Equal((0, 0, 0), source.RetainedState);
        Assert.Equal(5, f.State.RequestReads);
    }

    [Fact]
    public async Task ReplacedAndRemovedStateReturnsToLiveSetAndSameTokensDoNotCrossProjects()
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        var source = Source(f);
        for (var round = 0; round < 30; round++)
        {
            Hold(f, "Question " + round);
            f.BoardMarker = "state:" + round;
            Assert.Equal("Question " + round, Assert.Single((await source.ReadAsync(CancellationToken.None)).Live).Text);
            Assert.Equal((2, 1, 0), source.RetainedState);
        }
        using var other = new OwnerConsoleRefreshReuseFixture();
        other.BoardMarker = f.BoardMarker;
        var otherSource = Source(other);
        Assert.Empty((await otherSource.ReadAsync(CancellationToken.None)).Live);
        Assert.Equal((2, 0, 0), otherSource.RetainedState);
        f.State.Visible.Clear();
        f.BoardMarker = "removed";
        Assert.Empty((await source.ReadAsync(CancellationToken.None)).Live);
        Assert.Equal((0, 0, 0), source.RetainedState);
        Assert.Equal((2, 0, 0), otherSource.RetainedState);
    }

    private static void Hold(OwnerConsoleRefreshReuseFixture f, string text)
    {
        var snapshot = f.Harness.Kernel.ExportSnapshot();
        f.Harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal.Id == "11111111"
            ? goal with { CurrentHold = new("hold", "steward-owner-question", text, f.Clock.Now) } : goal).ToArray() });
    }
}
