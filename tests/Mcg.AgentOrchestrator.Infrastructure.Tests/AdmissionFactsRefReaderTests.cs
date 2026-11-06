using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = GitRefFileResolverParityTests.Fixture;

// Parallel-safe: private git repositories; environment hazards use an injected accessor.
public sealed class AdmissionFactsRefReaderTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void OrdinaryConfig_TickQueuesAndConsumesFactsInsteadOfHoldingForever()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Configured admission repository");
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        var batches = 0;
        var reader = new AdmissionFactsTickReader(fixture.Repository, "main", (inputs, _) =>
        {
            Interlocked.Increment(ref batches);
            return inputs.Select(AdmissionFactsTestData.Fact).ToArray();
        });
        try
        {
            reader.Begin([goal], _ => []);
            var inputs = Assert.IsType<AdmissionFactsInputs>(reader.Inputs(goal.Id));
            Assert.Equal(fixture.OriginalHead, inputs.Branch);
            Assert.Equal(fixture.MainHead, inputs.Main);
            Assert.Null(reader.Facts(goal.Id));
            AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Configured repository was never queued.");
            reader.Begin([goal], _ => []);
            Assert.Equal(inputs, Assert.IsType<AdmissionFacts>(reader.Facts(goal.Id)).Inputs);
            Assert.Equal(1, batches);
        }
        finally
        {
            reader.Derivation.Stop();
            AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Configured repository worker did not exit.");
        }
    }

    [Theory]
    [InlineData("loose")]
    [InlineData("packed")]
    [InlineData("linked")]
    [InlineData("detached")]
    [InlineData("sha256")]
    public void OrdinaryConfig_ObservesRefsAndMainAdvanceWithoutGitFallback(string shape)
    {
        using var fixture = new Fixture(sha256: shape == "sha256");
        fixture.Git("config", "user.name", "Admission Reader");
        fixture.Git("config", "core.autocrlf", "false");
        fixture.Git("config", "commit.gpgSign", "false");
        // This is intentionally outside the general resolver's proof-of-git-success contract.
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, "HEAD", out _));
        var repo = fixture.Repo;
        if (shape == "packed") fixture.Git("pack-refs", "--all");
        if (shape == "linked")
        {
            repo = Path.Combine(fixture.Root, "linked");
            fixture.Git("worktree", "add", "-b", "other", repo);
        }
        if (shape == "detached") fixture.Git("checkout", "--detach");
        var old = fixture.Oracle("refs/heads/fixture-main");
        Assert.True(AdmissionFactsRefReader.TryResolve(repo, "HEAD", out var head));
        Assert.Equal(old, head);
        Assert.True(AdmissionFactsRefReader.TryResolve(repo, "refs/heads/fixture-main", out var main));
        Assert.Equal(old, main);
        // In the detached case move main explicitly, leaving HEAD at the old commit.
        fixture.Git("commit", "--allow-empty", "-m", "Main advances");
        if (shape == "detached") fixture.Git("branch", "-f", "fixture-main", "HEAD");
        var next = fixture.Oracle("refs/heads/fixture-main");
        Assert.NotEqual(old, next);
        Assert.True(AdmissionFactsRefReader.TryResolve(repo, "refs/heads/fixture-main", out main));
        Assert.Equal(next, main);
        Assert.True(AdmissionFactsRefReader.TryResolve(repo, "HEAD", out head));
        Assert.Equal(shape == "linked" ? old : next, head);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("symbolic")]
    [InlineData("uppercase")]
    [InlineData("ref-lock")]
    [InlineData("HEAD.lock")]
    [InlineData("packed-refs.lock")]
    [InlineData("reftable")]
    [InlineData("GIT_DIR")]
    [InlineData("GIT_NAMESPACE")]
    public void UncertainRef_RemainsPendingInsteadOfReturningAnId(string hazard)
    {
        using var fixture = new Fixture();
        const string reference = "refs/heads/fixture-main";
        var metadata = Path.Combine(fixture.Repo, ".git");
        var path = Path.Combine(metadata, reference.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(AdmissionFactsRefReader.TryResolve(fixture.Repo, reference, out var original));
        Func<string, string?> environment = _ => null;
        if (hazard.StartsWith("GIT_", StringComparison.Ordinal)) environment = name => name == hazard ? metadata : null;
        else if (hazard == "reftable") Directory.CreateDirectory(Path.Combine(metadata, "reftable"));
        else if (hazard == "ref-lock") File.WriteAllText(path + ".lock", "");
        else if (hazard.EndsWith(".lock", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(metadata, hazard), "");
        else File.WriteAllText(path, hazard switch
        {
            "malformed" => "not-an-id\n",
            "symbolic" => "ref: refs/heads/other\n",
            _ => original.ToUpperInvariant() + "\n"
        });
        Assert.False(AdmissionFactsRefReader.TryResolve(fixture.Repo, reference, out var actual, environment));
        Assert.Empty(actual);
        Assert.False(AdmissionFactsRefReader.TryResolve(fixture.Repo, "HEAD", out _, environment));
    }
}
