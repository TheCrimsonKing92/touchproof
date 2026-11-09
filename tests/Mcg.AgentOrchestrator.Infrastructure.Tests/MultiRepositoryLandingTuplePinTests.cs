using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pins belong to each test's execution context and use unique goal identities.
public sealed class MultiRepositoryLandingTuplePinTests
{
    private static string Tuple(string candidate) => new RepositoryCandidateTuple([
        new("default", new CandidateIdentity(candidate, "base", "manifest")),
        new("crosswalk", new CandidateIdentity("crosswalk", "base", "manifest"))]).Canonical;

    [Fact]
    public void Enter_OnlyPinsOwningGoalUntilDisposed()
    {
        var goal = GoalId.New();
        var tuple = Tuple("home");
        Assert.Null(MultiRepositoryLandingTuplePin.For(goal));
        using (MultiRepositoryLandingTuplePin.Enter(goal, tuple))
        {
            Assert.Equal(tuple, MultiRepositoryLandingTuplePin.For(goal));
            Assert.Null(MultiRepositoryLandingTuplePin.For(GoalId.New()));
        }
        Assert.Null(MultiRepositoryLandingTuplePin.For(goal));
    }

    [Fact]
    public void Dispose_RestoresPreviousPinAfterException()
    {
        var goal = GoalId.New();
        var original = Tuple("original");
        using (MultiRepositoryLandingTuplePin.Enter(goal, original))
        {
            Assert.Throws<IOException>((Action)(() =>
            {
                using var inner = MultiRepositoryLandingTuplePin.Enter(goal, Tuple("inner"));
                Assert.Equal(Tuple("inner"), MultiRepositoryLandingTuplePin.For(goal));
                throw new IOException("fixture landing failure");
            }));
            Assert.Equal(original, MultiRepositoryLandingTuplePin.For(goal));
        }
        Assert.Null(MultiRepositoryLandingTuplePin.For(goal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed")]
    public void Enter_InvalidTuple_RejectsWithoutReplacingPin(string? invalid)
    {
        var goal = GoalId.New();
        var original = Tuple("original");
        using var pin = MultiRepositoryLandingTuplePin.Enter(goal, original);
        Assert.Throws<ArgumentException>(() => MultiRepositoryLandingTuplePin.Enter(goal, invalid!));
        Assert.Equal(original, MultiRepositoryLandingTuplePin.For(goal));
    }

    [Fact]
    public void Dispose_OutOfOrder_FailsWithoutLosingCurrentPin()
    {
        var goal = GoalId.New();
        using var outer = MultiRepositoryLandingTuplePin.Enter(goal, Tuple("outer"));
        using (MultiRepositoryLandingTuplePin.Enter(goal, Tuple("inner")))
        {
            Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            Assert.Equal(Tuple("inner"), MultiRepositoryLandingTuplePin.For(goal));
        }
        Assert.Equal(Tuple("outer"), MultiRepositoryLandingTuplePin.For(goal));
        outer.Dispose();
        outer.Dispose();
        Assert.Null(MultiRepositoryLandingTuplePin.For(goal));
    }
}
