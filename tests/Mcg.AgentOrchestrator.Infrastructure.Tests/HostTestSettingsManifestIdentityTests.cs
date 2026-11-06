using Mcg.AgentOrchestrator.Infrastructure;
using static HostTestSettingsFixture;

// Parallel-safe: immutable manifest and private configuration files.
public sealed class HostTestSettingsManifestIdentityTests
{
    [Fact]
    public void HostOnlyChanges_AlterExecutionSettingsWithoutChangingManifestOrCandidateBinding()
    {
        using var fixture = new HostTestSettingsFixture();
        var manifest = Manifest;
        var identity = EffectiveAcceptanceManifestIdentity.Compute([], manifest);
        var requests = new List<RemoteLaneRequest>();
        var local = new List<string>();
        foreach (var value in new[] { "2", "3" })
        {
            var config = fixture.Load("{\"executors\":[{\"id\":\"one\",\"testSettings\":{\"" + Slots + "\":\"" + value +
                "\"}}],\"lanes\":[\"lane\"],\"localTestSettings\":{\"" + Slots + "\":\"" + value + "\"}}");
            using var coordinator = fixture.Coordinator(config, manifest);
            requests.Add(coordinator.CreateRequest("lane", "tests.csproj", "filter", Identity(identity), "one"));
            local.Add(coordinator.HostTestSettings.Local[Slots]);
            Assert.Equal(identity, EffectiveAcceptanceManifestIdentity.Compute([], manifest));
        }
        Assert.Equal(new[] { "2", "3" }, requests.Select(request => request.TestSettings![Slots]));
        Assert.Equal(new[] { "2", "3" }, local);
        Assert.Equal(requests[0].ManifestIdentity, requests[1].ManifestIdentity);
        Assert.Equal(identity, requests[0].ManifestIdentity);
        Assert.Equal(requests[0].CandidateTreeSha, requests[1].CandidateTreeSha);
        Assert.Equal(requests[0].VerifyingCommitSha, requests[1].VerifyingCommitSha);
        Assert.Equal("4", manifest.DeclaredTestSettings[Slots]);
    }
}
