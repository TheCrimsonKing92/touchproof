using System.ComponentModel;
using System.Globalization;
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

        var executable = WorkerProfileDiagnostics.ExtractExecutable(profile.CommandTemplate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (cli is null || !(executable.Equals(cli, comparison) || OperatingSystem.IsWindows() &&
            (executable.Equals(cli + ".exe", comparison) || executable.Equals(cli + ".cmd", comparison))) ||
            !MatchesHostLauncher(profile.CommandTemplate, executable, cli, comparison))
            return Observation(ProviderConnectionResult.Unsupported);
        try
        {
            string[] arguments = cli == "codex" ? ["login", "status"] : ["auth", "status", "--json"];
            var resolveInShell = OperatingSystem.IsWindows() && locate is null;
            WorkerProcessDirectRunRequest request;
            if (resolveInShell)
            {
                request = CreateWindowsProbeRequest(executable, arguments, workingDirectory);
            }
            else
            {
                var path = (locate ?? LocateOnPath)(executable);
                if (path is null) return Observation(ProviderConnectionResult.ToolMissing);
                request = CreateRequest(path, arguments, workingDirectory);
            }
            var result = run is null
                ? await RunAsync(request, kind, cancellationToken)
                : await run(request, cancellationToken);
            if (result.TimedOut) return Observation(ProviderConnectionResult.TimedOut);
            if (result.CaptureLimited) return Observation(ProviderConnectionResult.OutputLimited);
            var parsed = resolveInShell ? ParseWindowsProbe(cli, result) : Parse(cli, result);
            return Observation(parsed.Result, parsed.Mode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            return Observation(ProviderConnectionResult.ToolMissing);
        }
    }

    internal static bool MatchesHostLauncher(string template, string executable, string cli, StringComparison comparison) =>
        MatchesNativeLauncher(template, executable, cli, comparison, allowGrokTurnLimit: false);

    internal static bool MatchesNativeSignInLauncher(string template, string executable, string cli, StringComparison comparison) =>
        MatchesNativeLauncher(template, executable, cli, comparison, allowGrokTurnLimit: cli == "grok");

    private static bool MatchesNativeLauncher(string template, string executable, string cli, StringComparison comparison,
        bool allowGrokTurnLimit)
    {
        // Executable extraction skips shell setup. Only accept native commands whose full
        // template establishes the same launcher and credential environment as the host.
        var command = template.AsSpan().Trim();
        if (!command.StartsWith(executable, comparison)) return false;
        var arguments = command[executable.Length..].TrimStart(" \t");
        var defaultTemplate = WorkerProfileCatalog.Default().GetRequired(cli + "-cli").CommandTemplate;
        var defaultArguments = defaultTemplate.AsSpan(cli.Length + 1);
        return arguments.IsEmpty ||
            (cli == "codex" && arguments.SequenceEqual("exec")) ||
            (cli == "claude" && arguments.SequenceEqual("-p")) ||
            arguments.SequenceEqual(defaultArguments) ||
            (allowGrokTurnLimit && MatchesGrokTurnLimit(arguments, defaultArguments));
    }

    private static bool MatchesGrokTurnLimit(ReadOnlySpan<char> arguments, ReadOnlySpan<char> defaultArguments)
    {
        // The catalog ends with the turn limit. Every character before its value must match.
        var limitStart = defaultArguments.LastIndexOf(' ') + 1;
        var prefix = defaultArguments[..limitStart];
        if (!prefix.EndsWith(" --max-turns ", StringComparison.Ordinal) ||
            !arguments.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var limit = arguments[limitStart..];
        // Grok accepts positive unsigned 32-bit turn counts.
        return !limit.IsEmpty && !limit.ContainsAnyExceptInRange('0', '9') &&
            uint.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var maxTurns) && maxTurns > 0;
    }

    private static Task<WorkerProcessRunResult> RunAsync(WorkerProcessDirectRunRequest request,
        ProviderKind kind, CancellationToken token) =>
        WorkerProcessRunner.RunBoundedDirectAsync(request, MaximumCaptureBytes, token, TimeProvider.System, null, null,
            OperatingSystem.IsWindows() ? start => WorkerProviderHomeEnvironment.ApplyDesktopCredentialSource(start.Environment, kind) : null);

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

    private static WorkerProcessDirectRunRequest CreateWindowsProbeRequest(string executable, string[] arguments, string directory)
    {
        // Resolve as dispatch does, then invoke that exact application within the same bounded shell.
        // Disable module autoload only during discovery. The resolved script uses the normal shell settings.
        var command = $$"""
            try {
                $launcher = & {
                    $PSModuleAutoLoadingPreference = 'None'
                    Get-Command -Name '{{executable}}' -ErrorAction Stop
                }
            }
            catch [System.Management.Automation.CommandNotFoundException] {
                [Console]::WriteLine('missing')
                exit 0
            }
            if (-not ({{WindowsNativeLauncherCondition(executable)}})) {
                [Console]::WriteLine('unsupported')
                exit 0
            }
            [Console]::WriteLine('application')
            & $launcher {{string.Join(' ', arguments)}}
            exit $LASTEXITCODE
            """;
        return new(WorkerShell.Executable, [.. WorkerShell.BaseArguments(), command], directory, Deadline,
            RequireOwnedProcessGroup: true);
    }

    // npm's installed Codex command can resolve to its PowerShell shim. Explicit script
    // templates are still outside the host probe's closed launcher contract.
    internal static string WindowsNativeLauncherCondition(string executable)
    {
        const string application = "($launcher.CommandType -eq 'Application' -and [IO.Path]::GetExtension($launcher.Path) -in @('.exe', '.cmd'))";
        return executable.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? application + " -or ($launcher.CommandType -eq 'ExternalScript' -and [IO.Path]::GetFileName($launcher.Path) -eq 'codex.ps1')"
            : application;
    }

    private static (ProviderConnectionResult Result, ProviderConnectionAuthMode Mode) ParseWindowsProbe(string cli, WorkerProcessRunResult response)
    {
        // The shell writes the resolution header before any application output.
        var newline = response.StandardOutput.IndexOf('\n');
        if (newline < 0) return (ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown);
        return response.StandardOutput[..newline].TrimEnd('\r') switch
        {
            "missing" => (ProviderConnectionResult.ToolMissing, ProviderConnectionAuthMode.Unknown),
            "unsupported" => (ProviderConnectionResult.Unsupported, ProviderConnectionAuthMode.Unknown),
            "application" => Parse(cli, response with { StandardOutput = response.StandardOutput[(newline + 1)..] }),
            _ => (ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown)
        };
    }

    private static string? LocateOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }

    internal static (ProviderConnectionResult Result, ProviderConnectionAuthMode Mode) Parse(string cli, WorkerProcessRunResult response)
    {
        var output = response.StandardOutput.Trim();
        var error = response.StandardError.Trim();
        bool Exact(string text) => output.Equals(text, StringComparison.OrdinalIgnoreCase) || error.Equals(text, StringComparison.OrdinalIgnoreCase);
        if (response.ExitCode != 0 && (ReportsUnsupportedCommand(cli, error) || ReportsUnsupportedCommand(cli, output)))
            return (ProviderConnectionResult.Unsupported, ProviderConnectionAuthMode.Unknown);
        if (cli == "codex")
        {
            var oauth = Exact("Logged in using ChatGPT");
            var apiKey = Exact("Logged in using an API key") || Exact("Logged in using API key");
            var signedIn = oauth || apiKey;
            var keyConfigured = ReportsCodexApiKeyConfiguration(output) || ReportsCodexApiKeyConfiguration(error);
            var signedOut = Exact("Not logged in");
            var expired = Exact("Credentials expired") || Exact("Token expired");
            if ((signedIn ? 1 : 0) + (keyConfigured ? 1 : 0) + (signedOut ? 1 : 0) + (expired ? 1 : 0) > 1)
                return (ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown);
            if (response.ExitCode == 0 && keyConfigured)
                return (ProviderConnectionResult.MaterialPresent, ProviderConnectionAuthMode.ApiKey);
            if (response.ExitCode == 0 && signedIn)
                return (ProviderConnectionResult.SignedIn, (oauth, apiKey) switch
                {
                    (true, false) => ProviderConnectionAuthMode.OAuth,
                    (false, true) => ProviderConnectionAuthMode.ApiKey,
                    _ => ProviderConnectionAuthMode.Unknown
                });
            if (signedOut) return (ProviderConnectionResult.SignedOut, ProviderConnectionAuthMode.Unknown);
            if (expired) return (ProviderConnectionResult.Expired, ProviderConnectionAuthMode.Unknown);
        }
        if (cli == "claude") return ParseClaude(output, response.ExitCode);
        return (ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown);
    }

    private static bool ReportsUnsupportedCommand(string cli, ReadOnlySpan<char> diagnostic)
    {
        var newline = diagnostic.IndexOf('\n');
        var firstLine = newline < 0 ? diagnostic : diagnostic[..newline].TrimEnd();
        var rejectsStatus = firstLine.Equals("error: unrecognized subcommand 'status'", StringComparison.OrdinalIgnoreCase) ||
            firstLine.Equals("error: unknown command 'status'", StringComparison.OrdinalIgnoreCase);
        var rejectsAuth = cli == "claude" &&
            (firstLine.Equals("error: unknown command 'auth'", StringComparison.OrdinalIgnoreCase) ||
             firstLine.Equals("error: unrecognized subcommand 'auth'", StringComparison.OrdinalIgnoreCase));
        return (rejectsStatus || rejectsAuth) && (newline < 0 ||
            diagnostic[(newline + 1)..].TrimStart().StartsWith("Usage:", StringComparison.OrdinalIgnoreCase));
    }

    private static (ProviderConnectionResult Result, ProviderConnectionAuthMode Mode) ParseClaude(string output, int exitCode)
    {
        var unknown = (ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown);
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return unknown;

            bool? signedIn = null;
            string? failure = null;
            var failureSeen = false;
            ProviderConnectionAuthMode? mode = null;
            foreach (var field in doc.RootElement.EnumerateObject())
            {
                switch (field.Name)
                {
                    case "loggedIn":
                        if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unknown;
                        var reportedSignIn = field.Value.GetBoolean();
                        if (signedIn is not null && signedIn != reportedSignIn) return unknown;
                        signedIn = reportedSignIn;
                        break;
                    case "error":
                        var reportedFailure = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null;
                        if (failureSeen && failure != reportedFailure) return unknown;
                        failureSeen = true;
                        failure = reportedFailure;
                        break;
                    case "authMethod":
                        var reportedMode = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() switch
                        {
                            "claude.ai" or "oauth" => ProviderConnectionAuthMode.OAuth,
                            "api_key" or "apiKey" => ProviderConnectionAuthMode.ApiKey,
                            _ => ProviderConnectionAuthMode.Unknown
                        } : ProviderConnectionAuthMode.Unknown;
                        mode = mode is null || mode == reportedMode ? reportedMode : ProviderConnectionAuthMode.Unknown;
                        break;
                }
            }
            if (failure is "token_expired" or "credentials_expired")
                return signedIn == true ? unknown : (ProviderConnectionResult.Expired, ProviderConnectionAuthMode.Unknown);
            return signedIn switch
            {
                false => (ProviderConnectionResult.SignedOut, mode ?? ProviderConnectionAuthMode.Unknown),
                true when exitCode == 0 => (ProviderConnectionResult.SignedIn, mode ?? ProviderConnectionAuthMode.Unknown),
                _ => unknown
            };
        }
        catch (JsonException)
        {
            return unknown;
        }
    }

    private static bool ReportsCodexApiKeyConfiguration(ReadOnlySpan<char> status)
    {
        const string prefix = "API key configured - ";
        var newline = status.IndexOf('\n');
        if (newline < 0 || !status[(newline + 1)..].SequenceEqual("Authentication is checked when you send a request."))
            return false;

        var line = status[..newline].TrimEnd('\r');
        // Match the released status format and discard the masked key.
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var maskedKey = line[prefix.Length..];
        return maskedKey.SequenceEqual("***") || (maskedKey.Length == 16 && maskedKey[8..11].SequenceEqual("***"));
    }
}
