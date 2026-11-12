using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its repositories, git environment and sinks.
public sealed class AdoptedBuildPushServiceRepositoryLabelTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("companion")]
    public void CleanCommit_PushesWithOptionalRepositoryField(string? label)
    {
        using var f = new AdoptedBuildPushFixture();
        var commit = f.Commit("adopted");
        var service = new AdoptedBuildPushService(f.Repository, AdoptedBuildLeakScannerTests.GenericPath,
            f.OwnerPath, f.Events.Add, f.Attention.Add, f.Run, label);

        Assert.True(service.Push(commit));

        Assert.Equal(commit, f.RemoteTip);
        var field = label is null ? "" : " repository=companion";
        Assert.Equal($"ADOPTED_BUILD_PUSHED commit={commit} remote=origin{field} added_lines=1", Assert.Single(f.Events));
        Assert.Empty(f.Attention);
    }

    [Fact]
    public void MissingOwnerPatterns_UnlabelledHoldPreservesExactText()
    {
        using var f = new AdoptedBuildPushFixture();
        var commit = f.Commit("adopted");
        File.Delete(f.OwnerPath);

        Assert.False(f.Service().Push(commit));

        Assert.Equal($"ADOPTED_BUILD_PUSH_HELD commit={commit} remote=origin reason=owner-patterns-missing", Assert.Single(f.Events));
        Assert.Equal($"Adopted build publication held. commit={commit} remote=origin reason=owner-patterns-missing\n\n" +
            $"Next: push-adopted {commit} --remote origin --branch main", Assert.Single(f.Attention));
    }

    [Fact]
    public void MissingOwnerPatterns_LabelledHoldIdentifiesManualPublication()
    {
        using var f = new AdoptedBuildPushFixture();
        var commit = f.Commit("adopted");
        File.Delete(f.OwnerPath);
        var service = new AdoptedBuildPushService(f.Repository, AdoptedBuildLeakScannerTests.GenericPath,
            f.OwnerPath, f.Events.Add, f.Attention.Add, f.Run, "companion");

        Assert.False(service.Push(commit));

        Assert.Equal($"ADOPTED_BUILD_PUSH_HELD commit={commit} remote=origin repository=companion reason=owner-patterns-missing",
            Assert.Single(f.Events));
        var body = Assert.Single(f.Attention);
        Assert.Contains("repository=companion", body);
        Assert.Contains("manually publish", body);
        Assert.DoesNotContain("push-adopted", body);
        Assert.Equal(f.Baseline, f.RemoteTip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("companion")]
    public void VerifiedPush_EventWriteFails_AttentionKeepsPublicationIdentity(string? label)
    {
        using var f = new AdoptedBuildPushFixture();
        var commit = f.Commit("adopted");
        var service = new AdoptedBuildPushService(f.Repository, AdoptedBuildLeakScannerTests.GenericPath,
            f.OwnerPath, _ => throw new IOException("private diagnostic"), f.Attention.Add, f.Run, label);

        Assert.True(service.Push(commit));

        Assert.Equal(commit, f.RemoteTip);
        var body = Assert.Single(f.Attention);
        if (label is null)
            Assert.Equal($"Adopted build publication verified. commit={commit} remote=origin " +
                "reason=pushed-event-write-failed. The push succeeded; its event could not be recorded. " +
                $"Next: push-adopted {commit} --remote origin --branch main", body);
        else
        {
            Assert.Contains("repository=companion", body);
            Assert.Contains("reason=pushed-event-write-failed", body);
            Assert.DoesNotContain("push-adopted", body);
        }
        Assert.DoesNotContain("private diagnostic", body);
    }
}
