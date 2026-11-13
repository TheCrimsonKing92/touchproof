using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable settings and strings; no environment mutation.
public sealed class AcceptanceSelectedExecutionManifestIdentityTests
{
    [Fact]
    public void Compute_OmittedAndExplicitOff_PreserveLegacyBytes()
    {
        // Pinned pre-selection wire contract, including its property order and absence of mode.
        var changeScoped = AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled() ? "true" : "false";
        var legacy = "{\"Checks\":[],\"DeclaredToolHomes\":[],\"ChangeScopedAcceptanceEnabled\":" + changeScoped +
            ",\"Engine\":{\"MaxConcurrentShards\":1,\"EnforceStructuralCoverage\":false,\"PartitionVerdictFullRerunEveryN\":5,\"OutputCaptureLimitBytes\":1073741824,\"Timeouts\":{\"DefaultMinutes\":null,\"BuildServerShutdownMinutes\":null,\"DiscoveryMinutes\":null},\"MtpInvocations\":[]}}";
        var expected = "effective-manifest-sha256-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacy)));
        Assert.Equal(expected, Identity("{}"));
        Assert.Equal(expected, Identity("{\"engine\":{\"selectedExecutionMode\":\"Off\"}}"));
    }

    [Fact]
    public void Compute_ActiveModes_HaveDistinctEffectiveIdentities()
    {
        var identities = new[] { "Off", "Shadow", "Enforce" }.Select(mode => Identity(
            "{\"engine\":{\"selectedExecutionMode\":\"" + mode + "\"}}")).ToArray();
        Assert.Equal(3, identities.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(identities[1], Identity("{\"engine\":{\"selectedExecutionMode\":\"shadow\"}}"));
    }

    private static string Identity(string json) =>
        EffectiveAcceptanceManifestIdentity.Compute([], AcceptanceGateEngineSettings.Parse(json));
}
