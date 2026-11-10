using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;

// Parallel-safe: every case owns its repository, kernel and journal; build effects are injected.
public sealed class ConductorConflictIntegrationScopeTests
{
    [Fact]
    public void LegacyClarificationRetry_RequiresScopeBeforeMergeWithoutParsingAnswer()
    {
        using var fixture = new IntegrationScopeFixture();
        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Base.Developer.Id, "Historical clarification",
            RetryCause.ContractClarification, authoritativeRetryFeedback: true);
        Assert.False(fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        fixture.Base.GitFixture.AssertRestored();
        fixture.Owner.ApplyScope(fixture.Goal, fixture.Intent(IntegrationScopeAction.OriginalOnly));
        Assert.True(fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        Assert.True(ConductorConflictIntegrationMarker.Admits(fixture.Base.Developer, fixture.Base.GitFixture.Worktree));
    }

    [Fact]
    public void SameOpenRoundRetry_ReusesGrantAndRecapturesAdmission()
    {
        using var fixture = new IntegrationScopeFixture();
        fixture.Owner.ApplyScope(fixture.Goal, fixture.Intent());
        Assert.True(fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        Git(fixture.Base.GitFixture.Worktree, "merge", "--abort");
        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Base.Developer.Id, "Retry the same integration",
            RetryCause.MainDriftConflict, authoritativeRetryFeedback: true);
        var admitted = fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal);
        Assert.True(admitted.CanDispatch);
        Assert.Equal(new[] { "src/Other.cs" }, admitted.ApprovedAdditionalPaths);
        Assert.True(ConductorConflictIntegrationMarker.Admits(fixture.Base.Developer, fixture.Base.GitFixture.Worktree));
        fixture.Base.Resolve(outside: true);
        Assert.Null(fixture.Reload().Validate(fixture.Goal));
    }

    [Fact]
    public void ReplacementRoundWithSameHeads_CannotInheritOrReplayOldGrant()
    {
        using var fixture = new IntegrationScopeFixture();
        var intent = fixture.Intent();
        fixture.Owner.ApplyScope(fixture.Goal, intent);
        var oldDetail = fixture.Base.Entry.Detail!;
        GoalOperationJournal.Failed(fixture.Base.GitFixture.Repository, fixture.Goal, ConductorConflictIntegration.OperationName, oldDetail);
        var replacement = oldDetail.Replace(fixture.RoundId, Guid.NewGuid().ToString("N"), StringComparison.Ordinal);
        var node = System.Text.Json.Nodes.JsonNode.Parse(replacement)!;
        node["ScopeDecisions"] = null;
        GoalOperationJournal.Begin(fixture.Base.GitFixture.Repository, fixture.Goal, ConductorConflictIntegration.OperationName, node.ToJsonString());
        fixture.Reload().ApplyScope(fixture.Goal, intent);
        Assert.Empty(fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal).ApprovedAdditionalPaths);
        fixture.Base.Resolve(outside: true);
        Assert.Contains("src/Other.cs", fixture.Reload().Validate(fixture.Goal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameCandidate_OnlyExplicitAdjacentGrantPermitsRetention(bool approved)
    {
        using var fixture = new IntegrationScopeFixture();
        if (approved) fixture.Owner.ApplyScope(fixture.Goal, fixture.Intent());
        var admission = fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal);
        Assert.Equal(DeveloperBranchIntegrationStatus.ConflictAdmitted, admission.Status);
        Assert.Equal(new[] { WidgetPath }, admission.ConflictPaths);
        Assert.Equal(approved ? new[] { "src/Other.cs" } : [], admission.ApprovedAdditionalPaths);
        var head = fixture.Base.Resolve(outside: true);
        var failure = fixture.Reload().Validate(fixture.Goal);
        if (approved)
        {
            Assert.Null(failure);
            Assert.Equal(head, Git(fixture.Base.GitFixture.Worktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(GoalOperationStatus.Completed, fixture.Base.Entry.Status);
            Assert.Null(fixture.Reload().Validate(fixture.Goal));
        }
        else
        {
            Assert.Contains("scope check: changed outside recorded paths: src/Other.cs", failure);
            fixture.Base.GitFixture.AssertRestored();
        }
    }

    [Fact]
    public void OneApprovedPath_DoesNotAuthorizeAnotherOrRequireAnEdit()
    {
        using var fixture = new IntegrationScopeFixture();
        fixture.Owner.ApplyScope(fixture.Goal, fixture.Intent(paths: ["src/Unused.cs"]));
        fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal);
        fixture.Base.Resolve(outside: true);
        Assert.Contains("src/Other.cs", fixture.Owner.Validate(fixture.Goal));
        fixture.Base.GitFixture.AssertRestored();

        using var unused = new IntegrationScopeFixture();
        unused.Owner.ApplyScope(unused.Goal, unused.Intent(paths: ["src/Unused.cs"]));
        unused.Owner.IntegrateMainBeforeDeveloperDispatch(unused.Goal);
        unused.Base.Resolve();
        Assert.Null(unused.Owner.Validate(unused.Goal));
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("round")]
    [InlineData("original")]
    [InlineData("main")]
    [InlineData("principal")]
    [InlineData("terminal")]
    public void MismatchedAuthority_IsRejectedWithoutChangingRound(string dimension)
    {
        using var fixture = new IntegrationScopeFixture();
        var intent = fixture.Intent();
        var request = JsonSerializer.Deserialize<IntegrationScopeAuthorization>(intent.PayloadJson, OperatorIntentJson.Options)!;
        request = dimension switch
        {
            "goal" => request with { GoalId = Guid.NewGuid().ToString("N") },
            "round" => request with { RoundId = Guid.NewGuid().ToString("N") },
            "original" => request with { OriginalHead = new string('a', 40) },
            "main" => request with { MainSha = new string('b', 40) },
            _ => request
        };
        intent = intent with { PayloadJson = JsonSerializer.Serialize(request, OperatorIntentJson.Options),
            ActorKind = dimension == "principal" ? OperatorActorKind.Agent : OperatorActorKind.Human };
        if (dimension == "terminal")
            GoalOperationJournal.Failed(fixture.Base.GitFixture.Repository, fixture.Goal,
                ConductorConflictIntegration.OperationName, fixture.Base.Entry.Detail);
        var before = fixture.Base.Entry.Detail;
        Assert.Throws<InvalidOperationException>(() => fixture.Owner.ApplyScope(fixture.Goal, intent));
        Assert.Equal(before, fixture.Base.Entry.Detail);
        fixture.Base.GitFixture.AssertRestored();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Revocation_StopsFutureAdmissionsButPreservesCapturedScope(bool admitted)
    {
        using var fixture = new IntegrationScopeFixture();
        var approve = fixture.Intent();
        fixture.Owner.ApplyScope(fixture.Goal, approve);
        if (admitted) fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal);
        fixture.Reload().ApplyScope(fixture.Goal, fixture.Intent(IntegrationScopeAction.Revoke, approvalId: approve.Id));
        fixture.Reload().ApplyScope(fixture.Goal, approve); // Redelivery cannot resurrect the grant.
        var result = fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal);
        if (admitted)
        {
            Assert.True(result.CanDispatch);
            fixture.Base.Resolve(outside: true);
            Assert.Null(fixture.Reload().Validate(fixture.Goal));
        }
        else
        {
            Assert.False(result.CanDispatch);
            Assert.Equal(0, fixture.Base.InnerCalls);
            Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
            fixture.Base.GitFixture.AssertRestored();
        }
    }

    [Theory]
    [InlineData("../other.cs")]
    [InlineData("/root.cs")]
    [InlineData("src/*.cs")]
    [InlineData("src")]
    [InlineData(".git/config")]
    [InlineData(".git./config")]
    [InlineData(".orchestrator/state.db")]
    [InlineData("C:\\other.cs")]
    public void PathGrant_RejectsImplicitOrEscapingScope(string path)
    {
        using var fixture = new IntegrationScopeFixture();
        Assert.Throws<InvalidOperationException>(() => fixture.Owner.ApplyScope(fixture.Goal, fixture.Intent(paths: [path])));
        fixture.Base.GitFixture.AssertRestored();
    }
}
