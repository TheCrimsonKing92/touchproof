using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all snapshots, markers and mutations belong to this test.
public sealed class OwnerConsoleDecisionMarkerOrderingTests
{
    [Fact]
    public async Task WriteAfterBoardLoadCannotCacheOldQuestionsUnderNewMarker()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        var questions = new SnapshotQuestions(fixture.Snapshots);
        var wrote = false;
        var builder = new OwnerConsoleViewModelBuilder(fixture.State, questions, fixture.Harness.Liveness,
            new Epics(), fixture.Clock, snapshots: fixture.Snapshots, decisionsMarker: () => fixture.Marker,
            gateMotion: _ =>
            {
                if (!wrote)
                {
                    wrote = true;
                    var snapshot = fixture.Harness.Kernel.ExportSnapshot();
                    fixture.Harness.Kernel.ReplaceWithSnapshot(snapshot with
                    {
                        Goals = snapshot.Goals.Select(goal => goal.Id == "11111111"
                            ? goal with { Objective = "New question" } : goal).ToArray()
                    });
                    fixture.State.Versions["11111111"] = "b";
                    fixture.Marker = "after-write";
                }
                return null;
            });

        async Task<string> Refresh()
        {
            using var generation = fixture.Snapshots.BeginRefresh(reuseUnchanged: true);
            await builder.BuildBoardAsync();
            var (decisions, _) = await builder.ReadDecisionsAsync(CancellationToken.None);
            return Assert.Single(decisions).FullText;
        }

        Assert.Equal("First", await Refresh()); // The question joins the board's pre-write load.
        Assert.True(wrote);
        Assert.Equal("New question", await Refresh());
        Assert.Equal("New question", await Refresh());
        Assert.Equal(2, questions.Reads);
    }

    private sealed class SnapshotQuestions(OwnerConsoleRefreshGoalSnapshots snapshots) : IOwnerQuestionSource
    {
        internal int Reads;
        public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken)
        {
            Reads++;
            var loaded = await snapshots.LoadAsync([new GoalId("11111111")], cancellationToken);
            return [new("q", "11111111", OwnerQuestionKind.HumanInput, Assert.Single(loaded.Goals).Objective)];
        }
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }
}
