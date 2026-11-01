using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all files and RSA keys are test-owned; HTTP is always a fake.
public sealed class GitHubAppForgeIdentityTests
{
    private const string Grant = "ghs_testInstallationGrant";
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Acquire_AppSettings_SignsClaimsAndExchangesThroughTransport()
    {
        using var fixture = new IdentityFiles();
        var transport = new Transport(new(201, JsonSerializer.Serialize(new { token = Grant })));

        var identity = new ForgeIdentityProvider(transport, new Clock()).Acquire(fixture.Settings);

        Assert.Equal(1, transport.Calls);
        Assert.Equal("https://api.github.com/app/installations/177541698/access_tokens", transport.Uri!.AbsoluteUri);
        Assert.Equal("application/vnd.github+json", transport.Headers!["Accept"]);
        Assert.Equal("2022-11-28", transport.Headers["X-GitHub-Api-Version"]);
        Assert.Equal("mcg-agent-orchestrator", transport.Headers["User-Agent"]);
        Assert.StartsWith("Bearer ", transport.Headers["Authorization"]);
        var segments = transport.Headers["Authorization"][7..].Split('.');
        Assert.Equal(3, segments.Length);
        using var header = JsonDocument.Parse(Decode(segments[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        using var claims = JsonDocument.Parse(Decode(segments[1]));
        Assert.Equal(5567992, claims.RootElement.GetProperty("iss").GetInt64());
        Assert.Equal(Now.AddSeconds(-60).ToUnixTimeSeconds(), claims.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(Now.AddMinutes(9).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(fixture.Key.ExportParameters(false));
        var signed = Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]);
        Assert.True(publicKey.VerifyData(signed, Decode(segments[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        signed[0] ^= 1;
        Assert.False(publicKey.VerifyData(signed, Decode(segments[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        Assert.Equal("mcg-orchestrator[bot]", identity.Login);
        Assert.Equal(Grant, identity.Environment["GH_TOKEN"]);
        Assert.Equal("2", identity.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.https://github.com/.extraheader", identity.Environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("", identity.Environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("http.https://github.com/.extraheader", identity.Environment["GIT_CONFIG_KEY_1"]);
        var basic = "AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + Grant));
        Assert.Equal(basic, identity.Environment["GIT_CONFIG_VALUE_1"]);
        Assert.Equal(6, identity.Environment.Count);
        Assert.DoesNotContain(Grant, identity.ToString());
        Assert.DoesNotContain(basic, identity.ToString());
        Assert.DoesNotContain(Grant, new ForgeHttpResponse(201, Grant).ToString());
        Assert.Equal(2, Directory.GetFiles(fixture.Directory).Length);
    }

    [Theory]
    [InlineData("unknown", "unknown forge identity kind")]
    [InlineData("grant-file", "not implemented")]
    [InlineData("missing-key", "signing key not found")]
    [InlineData("invalid-key", "signing key could not be read")]
    [InlineData("missing-slug", "require slug")]
    public void Acquire_InvalidSettings_FailsWithoutExchangeOrSecretText(string failure, string message)
    {
        using var fixture = new IdentityFiles();
        if (failure is "unknown" or "grant-file") fixture.WriteSettings(failure);
        if (failure == "missing-key") File.Delete(fixture.KeyPath);
        if (failure == "invalid-key") File.WriteAllText(fixture.KeyPath, Grant);
        if (failure == "missing-slug") fixture.WriteSettings(slug: "");
        var transport = new Transport(new(201, JsonSerializer.Serialize(new { token = Grant })));

        var error = Assert.Throws<ForgeIdentityUnavailableException>(() =>
            new ForgeIdentityProvider(transport, new Clock()).Acquire(fixture.Settings));

        Assert.Contains(message, error.Message);
        Assert.DoesNotContain(Grant, error.ToString());
        Assert.DoesNotContain(fixture.Key.ExportRSAPrivateKeyPem(), error.ToString());
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(401, "HTTP 401")]
    [InlineData(500, "HTTP 500")]
    [InlineData(201, "no usable grant")]
    public void Acquire_FailedExchange_DiscardsResponseBody(int status, string message)
    {
        using var fixture = new IdentityFiles();
        var transport = new Transport(new(status, Grant + fixture.Key.ExportRSAPrivateKeyPem()));
        var error = Assert.Throws<ForgeIdentityUnavailableException>(() =>
            new ForgeIdentityProvider(transport, new Clock()).Acquire(fixture.Settings));
        Assert.Equal(1, transport.Calls);
        Assert.Contains(message, error.Message);
        Assert.DoesNotContain(Grant, error.ToString());
        Assert.DoesNotContain("PRIVATE KEY", error.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"token\":\"\"}")]
    [InlineData("{\"token\":null}")]
    public void Acquire_EmptyGrant_FailsWithSafeMessage(string body)
    {
        using var fixture = new IdentityFiles();
        var error = Assert.Throws<ForgeIdentityUnavailableException>(() =>
            new ForgeIdentityProvider(new Transport(new(201, body)), new Clock()).Acquire(fixture.Settings));
        Assert.Contains("no usable grant", error.Message);
    }

    [Fact]
    public void Acquire_TransportException_DiscardsExceptionMessage()
    {
        using var fixture = new IdentityFiles();
        var error = Assert.Throws<ForgeIdentityUnavailableException>(() =>
            new ForgeIdentityProvider(new ThrowingTransport(), new Clock()).Acquire(fixture.Settings));
        Assert.Contains("exchange failed: HttpRequestException", error.Message);
        Assert.DoesNotContain(Grant, error.ToString());
        Assert.Null(error.InnerException);
    }

    private static byte[] Decode(string value) => Convert.FromBase64String(
        value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class Transport(ForgeHttpResponse response) : IForgeHttpTransport
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public IReadOnlyDictionary<string, string>? Headers { get; private set; }
        public ForgeHttpResponse Post(Uri uri, IReadOnlyDictionary<string, string> headers, TimeSpan timeout)
        {
            Calls++;
            Uri = uri;
            Headers = new Dictionary<string, string>(headers);
            return response;
        }
    }

    private sealed class ThrowingTransport : IForgeHttpTransport
    {
        public ForgeHttpResponse Post(Uri uri, IReadOnlyDictionary<string, string> headers, TimeSpan timeout) =>
            throw new HttpRequestException(Grant);
    }

    private sealed class IdentityFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "forge-identity-" + Guid.NewGuid().ToString("N"));
        public RSA Key { get; } = RSA.Create(2048);
        public string KeyPath => Path.Combine(Directory, "key.pem");
        public string Settings => Path.Combine(Directory, "identity.json");
        public IdentityFiles()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(KeyPath, Key.ExportRSAPrivateKeyPem());
            WriteSettings();
        }
        public void WriteSettings(string kind = "github-app", string slug = "mcg-orchestrator") =>
            File.WriteAllText(Settings, JsonSerializer.Serialize(new
            {
                kind, appId = 5567992, installationId = 177541698, slug, privateKeyPath = KeyPath
            }));
        public void Dispose()
        {
            Key.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
