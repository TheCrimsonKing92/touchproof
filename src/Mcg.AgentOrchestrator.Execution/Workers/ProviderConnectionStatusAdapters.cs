using System.ComponentModel;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProviderConnectionStatusAdapters(
    string workingDirectory,
    Func<WorkerProcessDirectRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? run = null,
    Func<string, string?>? locate = null,
    TimeProvider? time = null)
{
    public const int MaximumCaptureBytes = 64 * 1024;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public static string Provider(WorkerProfile profile) => WorkerProviderCatalog.Default().ResolveProfile(profile.Name).ProviderName;

    public async Task<ProviderConnectionObservation> CheckAsync(WorkerProfile profile, CancellationToken cancellationToken = default)
    {
        var kind = WorkerProviderCatalog.Default().ResolveProfile(profile.Name).Identity.Kind;
        var cli = kind switch
        {
            ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna => "codex",
            ProviderKind.AnthropicClaudeCli => "claude",
            _ => null
        };
        var source = cli == "codex" ? ProviderConnectionSource.CodexLoginStatus :
            cli == "claude" ? ProviderConnectionSource.ClaudeAuthStatus : ProviderConnectionSource.None;
        ProviderConnectionObservation Observation(ProviderConnectionResult result,
            ProviderConnectionAuthMode mode = ProviderConnectionAuthMode.Unknown) =>
            new(ProviderConnectionContext.Host, source, result, mode, (time ?? TimeProvider.System).GetUtcNow());

        // A recognized name with a custom launcher is not permission to execute its template.
        if (cli is null || !string.Equals(Path.GetFileNameWithoutExtension(
            WorkerProfileDiagnostics.ExtractExecutable(profile.CommandTemplate)), cli, StringComparison.OrdinalIgnoreCase))
            return Observation(ProviderConnectionResult.Unsupported);
        try
        {
            var path = (locate ?? Locate)(cli);
            if (path is null) return Observation(ProviderConnectionResult.ToolMissing);
            string[] arguments = cli == "codex" ? ["login", "status"] : ["auth", "status", "--json"];
            var request = CreateRequest(path, arguments, workingDirectory);
            var result = await (run ?? RunAsync)(request, cancellationToken);
            if (result.TimedOut) return Observation(ProviderConnectionResult.TimedOut);
            if (result.CaptureLimited) return Observation(ProviderConnectionResult.OutputLimited);
            var parsed = Parse(cli, result);
            return Observation(parsed.Result, parsed.Mode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            return Observation(ProviderConnectionResult.ToolMissing);
        }
    }

    private static Task<WorkerProcessRunResult> RunAsync(WorkerProcessDirectRunRequest request, CancellationToken token) =>
        WorkerProcessRunner.RunBoundedDirectAsync(request, MaximumCaptureBytes, token);

    internal static WorkerProcessDirectRunRequest CreateRequest(string path, string[] arguments, string directory)
    {
        if (OperatingSystem.IsWindows() && Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // Only a discovered application path and fixed literal status arguments enter this script.
            var command = "& '" + path.Replace("'", "''") + "' " + string.Join(' ', arguments) + "; exit $LASTEXITCODE";
            return new(WorkerShell.Executable, [.. WorkerShell.BaseArguments(), command], directory, Deadline,
                RequireOwnedProcessGroup: true);
        }
        return new(path, arguments, directory, Deadline, RequireOwnedProcessGroup: true);
    }

    private static string? Locate(string cli)
    {
        var names = OperatingSystem.IsWindows() ? new[] { cli + ".exe", cli + ".cmd" } : [cli];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var path = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        }
        return null;
    }

    internal static (ProviderConnectionResult Result, ProviderConnectionAuthMode Mode) Parse(string cli, WorkerProcessRunResult response)
    {
        var output = response.StandardOutput.Trim();
        var error = response.StandardError.Trim();
        bool Exact(string text) => output.Equals(text, StringComparison.OrdinalIgnoreCase) || error.Equals(text, StringComparison.OrdinalIgnoreCase);
        if (Exact("error: unrecognized subcommand 'status'") || Exact("error: unknown command 'status'"))
            return (ProviderConnectionResult.Unsupported, ProviderConnectionAuthMode.Unknown);
        if (cli == "codex")
        {
            var signedIn = Exact("Logged in using ChatGPT") || Exact("Logged in using an API key") || Exact("Logged in using API key");
            var signedOut = Exact("Not logged in");
            var expired = Exact("Credentials expired") || Exact("Token expired");
            if ((signedIn ? 1 : 0) + (signedOut ? 1 : 0) + (expired ? 1 : 0) > 1)
                return (ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown);
            if (response.ExitCode == 0 && Exact("Logged in using ChatGPT"))
                return (ProviderConnectionResult.SignedIn, ProviderConnectionAuthMode.OAuth);
            if (response.ExitCode == 0 && (Exact("Logged in using an API key") || Exact("Logged in using API key")))
                return (ProviderConnectionResult.SignedIn, ProviderConnectionAuthMode.ApiKey);
            if (Exact("Not logged in")) return (ProviderConnectionResult.SignedOut, ProviderConnectionAuthMode.Unknown);
            if (Exact("Credentials expired") || Exact("Token expired"))
                return (ProviderConnectionResult.Expired, ProviderConnectionAuthMode.Unknown);
        }
        if (cli == "claude")
        {
            try
            {
                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return (ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown);
                if (root.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.String &&
                    failure.GetString() is "token_expired" or "credentials_expired")
                    return (root.TryGetProperty("loggedIn", out var conflicting) && conflicting.ValueKind == JsonValueKind.True
                        ? ProviderConnectionResult.Unknown : ProviderConnectionResult.Expired, ProviderConnectionAuthMode.Unknown);
                if (root.TryGetProperty("loggedIn", out var signedIn) && signedIn.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    var mode = root.TryGetProperty("authMethod", out var method) && method.ValueKind == JsonValueKind.String
                        ? method.GetString() switch { "claude.ai" or "oauth" => ProviderConnectionAuthMode.OAuth,
                            "api_key" or "apiKey" => ProviderConnectionAuthMode.ApiKey, _ => ProviderConnectionAuthMode.Unknown }
                        : ProviderConnectionAuthMode.Unknown;
                    if (!signedIn.GetBoolean()) return (ProviderConnectionResult.SignedOut, mode);
                    if (response.ExitCode == 0) return (ProviderConnectionResult.SignedIn, mode);
                }
            }
            catch (JsonException) { }
        }
        return (ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown);
    }
}
