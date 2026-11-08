using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: two case-owned real repositories and stores; only the verifier's process runner is injected.
public sealed class MultiRepositoryAcceptanceGateTests
{
    [Fact]
    public void AdvanceOnce_TwoRepositoriesPass_RecordsTupleReceiptAndLands()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var tuple = fixture.Tuple();
        var result = fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("Landed:", Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome).Description);
        Assert.Equal(new[] { ("default", fixture.HomeWorktree), ("crosswalk", fixture.SecondaryWorktree) }, fixture.Calls);
        var receipt = Assert.IsType<MultiRepositoryAcceptanceReceipt>(fixture.Gate.Store.TryRead(fixture.Goal.Id));
        Assert.True(receipt.Passed);
        Assert.Equal(tuple.Canonical, receipt.TupleCanonical);
        Assert.Equal(new[] { "crosswalk", "default" }, receipt.Outcomes.Select(outcome => outcome.Key));
        Assert.All(receipt.Outcomes, outcome => Assert.True(File.Exists(outcome.EvidenceReference)));
        Assert.Single(Directory.GetFiles(fixture.Gate.Store.DirectoryPath, "*.json"));
        Assert.Equal(2, receipt.IntegrationBases.Count);
        Assert.Equal(1, fixture.LandingCalls);
    }

    [Fact]
    public void AdvanceOnce_SecondRepositoryFails_NamesKeyAndRoutesDeveloperWithEvidence()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture { FailSecondary = true };
        var result = fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(2, fixture.Calls.Count);
        var receipt = fixture.Gate.Store.TryRead(fixture.Goal.Id)!;
        Assert.False(receipt.Passed);
        var failed = Assert.Single(receipt.Outcomes, outcome => !outcome.Passed);
        Assert.Equal("crosswalk", failed.Key);
        var feedback = Assert.Single(fixture.RetryMessages);
        Assert.Contains("crosswalk", feedback);
        Assert.Contains(failed.EvidenceReference, feedback);
        Assert.Contains("crosswalk", string.Join(',', fixture.Goal.RetainedAcceptanceFailure!.FailedChecks));
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Goal.Tasks.Single().Status);
        Assert.Equal(0, fixture.LandingCalls);
    }

    [Fact]
    public void AdvanceOnce_RepositoryCommitAfterPassingReceipt_RegatesWithoutLandingHold()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var driver = fixture.Driver();
        Assert.NotNull(fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome).Landable);
        var old = fixture.Gate.Store.TryRead(fixture.Goal.Id)!;
        GoalRepositoryWorktreeProvisionerFixture.CommitChange(fixture.SecondaryWorktree);
        fixture.FailSecondary = true;
        Assert.False(old.IsCurrentFor(fixture.Tuple()));
        Assert.Null(fixture.Gate.Inspect(fixture.Goal, GoalLifecycleState.Verified).Hold!.Decision);

        var result = driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(new[] { "default", "crosswalk", "default", "crosswalk" }, fixture.Calls.Select(call => call.Key));
        var current = fixture.Gate.Store.TryRead(fixture.Goal.Id)!;
        Assert.NotEqual(old.TupleCanonical, current.TupleCanonical);
        Assert.False(current.Passed);
        Assert.Single(fixture.RetryMessages);
        Assert.Equal(0, fixture.LandingCalls);
    }

    [Fact]
    public void Run_HomeFails_StillRunsAndRecordsSecondary()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture { FailHome = true };
        var result = fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome);
        Assert.Null(result.Hold);
        Assert.Contains("default", result.Failure!.FailureDetail);
        Assert.Equal(new[] { "default", "crosswalk" }, fixture.Calls.Select(call => call.Key));
        Assert.Equal(2, fixture.Gate.Store.TryRead(fixture.Goal.Id)!.Outcomes.Count);
    }

    [Fact]
    public void Run_CandidateChangesDuringCheck_PreservesStaleReceiptWithoutLandingHold()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        fixture.OnCheck = key => { if (key == "crosswalk") GoalRepositoryWorktreeProvisionerFixture.CommitChange(fixture.SecondaryWorktree); };
        var result = fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome);
        Assert.Contains("candidate changed", result.Hold!.Reason);
        Assert.Null(result.Hold.Decision);
        Assert.False(fixture.Gate.Store.TryRead(fixture.Goal.Id)!.IsCurrentFor(fixture.Tuple()));
        Assert.False(fixture.Gate.HasCurrentPass(fixture.Goal));
    }

    [Fact]
    public void Run_ProjectManifestChanges_RegatesEveryRepository()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome);
        var old = fixture.Gate.Store.TryRead(fixture.Goal.Id)!;
        File.WriteAllText(fixture.SecondaryManifest, MultiRepositoryAcceptanceGateFixture.Manifest("revised secondary check", "--version"));
        Assert.False(old.IsCurrentFor(fixture.Tuple()));
        var result = fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome);
        Assert.NotNull(result.Landable);
        Assert.Equal(4, fixture.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_SecondaryInputMissing_FailsWithKeyedEvidence(bool missingWorktree)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        if (missingWorktree)
            fixture.Repositories.Crosswalk.Git("worktree", "remove", fixture.SecondaryWorktree);
        else
            File.Delete(fixture.SecondaryManifest);
        var result = fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome);
        Assert.Null(result.Hold);
        var check = Assert.Single(result.Failure!.RequiredUnmetCriteria);
        Assert.Equal("crosswalk: acceptance", check.Name);
        Assert.Contains("evidence=", check.OutputTail);
        var path = check.OutputTail!.Split(Environment.NewLine)[0]["evidence=".Length..];
        Assert.True(File.Exists(path));
        Assert.Null(fixture.Gate.Store.TryRead(fixture.Goal.Id));
        Assert.False(fixture.Gate.HasCurrentPass(fixture.Goal));
    }

    [Fact]
    public void Run_UnregisteredProject_RecordsFailureWithoutHomeManifestFallback()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        // Keep the candidate computable independently of registry resolution.
        var config = Path.Combine(fixture.SecondaryWorktree, "config");
        Directory.CreateDirectory(config);
        File.Copy(fixture.SecondaryManifest, Path.Combine(config, "acceptance-manifest.json"));
        GoalRepositoryWorktreeProvisionerFixture.Run(fixture.SecondaryWorktree, "add", "config/acceptance-manifest.json");
        GoalRepositoryWorktreeProvisionerFixture.Run(fixture.SecondaryWorktree, "commit", "-m", "Seed secondary manifest");
        var registry = new OrchestratorProjectRegistry(Path.Combine(fixture.Repositories.Home.Root, "empty-registry"), fixture.Repositories.Registry.DataRootDirectory);
        var gate = new MultiRepositoryAcceptanceGate(fixture.Repositories.Workspace, registry, fixture.Verifier);

        var result = gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome);

        Assert.Null(result.Hold);
        var receipt = gate.Store.TryRead(fixture.Goal.Id)!;
        Assert.Equal(2, receipt.Outcomes.Count);
        var failed = Assert.Single(receipt.Outcomes, outcome => !outcome.Passed);
        Assert.Equal("crosswalk", failed.Key);
        Assert.Contains("Unknown project", failed.Detail);
        Assert.Equal("default", Assert.Single(fixture.Calls).Key);
        Assert.True(File.Exists(failed.EvidenceReference));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_LandWithoutCurrentPass_NeitherGatesNorMerges(bool staleReceipt)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        if (staleReceipt)
        {
            fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome);
            GoalRepositoryWorktreeProvisionerFixture.CommitChange(fixture.SecondaryWorktree);
        }
        var calls = fixture.Calls.Count;
        var head = fixture.Repositories.Home.Git("rev-parse", "HEAD");

        Assert.True(CliCommandHandlers.Execute(["land", fixture.Goal.Id.Value], fixture.Context()));

        Assert.Equal(calls, fixture.Calls.Count);
        Assert.DoesNotContain(MultiRepositoryGoal.Route, fixture.Goal.LandingBlocker?.Kind ?? "");
        Assert.Equal(head, fixture.Repositories.Home.Git("rev-parse", "HEAD"));
        Assert.True(Directory.Exists(fixture.HomeWorktree));
        Assert.True(Directory.Exists(fixture.SecondaryWorktree));
    }

    [Fact]
    public void Execute_RegistryCheckoutChanges_ValidatesRecordedIntegrationBase()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome);
        var receipt = fixture.Gate.Store.TryRead(fixture.Goal.Id)!;
        Assert.True(receipt.Passed);
        // The tuple and manifest stay fixed; only registry ownership changes.
        var registry = new OrchestratorProjectRegistry(Path.Combine(fixture.Repositories.Home.Root, "changed-registry"), fixture.Repositories.Registry.DataRootDirectory);
        registry.CreateProject("crosswalk", fixture.Repositories.Home.CheckoutPath, "main");
        var candidate = GoalRepositoryCandidateIdentity.Compute(fixture.Goal, fixture.Repositories.Workspace, registry);
        Assert.Equal(receipt.TupleCanonical, candidate.Identity!.RepositoryTupleCanonical);
        var gate = new MultiRepositoryAcceptanceGate(fixture.Repositories.Workspace, registry, fixture.Verifier);
        Assert.True(gate.HasCurrentPass(fixture.Goal));
        Assert.NotNull(gate.Inspect(fixture.Goal, GoalLifecycleState.Verified).Landable);
        var landing = new MultiRepositoryTupleLanding(fixture.Repositories.Workspace, registry);
        var result = landing.Execute(fixture.Goal, GoalLifecycleState.Verified, receipt,
            () => throw new InvalidOperationException("Must not land a stale base"), () => null);
        Assert.Equal("multi-repository-acceptance-stale", result.Hold!.Decision!.DiscriminatingEvidence);
        Assert.Contains("crosswalk", result.Hold.Reason);
        Assert.Equal(2, fixture.Calls.Count);
    }

    [Theory]
    [InlineData("acceptance")]
    [InlineData("accept")]
    public void Execute_AcceptanceCommand_GatesSetWithoutMerging(string command)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var before = fixture.Repositories.Home.Git("rev-parse", "HEAD");
        CliCommandHandlers.Execute([command, fixture.Goal.Id.Value, "--no-record", "--autonomy", AutonomyPolicy.SupervisedAuto.Name], fixture.Context());
        Assert.True(fixture.Gate.Store.TryRead(fixture.Goal.Id)!.Passed);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(before, fixture.Repositories.Home.Git("rev-parse", "HEAD"));
        Assert.True(Directory.Exists(fixture.HomeWorktree));
        Assert.Equal("multi-repository-landing/" + MultiRepositoryGoal.Route, fixture.Goal.LandingBlocker!.Kind);
    }

}
