using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all scopes and goals are in memory; no repository filesystem lookup.
public sealed class BoardFillConcreteFileDependsTests
{
    [Theory]
    [InlineData("src/Feature/", "src/Feature/", ScopeCollisionKind.ExactFile)]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/", ScopeCollisionKind.ExactFile)]
    [InlineData("src/Feature/", "src/Feature/Shared.cs", ScopeCollisionKind.DirectoryPrefix)]
    [InlineData("src/Feature/Shared.cs", "src/Feature/", ScopeCollisionKind.DirectoryPrefix)]
    public void Propose_DirectoryOverlap_OmitsDependencyKeepsVerdict(
        string activePath, string draftPath, ScopeCollisionKind kind)
    {
        var kernel = new AgentOrchestratorKernel();
        var active = kernel.CreateGoal($"Change {activePath} only.");
        var draft = $"Change {draftPath} only.";
        var report = GoalScopeCollisionAdvisor.Build([draft], kernel.Goals, "draft");
        var collision = Assert.Single(report.Collisions);
        Assert.Equal(active.Id.Value, collision.GoalId);
        Assert.Equal(kind, collision.Kind);
        Assert.StartsWith("overlap-detected", report.VerdictToken);

        var proposal = BoardFillScopeDepends.Propose(draft, kernel.Goals, "draft");

        Assert.Empty(proposal.Depends);
        Assert.Equal(report.VerdictToken, proposal.Verdict);
    }

    [Theory]
    [InlineData("src/Feature/Shared.cs", "src/Feature/Shared.cs")]
    [InlineData("src/Feature/My.Thing.Tests.cs", "src/Feature/My.Thing.Tests.cs")]
    [InlineData("SRC/Feature/Shared.cs", "src/Feature/Shared.cs")]
    public void Propose_SharedConcreteFile_AddsDependency(string activePath, string draftPath)
    {
        var kernel = new AgentOrchestratorKernel();
        var active = kernel.CreateGoal($"Change {activePath} and src/Other/.");

        var proposal = BoardFillScopeDepends.Propose(
            $"Change {draftPath} and src/Other/Noise.cs.", kernel.Goals, "draft");

        var dependency = Assert.Single(proposal.Depends);
        Assert.Equal(active.Id.Value, dependency.GoalId);
        Assert.Equal($"ExactFile {draftPath}<->{activePath}", dependency.Reason);
        Assert.StartsWith("overlap-detected", proposal.Verdict);
    }

    [Fact]
    public void Propose_SiblingFiles_OmitsDependency()
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Change src/Feature/A.cs only.");

        var proposal = BoardFillScopeDepends.Propose(
            "Change src/Feature/B.cs only.", kernel.Goals, "draft");

        Assert.Empty(proposal.Depends);
    }

    [Theory]
    [InlineData("src/Feature/", false)]
    [InlineData("src/Feature\\", false)]
    [InlineData("src/Feature.Tests/", false)]
    [InlineData("src/Feature.Tests\\", false)]
    [InlineData("src/Feature/*.cs", false)]
    [InlineData("src/Feature/F?o.cs", false)]
    [InlineData("src/[Feature]/File.cs", false)]
    [InlineData("src/Feature]/File.cs", false)]
    [InlineData("src/{Feature}/File.cs", false)]
    [InlineData("src/Feature}/File.cs", false)]
    [InlineData("src/.gitignore", false)]
    [InlineData("src/Feature/Makefile", false)]
    [InlineData("src/Feature/name.", false)]
    [InlineData("src/Feature/Shared.cs", true)]
    [InlineData("src/Feature/My.Thing.Tests.cs", true)]
    // Without a trailing separator, the operator's filename grammar is the tiebreaker.
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests", true)]
    public void CreatesDependency_PathGrammar_RequiresConcreteFile(string path, bool expected)
    {
        Assert.Equal(expected, BoardFillScopeDepends.CreatesDependency(Collision(path, path)));
    }

    [Theory]
    [InlineData("src/Feature.Tests/", "src/Feature.Tests")]
    [InlineData("src/Feature.Tests", "src/Feature.Tests/")]
    [InlineData("src/Feature.Tests\\", "src/Feature.Tests")]
    [InlineData("src/Feature.Tests", "src/Feature.Tests\\")]
    [InlineData(" src/Feature.Tests/ ", "src/Feature.Tests")]
    public void CreatesDependency_EitherSideDeclaresDirectory_OmitsDependency(
        string proposed, string conflicting)
    {
        Assert.Equal(PathOverlapKind.Exact, RepositoryPathOverlap.Classify(proposed, conflicting));
        Assert.False(BoardFillScopeDepends.CreatesDependency(Collision(proposed, conflicting)));
    }

    [Fact]
    public void CreatesDependency_DirectoryPrefixWithFileLikeNames_OmitsDependency()
    {
        var collision = Collision("src/Feature.cs", "src/Feature.cs/Other.cs") with
        {
            Kind = ScopeCollisionKind.DirectoryPrefix
        };

        Assert.False(BoardFillScopeDepends.CreatesDependency(collision));
    }

    private static ScopeCollision Collision(string proposed, string conflicting) =>
        new("other-goal", "other", "Active", ScopeCollisionKind.ExactFile,
            proposed, FileScopeProvenance.Explicit, conflicting, FileScopeProvenance.Explicit);
}
