using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Attempt = Mcg.AgentOrchestrator.App.Orchestration.PreTesterTestImpactPlanAttemptCoordinator;

public sealed class PreTesterTestImpactPlanAttemptCoordinatorTests
{
    private static readonly SpawnProcessIdentity Owner = new(42, DateTimeOffset.UnixEpoch, "planning-child");

    [Fact]
    public void LiveRequestsLaunchOncePerKeyAcrossCoordinators()
    {
        using var fixture = new Fixture();
        var key = fixture.Key;

        Assert.Equal(Attempt.State.Pending, fixture.Coordinator().Observe(key).Status);
        Assert.Equal(Attempt.State.Pending, fixture.Coordinator().Observe(key).Status);
        Assert.Single(fixture.Launches);

        var other = key with { ReversePathsJson = JsonSerializer.Serialize(new[] { "src/Other.cs" }) };
        Assert.Equal(Attempt.State.Pending, fixture.Coordinator().Observe(other).Status);
        Assert.Equal(2, fixture.Launches.Count);
        Assert.NotEqual(fixture.Launches[0].Path, fixture.Launches[1].Path);
    }

    [Theory]
    [InlineData("failed", "planner crashed")]
    [InlineData("owner-dead", "Planning process 42 exited without a result.")]
    [InlineData("launch-failed", "launch denied")]
    public void TerminalAttemptsKeepTheirReasonWithoutReplacement(string outcome, string reason)
    {
        using var fixture = new Fixture();
        if (outcome == "launch-failed")
            fixture.LaunchError = reason;
        var first = fixture.Coordinator().Observe(fixture.Key);
        var (path, id) = Assert.Single(fixture.Launches);
        if (outcome == "failed")
            Assert.Equal(1, Attempt.RunAttempt(path, id, _ => throw new InvalidOperationException(reason)));
        if (outcome == "owner-dead")
            fixture.OwnerStatus = SpawnTrackedProcessStatus.DeadOrRecycled;
        Assert.Equal(outcome == "launch-failed" ? Attempt.State.Failed : Attempt.State.Pending, first.Status);

        var terminal = fixture.Coordinator().Observe(fixture.Key);
        Assert.Equal(Attempt.State.Failed, terminal.Status);
        Assert.Equal($"{outcome}: {reason}", terminal.Detail);
        Assert.Equal(terminal, fixture.Coordinator().Observe(fixture.Key));
        Assert.Single(fixture.Launches);
    }

    [Fact]
    public void CompletedArtifactWithAnotherKeyBecomesATerminalFailure()
    {
        using var fixture = new Fixture();
        var key = fixture.Key;
        Assert.Equal(Attempt.State.Pending, fixture.Coordinator().Observe(key).Status);
        var (path, id) = Assert.Single(fixture.Launches);
        var selection = new Attempt.PlanningRead<RepositoryRecheckTestSelection>(new(true, ["GuardTests"]), null);
        Assert.Equal(0, Attempt.RunAttempt(path, id, request => new(request, null, selection)));
        Assert.Equal(Attempt.State.Completed, fixture.Coordinator().Observe(key).Status);

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "result.json"),
            JsonSerializer.Serialize(new Attempt.StoredResult(key with { Candidate = key.Candidate with { Head = "other" } }, null, selection)));
        var terminal = fixture.Coordinator().Observe(key);
        Assert.Equal(Attempt.State.Failed, terminal.Status);
        Assert.Contains("result-unusable", terminal.Detail, StringComparison.Ordinal);
        Assert.Equal(terminal, fixture.Coordinator().Observe(key));
        Assert.Single(fixture.Launches);
    }

    [Fact]
    public void StoredPlanRestoresChangedClassesForInlineMemoConsumers()
    {
        using var fixture = new Fixture();
        var key = fixture.Key with { SummaryJson = JsonSerializer.Serialize(RepositoryChangeClassifier.Classify(["src/Widget.cs"])),
            ReversePathsJson = null };
        Assert.Equal(Attempt.State.Pending, fixture.Coordinator().Observe(key).Status);
        var (path, id) = Assert.Single(fixture.Launches);
        var check = new RepositoryTestImpactCheck("infrastructure tests", ["dotnet", "test"], "changed tests")
            { ChangedTestClasses = ["ChangedWidgetTests"] };
        var plan = Attempt.StoredPlan.Capture(new(true, false, "changed tests", [check]));
        Assert.Equal(0, Attempt.RunAttempt(path, id, request => new(request, new(plan, null), null)));

        var completed = fixture.Coordinator().Observe(key);
        Assert.Equal(Attempt.State.Completed, completed.Status);
        var restored = Assert.Single(completed.Result!.Plan!.Read().Restore().Checks);
        Assert.Equal("ChangedWidgetTests", Assert.Single(restored.ChangedTestClasses));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        internal List<(string Path, Guid Id)> Launches { get; } = [];
        internal SpawnTrackedProcessStatus OwnerStatus { get; set; } = SpawnTrackedProcessStatus.LiveMatch;
        internal string? LaunchError { get; set; }
        internal Attempt.AttemptKey Key => new(new(_root, "head", "main", "base"), null,
            JsonSerializer.Serialize(new[] { "src/Widget.cs" }));

        internal Attempt Coordinator() => new(Path.Combine(_root, "attempts"), _root, (path, id) =>
        {
            Launches.Add((path, id));
            return LaunchError is { } error ? throw new InvalidOperationException(error) : Owner;
        }, _ => OwnerStatus);

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
