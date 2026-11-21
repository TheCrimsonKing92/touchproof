using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class ModelInventorySources(string workingDirectory, HttpClient anthropic)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    public Task<ModelListing> ListAsync(WorkerProfile profile, ModelDiscoverySource source, CancellationToken token) =>
        source switch
        {
            ModelDiscoverySource.CodexCache => ReadCodexAsync(
                Path.Combine(WorkerProviderHomeEnvironment.CodexSeedDirectory(), "models_cache.json"), token),
            ModelDiscoverySource.GrokModels => ListGrokAsync(profile, token),
            ModelDiscoverySource.AnthropicModels => ListAnthropicAsync(token),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };

    public async Task<ModelProbeResult> ProbeAsync(WorkerProfile profile, string model, CancellationToken token)
    {
        if (WorkerProviderCatalog.Default().ResolveProfile(profile.Name).Identity.Kind != ProviderKind.AnthropicClaudeCli)
            return new(ModelProbeStatus.Failed, "Probe unsupported for this profile");
        if (!ProbeModelId().IsMatch(model))
            return new(ModelProbeStatus.Failed, "Invalid Claude model identifier");

        var result = await RunAsync(profile, ["-p", "--model", model, "Reply with exactly: READY"], token);
        var failure = ProcessFailure(result);
        return failure is null && result.StandardOutput.Trim().Equals("READY", StringComparison.Ordinal)
            ? new(ModelProbeStatus.Succeeded)
            : new(ModelProbeStatus.Failed, failure ?? "Expected an exact READY reply");
    }

    public static async Task<ModelListing> ReadCodexAsync(string path, CancellationToken token = default)
    {
        try
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            var root = json.RootElement;
            var timestamp = root.TryGetProperty("fetched_at", out var fetchedAt) &&
                fetchedAt.ValueKind == JsonValueKind.String && fetchedAt.TryGetDateTimeOffset(out var value)
                ? (DateTimeOffset?)value : null;
            return new(ModelSourceStatus.Available, ReadIds(root, "models", "slug", "id", "model"), timestamp);
        }
        catch (FileNotFoundException) { return new(ModelSourceStatus.NotConfigured, [], Detail: "Codex cache missing"); }
        catch (DirectoryNotFoundException) { return new(ModelSourceStatus.NotConfigured, [], Detail: "Codex cache missing"); }
    }

    public static ModelListing ParseGrok(WorkerProcessRunResult result)
    {
        if (ProcessFailure(result) is { } failure)
            return new(IsUnauthenticated(result) ? ModelSourceStatus.Unauthenticated : ModelSourceStatus.Unavailable,
                [], Detail: failure);

        var models = GrokModelRow().Matches(result.StandardOutput)
            .Select(match => match.Groups["id"].Value).ToArray();
        return models.Length == 0
            ? new(ModelSourceStatus.Unavailable, [], Detail: "Grok output contained no recognized model rows")
            : new(ModelSourceStatus.Available, models);
    }

    public static ModelListing ParseAnthropic(string json, out string? nextCursor)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var ids = ReadIds(root, "data", "id");
        nextCursor = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
            ? ReadString(root, "last_id") ?? throw new JsonException("Missing Anthropic pagination cursor")
            : null;
        return new(ModelSourceStatus.Available, ids);
    }

    private async Task<ModelListing> ListGrokAsync(WorkerProfile profile, CancellationToken token) =>
        ParseGrok(await RunAsync(profile, ["models"], token));

    private async Task<ModelListing> ListAnthropicAsync(CancellationToken token)
    {
        var key = Environment.GetEnvironmentVariable(ClaudeCredentialSource.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
            return new(ModelSourceStatus.NotConfigured, [], Detail: "Anthropic API key missing");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Deadline);
        var models = new List<string>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var path = cursor is null ? "v1/models" : $"v1/models?after_id={Uri.EscapeDataString(cursor)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
            using var response = await anthropic.SendAsync(request, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(ModelSourceStatus.Unauthenticated, [], Detail: $"HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                return new(ModelSourceStatus.Unavailable, [], Detail: $"HTTP {(int)response.StatusCode}");
            var page = ParseAnthropic(await response.Content.ReadAsStringAsync(deadline.Token), out cursor);
            models.AddRange(page.Models);
            if (cursor is not null && !cursors.Add(cursor))
                throw new JsonException("Repeated Anthropic pagination cursor");
        } while (cursor is not null);
        return new(ModelSourceStatus.Available, models);
    }

    private Task<WorkerProcessRunResult> RunAsync(WorkerProfile profile, string[] arguments, CancellationToken token)
    {
        var executable = WorkerProfileDiagnostics.ExtractExecutable(profile.CommandTemplate);
        var kind = WorkerProviderCatalog.Default().ResolveProfile(profile.Name).Identity.Kind;
        WorkerProcessDirectRunRequest request;
        if (OperatingSystem.IsWindows())
        {
            // Invoke application shims through the same shell as worker profiles.
            var command = "& " + string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)) +
                "; if ($null -eq $LASTEXITCODE) { exit 1 }; exit $LASTEXITCODE";
            request = new(WorkerShell.Executable, [.. WorkerShell.BaseArguments(), command],
                workingDirectory, Deadline, RequireOwnedProcessGroup: true);
        }
        else
        {
            request = new(executable, arguments, workingDirectory, Deadline, RequireOwnedProcessGroup: true);
        }
        return WorkerProcessRunner.RunBoundedDirectAsync(request, 64 * 1024, token,
            TimeProvider.System, null, null, OperatingSystem.IsWindows()
                ? start => WorkerProviderHomeEnvironment.ApplyDesktopCredentialSource(start.Environment, kind)
                : null);
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "''").Replace("\u2018", "\u2018\u2018").Replace("\u2019", "\u2019\u2019") + "'";

    private static string? ProcessFailure(WorkerProcessRunResult result) =>
        result.TimedOut ? "Timed out" :
        result.CaptureLimited ? "Output limit reached" :
        IsUnauthenticated(result) ? "Authentication required" :
        result.ExitCode != 0 ? $"Tool exited with code {result.ExitCode}" : null;

    private static bool IsUnauthenticated(WorkerProcessRunResult result) =>
        (result.StandardOutput + "\n" + result.StandardError)
            .Contains("not authenticated", StringComparison.OrdinalIgnoreCase);

    private static string[] ReadIds(JsonElement root, string array, params string[] keys)
    {
        if (!root.TryGetProperty(array, out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Missing '{array}' model array");
        var ids = rows.EnumerateArray().Select(row => row.ValueKind switch
        {
            JsonValueKind.String => row.GetString(),
            JsonValueKind.Object => keys.Select(key => ReadString(row, key)).FirstOrDefault(id => id is not null),
            _ => null
        }).OfType<string>().Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (ids.Length == 0 && rows.GetArrayLength() != 0)
            throw new JsonException($"Unrecognized '{array}' model entries");
        return ids;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    [GeneratedRegex(@"^[\t │|*•-]*(?<id>grok[-.][A-Za-z0-9._:/-]+)(?=[\s│|]|$)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GrokModelRow();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:/@+-]*\z")]
    private static partial Regex ProbeModelId();
}
