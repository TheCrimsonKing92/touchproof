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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromStateDirectory_FailedMoveUsesPublishedSecret(bool accessDenied)
    {
        const string key = "fixture-key";
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            var winner = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            Exception failure = accessDenied ? new UnauthorizedAccessException() : new NotSupportedException();
            var fingerprint = ClaudeApiHoldFingerprint.FromStateDirectory(directory, (_, destination) =>
            {
                File.WriteAllBytes(destination, winner);
                throw failure;
            });

            var identity = fingerprint.Compute(key);
            Assert.Equal(ClaudeApiHoldFingerprint.FromSecret(winner).Compute(key), identity);
            Assert.Equal(ClaudeApiHoldFingerprint.FromStateDirectory(directory).Compute(key), identity);
            var file = Assert.Single(Directory.GetFiles(directory));
            Assert.Equal(ClaudeApiHoldFingerprint.SecretFileName, Path.GetFileName(file));
            Assert.Equal(winner, File.ReadAllBytes(file));
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FromStateDirectory_FailedMoveWithoutValidSecretReportsUnavailable(bool malformedDestination)
    {
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            byte[] malformed = [1, 2, 3];
            var failure = new IOException("Injected move failure.");
            var fingerprint = ClaudeApiHoldFingerprint.FromStateDirectory(directory, (_, destination) =>
            {
                if (malformedDestination)
                    File.WriteAllBytes(destination, malformed);
                throw failure;
            });

            var error = Assert.Throws<InvalidOperationException>(() => fingerprint.Compute("fixture-key"));
            var path = Path.Combine(directory, ClaudeApiHoldFingerprint.SecretFileName);
            Assert.Equal($"API hold fingerprint secret is unavailable at '{path}'.", error.Message);
            Assert.Same(failure, error.InnerException);
            if (malformedDestination)
            {
                Assert.Equal(path, Assert.Single(Directory.GetFiles(directory)));
                Assert.Equal(malformed, File.ReadAllBytes(path));
            }
            else
            {
                Assert.Empty(Directory.GetFiles(directory));
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadSecret_TransientFailuresSucceedOnThirdAttempt(bool accessDenied)
    {
        const string key = "fixture-key";
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            var secret = new byte[32];
            File.WriteAllBytes(Path.Combine(directory, ClaudeApiHoldFingerprint.SecretFileName), secret);
            Exception failure = accessDenied ? new UnauthorizedAccessException() : new IOException();
            var attempts = 0;
            var fingerprint = ClaudeApiHoldFingerprint.FromStateDirectory(directory, File.Move, path =>
            {
                if (++attempts < 3)
                    throw failure;
                return File.OpenRead(path);
            });

            Assert.Equal(ClaudeApiHoldFingerprint.FromSecret(secret).Compute(key), fingerprint.Compute(key));
            Assert.Equal(3, attempts);
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadSecret_PersistentFailuresReportUnavailableAfterThirdAttempt(bool accessDenied)
    {
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            Exception failure = accessDenied ? new UnauthorizedAccessException() : new IOException();
            var attempts = 0;
            var fingerprint = ClaudeApiHoldFingerprint.FromStateDirectory(directory, File.Move, _ =>
            {
                attempts++;
                throw failure;
            });

            var error = Assert.Throws<InvalidOperationException>(() => fingerprint.Compute("fixture-key"));
            var path = Path.Combine(directory, ClaudeApiHoldFingerprint.SecretFileName);
            Assert.Equal($"API hold fingerprint secret is unavailable at '{path}'.", error.Message);
            Assert.Same(failure, error.InnerException);
            Assert.Equal(3, attempts);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadSecret_MissingPathsProceedToPublishWithoutRetry(bool missingDirectory)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var directory = missingDirectory ? Path.Combine(root, "missing") : root;
            var attempts = 0;
            var attemptsBeforePublish = 0;
            var fingerprint = ClaudeApiHoldFingerprint.FromStateDirectory(directory, (source, destination) =>
            {
                attemptsBeforePublish = attempts;
                File.Move(source, destination);
            }, path =>
            {
                attempts++;
                return File.OpenRead(path);
            });

            var identity = fingerprint.Compute("fixture-key");
            Assert.Equal(1, attemptsBeforePublish);
            Assert.Equal(2, attempts);
            Assert.Equal(ClaudeApiHoldFingerprint.FromStateDirectory(directory).Compute("fixture-key"), identity);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void ReadSecret_InvalidLengthReportsErrorWithoutRetry()
    {
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, ClaudeApiHoldFingerprint.SecretFileName);
            byte[] malformed = [1, 2, 3];
            File.WriteAllBytes(path, malformed);
            var attempts = 0;
            var fingerprint = ClaudeApiHoldFingerprint.FromStateDirectory(directory, File.Move, file =>
            {
                attempts++;
                return File.OpenRead(file);
            });

            var error = Assert.Throws<InvalidOperationException>(() => fingerprint.Compute("fixture-key"));
            Assert.Equal($"API hold fingerprint secret has an invalid length at '{path}'.", error.Message);
            Assert.Equal(1, attempts);
            Assert.Equal(malformed, File.ReadAllBytes(path));
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Fact]
    public async Task FromStateDirectory_ConcurrentInitializationStressUsesOnePersistedSecret()
    {
        const string key = "fixture-key";
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var directory = SharedTestSupport.CreateTempDirectory();
            try
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async Task<string> InitializeAsync()
                {
                    await start.Task;
                    return ClaudeApiHoldFingerprint.FromStateDirectory(directory).Compute(key);
                }

                var initializers = Enumerable.Range(0, 8).Select(_ => Task.Run(InitializeAsync)).ToArray();
                start.SetResult();
                var identities = await Task.WhenAll(initializers);

                var identity = Assert.Single(identities.Distinct());
                var file = Assert.Single(Directory.GetFiles(directory));
                Assert.Equal(ClaudeApiHoldFingerprint.SecretFileName, Path.GetFileName(file));
                var secret = File.ReadAllBytes(file);
                Assert.Equal(32, secret.Length);
                Assert.Equal(ClaudeApiHoldFingerprint.FromSecret(secret).Compute(key), identity);
            }
            finally { SharedTestSupport.RemoveTempDirectory(directory); }
        }
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
