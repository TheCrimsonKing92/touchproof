using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class GitHubAppForgeIdentity(IForgeHttpTransport transport, IClock clock)
{
    public ForgeIdentityPublish Acquire(JsonElement settings)
    {
        var appId = RequiredId(settings, "appId");
        var installationId = RequiredId(settings, "installationId");
        var slug = RequiredString(settings, "slug");
        if (!Regex.IsMatch(slug, @"\A[a-zA-Z0-9]+(?:-[a-zA-Z0-9]+)*\z"))
            throw new ForgeIdentityUnavailableException("GitHub App slug is invalid");
        var keyPath = RequiredString(settings, "privateKeyPath");
        if (!Path.IsPathFullyQualified(keyPath) || !File.Exists(keyPath))
            throw new ForgeIdentityUnavailableException("GitHub App signing key not found at an absolute path");

        string signature;
        try
        {
            using var key = RSA.Create();
            key.ImportFromPem(File.ReadAllText(keyPath));
            var now = clock.UtcNow;
            var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = appId, iat = now.AddSeconds(-60).ToUnixTimeSeconds(), exp = now.AddMinutes(9).ToUnixTimeSeconds()
            }));
            var unsigned = header + "." + claims;
            signature = unsigned + "." + Base64Url(key.SignData(Encoding.ASCII.GetBytes(unsigned),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ForgeIdentityUnavailableException("GitHub App signing key could not be read");
        }

        ForgeHttpResponse response;
        try
        {
            response = transport.Post(new Uri($"https://api.github.com/app/installations/{installationId}/access_tokens"),
                new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer " + signature,
                    ["Accept"] = "application/vnd.github+json",
                    ["X-GitHub-Api-Version"] = "2022-11-28",
                    ["User-Agent"] = "mcg-agent-orchestrator"
                }, TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ForgeIdentityUnavailableException($"GitHub App access grant exchange failed: {exception.GetType().Name}");
        }
        if (response.StatusCode != 201)
            throw new ForgeIdentityUnavailableException($"GitHub App access grant exchange failed: HTTP {response.StatusCode}");

        string grant;
        try
        {
            using var body = JsonDocument.Parse(response.Body);
            grant = body.RootElement.GetProperty("token").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(grant) || grant.Any(char.IsWhiteSpace))
                throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ForgeIdentityUnavailableException("GitHub App access grant response had no usable grant");
        }
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + grant));
        return new(new Dictionary<string, string>
        {
            ["GH_TOKEN"] = grant,
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = "",
            ["GIT_CONFIG_KEY_1"] = "http.https://github.com/.extraheader",
            ["GIT_CONFIG_VALUE_1"] = "AUTHORIZATION: basic " + basic
        }, slug + "[bot]");
    }

    private static string RequiredString(JsonElement settings, string name)
    {
        if (!settings.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new ForgeIdentityUnavailableException($"GitHub App settings require {name}");
        return value.GetString()!;
    }

    private static long RequiredId(JsonElement settings, string name)
    {
        if (settings.TryGetProperty(name, out var value) &&
            long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) return id;
        throw new ForgeIdentityUnavailableException($"GitHub App settings require {name}");
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
