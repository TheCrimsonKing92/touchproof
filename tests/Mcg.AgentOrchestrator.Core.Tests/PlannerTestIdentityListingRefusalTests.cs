using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: all sources and snapshots belong to this test, never the process default.
public sealed class PlannerTestIdentityListingRefusalTests
{
    private static readonly RunnerTestIdentity[] Listing =
    [
        new("process-inspector::tree_cli::pid_json_lists_spawned_child", "pid_json_lists_spawned_child"),
        new("process-inspector::tree::tests::groups_children_sorted", "tree::tests::groups_children_sorted")
    ];

    [Fact]
    public void DotFormSelector_CompleteListing_RefusesWithFormAndReplan()
    {
        const string selector = "process-inspector::tree_cli.pid_json_lists_spawned_child";
        var refusal = Refusal([selector], (_, _) => Listing);

        Assert.NotNull(refusal);
        Assert.Contains("'" + selector + "'", refusal);
        Assert.Contains("keeping every :: separator", refusal);
        Assert.Contains("replan", refusal);
    }

    [Theory]
    [InlineData("process-inspector::tree_cli::pid_json_lists_spawned_child")]
    [InlineData("process-inspector::tree::tests::groups_children_sorted")]
    [InlineData("pid_json_lists_spawned_child")]
    [InlineData("tree::tests::groups_children_sorted")]
    public void ExactIdentityOrName_CompleteListing_Accepts(string selector) =>
        Assert.Null(Refusal([selector], (_, _) => Listing));

    [Theory]
    [InlineData("PROCESS-inspector::tree_cli::pid_json_lists_spawned_child")]
    [InlineData("pid_json_lists_spawned_*")]
    [InlineData(" pid_json_lists_spawned_child ")]
    [InlineData("groups_children_sorted")]
    public void InexactIdentity_CompleteListing_Refuses(string selector) =>
        Assert.Contains("'" + selector + "'", Refusal([selector], (_, _) => Listing)!);

    [Fact]
    public void UnmatchedSelectors_Duplicates_ReportsDistinctInInputOrder()
    {
        var refusal = Refusal(["second", Listing[0].Identity, "first", "second"], (_, _) => Listing);
        Assert.NotNull(refusal);
        Assert.Contains("'second', 'first'", refusal);
        Assert.Equal(refusal.IndexOf("'second'", StringComparison.Ordinal),
            refusal.LastIndexOf("'second'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("throw")]
    public void UnavailableListing_IdentityOnly_DoesNotResolveChecks(string state)
    {
        Assert.Null(Refusal(["formerly.accepted"], (_, _) => state switch
        {
            "null" => null,
            "empty" => [],
            _ => throw new InvalidOperationException("listing unavailable")
        }, (_, _) => throw new InvalidOperationException("must not resolve checks")));
    }

    [Fact]
    public void CommandOnlyRound_ListingSource_IsNotCalled()
    {
        var calls = 0;
        var refusal = Refusal(["npm unmatched"], (_, _) => { calls++; return Listing; });
        Assert.Equal(0, calls);
        Assert.Contains("match no declared gate check", refusal!);
    }

    [Theory]
    [InlineData("npm declared", false)]
    [InlineData("npm unmatched", true)]
    public void MixedRound_ListingAvailable_CommandsUseDeclaredChecks(string command, bool rejected)
    {
        var refusal = Refusal([command, Listing[0].Identity], (_, _) => Listing,
            (_, _) => [new("declared", selector => selector == "npm declared")]);
        if (rejected) Assert.Contains("match no declared gate check", refusal!);
        else Assert.Null(refusal);
    }

    [Fact]
    public void SourceCancellation_Snapshot_Propagates()
    {
        Assert.Throws<OperationCanceledException>(() => Refusal(["test"],
            (_, _) => throw new OperationCanceledException()));
    }

    [Fact]
    public void OpaqueNames_DuplicateAcrossBinaries_AcceptsEitherForm()
    {
        RunnerTestIdentity[] listing = [new("full-one", "unrelated-name"), new("full-two", "unrelated-name")];
        Assert.Null(Refusal(["unrelated-name", "full-one", "full-two"], (_, _) => listing));
        Assert.NotNull(Refusal(["one"], (_, _) => listing));
    }

    private static string? Refusal(string[] selectors,
        Func<Goal, TaskSpec, IReadOnlyList<RunnerTestIdentity>?> source,
        Func<Goal, TaskSpec, IReadOnlyList<DeclaredGateCheck>>? checks = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Validate identity selectors", [task]);
        var snapshot = PlannerTestIdentityListing.Snapshot(selectors, goal, task, source);
        return PlannerCommandSelectorValidation.Refusal(selectors, goal, task,
            checks ?? ((_, _) => [new("tests", _ => false, "junit-command", "cargo")]), snapshot);
    }
}
