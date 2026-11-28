using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ClaudeApiHoldFingerprintTests
{
    [Fact]
    public void HoldIdentity_UsesShortKeyedDigestOfExactUtf8()
    {
        const string key = "fixture-€ ";
        var secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var fingerprint = ClaudeApiHoldFingerprint.FromSecret(secret);
        var identity = Assert.IsType<string>(ClaudeCliAuthProbe.HoldIdentity(key, fingerprint));

        Assert.Matches("^api-[0-9a-f]{16}$", identity);
        Assert.Equal("api-7e20a298c60ab5ea", identity);
        Assert.Equal(identity, ClaudeCliAuthProbe.HoldIdentity(key, ClaudeApiHoldFingerprint.FromSecret(secret)));
        Assert.NotEqual(identity, ClaudeCliAuthProbe.HoldIdentity(key.TrimEnd(), fingerprint));
        Assert.NotEqual(identity, ClaudeCliAuthProbe.HoldIdentity(key, ClaudeApiHoldFingerprint.FromSecret(new byte[32])));
        var legacyHex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        Assert.DoesNotContain(identity["api-".Length..], legacyHex);
        Assert.DoesNotContain(legacyHex, identity);
        Assert.Null(ClaudeCliAuthProbe.HoldIdentity(null, fingerprint));
        Assert.Null(ClaudeCliAuthProbe.HoldIdentity(" \t", fingerprint));
    }

    [Fact]
    public void FromStateDirectory_ConcurrentInitializationUsesOnePersistedSecret()
    {
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            var identities = new string[8];
            Parallel.For(0, identities.Length, index =>
                identities[index] = ClaudeApiHoldFingerprint.FromStateDirectory(directory).Compute("fixture-key"));

            var identity = Assert.Single(identities.Distinct());
            var file = Assert.Single(Directory.GetFiles(directory));
            Assert.Equal(ClaudeApiHoldFingerprint.SecretFileName, Path.GetFileName(file));
            Assert.Equal(32, new FileInfo(file).Length);
            Assert.Equal(identity, ClaudeApiHoldFingerprint.FromStateDirectory(directory).Compute("fixture-key"));
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Fact]
    public void FromStateDirectory_MalformedSecretIsPreserved()
    {
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            var file = Path.Combine(directory, ClaudeApiHoldFingerprint.SecretFileName);
            byte[] malformed = [1, 2, 3];
            File.WriteAllBytes(file, malformed);
            var fingerprint = ClaudeApiHoldFingerprint.FromStateDirectory(directory);

            Assert.Throws<InvalidOperationException>(() => fingerprint.Compute("fixture-key"));
            Assert.Equal(malformed, File.ReadAllBytes(file));
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Fact]
    public void Matches_RequiresCurrentKeyAndMatchingProviderAndModelAcrossForms()
    {
        const string key = "fixture-key";
        var fingerprint = ClaudeApiHoldFingerprint.FromSecret(new byte[32]);
        var shortIdentity = fingerprint.Compute(key);
        var legacy = "api-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var otherLegacy = "api-" + Convert.ToHexStringLower(SHA256.HashData("other-key"u8));
        bool IsCurrent(string identity) => fingerprint.IsCurrentKeyIdentity(identity, key, StringComparison.OrdinalIgnoreCase);

        var dispatch = new TaskDispatchRecord("claude-cli", "fixture", ".", DateTimeOffset.UnixEpoch,
            ProviderName: "Anthropic", ModelName: "claude-sonnet-4-6", ClaudeApiHoldIdentity: legacy);
        var shortBinding = $"anthropic::{shortIdentity}";
        Assert.True(ProviderHoldBinding.Matches(dispatch, shortBinding, IsCurrent));
        Assert.True(ProviderHoldBinding.Matches(dispatch, $"{shortBinding}|MODEL=CLAUDE-SONNET-4-6", IsCurrent));
        Assert.False(ProviderHoldBinding.Matches(dispatch, shortBinding));
        Assert.False(ProviderHoldBinding.Matches(dispatch, $"{shortBinding}|model=claude-opus-4-6", IsCurrent));
        Assert.False(ProviderHoldBinding.Matches(dispatch, $"xai::{shortIdentity}", IsCurrent));

        var shortDispatch = dispatch with { ClaudeApiHoldIdentity = shortIdentity };
        Assert.True(ProviderHoldBinding.Matches(shortDispatch, $"ANTHROPIC::{legacy.ToUpperInvariant()}", IsCurrent));
        Assert.False(ProviderHoldBinding.Matches(shortDispatch, $"anthropic::{otherLegacy}", IsCurrent));
        Assert.False(ProviderHoldBinding.Matches(shortDispatch, $"anthropic::{legacy[..^1]}", IsCurrent));
        Assert.False(ProviderHoldBinding.Matches(shortDispatch, $"anthropic::{legacy[..^1]}z", IsCurrent));
    }
}
