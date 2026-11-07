using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: every test owns its bare remote, worktree, git environment and event sinks.
public sealed class AdoptedBuildPushServiceTests
{
    [Fact]
    public void CleanAdoption_LocalBranchMoved_PushesOnlyAdoptedCommit()
    {
        using var f = new AdoptedBuildPushFixture();
        var adopted = f.Commit("adopted");
        var later = f.Commit("later");
        Assert.True(f.Service().Push(adopted));
        Assert.Equal(adopted, f.RemoteTip);
        Assert.NotEqual(later, f.RemoteTip);
        Assert.Equal($"ADOPTED_BUILD_PUSHED commit={adopted} remote=origin added_lines=1", Assert.Single(f.Events));
        Assert.Empty(f.Attention);
        var push = Assert.Single(f.Calls.Where(args => args.Contains("push")));
        Assert.Equal($"{adopted}:refs/heads/main", push[^1]);
        Assert.Single(push.Where(arg => arg.Contains(":refs/")));
        Assert.DoesNotContain(push, arg => arg.StartsWith('+') || arg.Contains("force"));
    }

    [Fact]
    public void ScanMatches_HoldsEveryLocationWithoutMatchedContent()
    {
        using var f = new AdoptedBuildPushFixture();
        var secret = AdoptedBuildLeakScannerTests.Token;
        var adopted = f.Commit(secret + "\n" + secret);
        Assert.False(f.Service().Push(adopted));
        AssertHeld(f, "scan-match");
        var body = Assert.Single(f.Attention);
        Assert.Contains("payload.txt:1", body);
        Assert.Contains("payload.txt:2", body);
        Assert.DoesNotContain(secret, string.Join("\n", f.Events.Concat(f.Attention)));
        Assert.DoesNotContain(f.Calls, args => args.Contains("push"));
    }

    [Fact]
    public void DivergentCommit_HoldsWithoutPushing()
    {
        using var f = new AdoptedBuildPushFixture();
        f.Git("checkout", "--orphan", "divergent");
        var adopted = f.Commit("unrelated");
        Assert.False(f.Service().Push(adopted));
        AssertHeld(f, "non-fast-forward");
        Assert.DoesNotContain(f.Calls, args => args.Contains("push"));
    }

    [Theory]
    [InlineData("push-failed")]
    [InlineData("fetch-failed")]
    [InlineData("owner-patterns-missing")]
    [InlineData("patterns-invalid")]
    public void FailedPreconditionOrPush_HoldsAndRaisesAttention(string reason)
    {
        using var f = new AdoptedBuildPushFixture();
        var adopted = f.Commit("adopted");
        f.FailPush = reason == "push-failed";
        f.FailFetch = reason == "fetch-failed";
        if (reason == "owner-patterns-missing") File.Delete(f.OwnerPath);
        if (reason == "patterns-invalid") File.WriteAllText(f.OwnerPath, "{}");
        Assert.False(f.Service().Push(adopted));
        AssertHeld(f, reason);
    }

    [Fact]
    public void FailedPostPushQuery_HoldsWithAttentionDespiteUpdatedRemote()
    {
        using var f = new AdoptedBuildPushFixture();
        var adopted = f.Commit("adopted");
        f.FailVerification = true;
        Assert.False(f.Service().Push(adopted));
        Assert.Equal(adopted, f.RemoteTip);
        Assert.Contains("reason=push-unverified", Assert.Single(f.Events));
        Assert.Contains("push-adopted", Assert.Single(f.Attention));
    }

    [Fact]
    public void DifferentPushDestination_IsFetchedScannedAndVerified()
    {
        using var f = new AdoptedBuildPushFixture();
        f.Git("remote", "set-url", "--push", "origin", f.OtherRemote);
        var adopted = f.Commit("adopted");
        Assert.True(f.Service().Push(adopted));
        Assert.Equal(f.Baseline, f.RemoteTip);
        Assert.Equal(adopted, f.Git("--git-dir=" + f.OtherRemote, "rev-parse", "refs/heads/main"));
        Assert.Contains(f.Calls, args => args.Contains("fetch") && args.Contains(f.OtherRemote));
        Assert.Contains(f.Calls, args => args.Contains("ls-remote") && args.Contains(f.OtherRemote));
    }

    [Fact]
    public void RemoteAdvancesAfterScan_RejectsPushWithoutForce()
    {
        using var f = new AdoptedBuildPushFixture();
        var adopted = f.Commit("adopted");
        f.AdvanceBeforePush = true;
        Assert.False(f.Service().Push(adopted));
        Assert.NotEqual(adopted, f.RemoteTip);
        Assert.Contains("reason=push-failed", Assert.Single(f.Events));
        Assert.Contains("push-adopted", Assert.Single(f.Attention));
    }

    private static void AssertHeld(AdoptedBuildPushFixture f, string reason)
    {
        Assert.Equal(f.Baseline, f.RemoteTip);
        Assert.Contains("ADOPTED_BUILD_PUSH_HELD", Assert.Single(f.Events));
        Assert.Contains("reason=" + reason, f.Events[0]);
        var body = Assert.Single(f.Attention);
        Assert.Contains(reason, body);
        Assert.Contains("push-adopted", body);
        Assert.DoesNotContain(f.Remote, body);
    }
}
