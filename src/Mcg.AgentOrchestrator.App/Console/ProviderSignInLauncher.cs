using System.Diagnostics;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record ProviderSignInTarget(ProviderKind Kind, string Executable)
{
    internal bool CanProbeDeviceCode => Kind == ProviderKind.XaiGrokCli;

    internal static ProviderSignInTarget? ForProfile(WorkerProfile profile)
    {
        var kind = WorkerProviderCatalog.Default().ResolveProfile(profile.Name).Identity.Kind;
        var cli = CliName(kind);
        var executable = WorkerProfileDiagnostics.ExtractExecutable(profile.CommandTemplate);
        return cli is not null && IsNativeName(executable, cli) &&
            ProviderConnectionStatusAdapters.MatchesNativeSignInLauncher(profile.CommandTemplate, executable, cli, StringComparison.OrdinalIgnoreCase)
            ? new(kind, executable.ToLowerInvariant())
            : null;
    }

    internal bool IsNative => CliName(Kind) is { } cli && IsNativeName(Executable, cli);

    private static string? CliName(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna => "codex",
        ProviderKind.AnthropicClaudeCli => "claude",
        ProviderKind.XaiGrokCli => "grok",
        _ => null
    };

    private static bool IsNativeName(string executable, string cli) =>
        executable.Equals(cli, StringComparison.OrdinalIgnoreCase) ||
        executable.Equals(cli + ".exe", StringComparison.OrdinalIgnoreCase) ||
        executable.Equals(cli + ".cmd", StringComparison.OrdinalIgnoreCase);
}

internal enum ProviderSignInOutcome { Unknown, Cancelled, Succeeded, ToolMissing, Unsupported, LaunchFailed }

internal sealed record ProviderSignInExit(ProviderSignInOutcome Outcome, int ExitCode)
{
    // The provider contracts used here do not document an authentication result for ordinary exits.
    internal static ProviderSignInExit FromExitCode(int code) =>
        new(code == unchecked((int)0xc000013a) ? ProviderSignInOutcome.Cancelled : ProviderSignInOutcome.Unknown, code);
}

internal interface IProviderSignInSession : IDisposable
{
    Task<ProviderSignInExit> WaitAsync(CancellationToken token);
}

internal sealed record ProviderSignInLaunch(IProviderSignInSession? Session, ProviderSignInOutcome? Failure = null);

internal interface IProviderSignInLauncher
{
    Task<bool> SupportsDeviceCodeAsync(ProviderSignInTarget target, CancellationToken token);
    Task<ProviderSignInLaunch> LaunchAsync(ProviderSignInTarget target, bool deviceCode, CancellationToken token,
        Func<bool>? tryCommitLaunch = null);
}

internal sealed class ProviderSignInLauncher(
    string workingDirectory,
    Func<WorkerProcessDirectRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? probe = null,
    Func<ProcessStartInfo, IProviderSignInSession>? start = null) : IProviderSignInLauncher
{
    private const uint CreateNewConsole = 0x00000010;
    private static readonly Regex DeviceCodeOption = new(
        @"(?m)^[ \t]*(?:-[a-zA-Z],[ \t]*)?--device-code(?=[ \t=,\r\n]|$)", RegexOptions.CultureInvariant);

    public async Task<bool> SupportsDeviceCodeAsync(ProviderSignInTarget target, CancellationToken token)
    {
        if (!target.IsNative || !target.CanProbeDeviceCode || !OperatingSystem.IsWindows()) return false;
        var (path, _) = await ResolveAsync(target, token);
        return path is not null && await AdvertisesDeviceCodeAsync(path, token);
    }

    public async Task<ProviderSignInLaunch> LaunchAsync(ProviderSignInTarget target, bool deviceCode, CancellationToken token,
        Func<bool>? tryCommitLaunch = null)
    {
        if (!target.IsNative || !OperatingSystem.IsWindows() || deviceCode && !target.CanProbeDeviceCode)
            return new(null, ProviderSignInOutcome.Unsupported);
        try
        {
            var (path, failure) = await ResolveAsync(target, token);
            if (path is null) return new(null, failure);
            if (deviceCode && !await AdvertisesDeviceCodeAsync(path, token))
                return new(null, ProviderSignInOutcome.Unsupported);
            var info = BuildStartInfo(path, target.Kind, workingDirectory, deviceCode);
            token.ThrowIfCancellationRequested();
            if (tryCommitLaunch?.Invoke() == false) return new(null, ProviderSignInOutcome.Cancelled);
            return new((start ?? StartInNewConsole)(info));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(null, ProviderSignInOutcome.LaunchFailed); }
    }

    private async Task<(string? Path, ProviderSignInOutcome Failure)> ResolveAsync(ProviderSignInTarget target, CancellationToken token)
    {
        // Resolve the same closed command token as the worker shell. Never execute the profile template.
        var command = $$"""
            $PSModuleAutoLoadingPreference = 'None'
            try { $launcher = Get-Command -Name '{{target.Executable.ToLowerInvariant()}}' -ErrorAction Stop }
            catch [System.Management.Automation.CommandNotFoundException] {
                [Console]::WriteLine('missing')
                exit 0
            }
            if ({{ProviderConnectionStatusAdapters.WindowsNativeLauncherCondition(target.Executable)}}) {
                [Console]::WriteLine($launcher.Path)
            } else {
                [Console]::WriteLine('unsupported')
            }
            """;
        var result = await RunProbeAsync(WorkerShell.Executable, [.. WorkerShell.BaseArguments(), command], token);
        if (result.TimedOut || result.CaptureLimited || result.ExitCode != 0) return (null, ProviderSignInOutcome.LaunchFailed);
        var path = result.StandardOutput.TrimEnd('\r', '\n');
        if (path == "missing") return (null, ProviderSignInOutcome.ToolMissing);
        var extension = Path.GetExtension(path);
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl) ||
            !(extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
              extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
              (target.Executable.Equals("codex", StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(path).Equals("codex.ps1", StringComparison.OrdinalIgnoreCase))))
            return (null, ProviderSignInOutcome.Unsupported);
        return (path, ProviderSignInOutcome.Unknown);
    }

    private async Task<bool> AdvertisesDeviceCodeAsync(string path, CancellationToken token)
    {
        var (file, args) = Command(path, ["login", "--help"], interactive: false);
        var result = await RunProbeAsync(file, args, token);
        return !result.TimedOut && !result.CaptureLimited && result.ExitCode == 0 &&
            (DeviceCodeOption.IsMatch(result.StandardOutput) || DeviceCodeOption.IsMatch(result.StandardError));
    }

    private Task<WorkerProcessRunResult> RunProbeAsync(string file, IReadOnlyList<string> args, CancellationToken token) =>
        (probe ?? RunBoundedProbeAsync)(
            new(file, args, workingDirectory, ProviderConnectionStatusAdapters.Deadline, RequireOwnedProcessGroup: true), token);

    private static Task<WorkerProcessRunResult> RunBoundedProbeAsync(WorkerProcessDirectRunRequest request, CancellationToken token) =>
        WorkerProcessRunner.RunBoundedDirectAsync(request, ProviderConnectionStatusAdapters.MaximumCaptureBytes, token);

    internal static ProcessStartInfo BuildStartInfo(string path, ProviderKind kind, string directory, bool deviceCode)
    {
        string[] nativeArgs = kind switch
        {
            ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna => ["login"],
            ProviderKind.AnthropicClaudeCli => ["auth", "login"],
            ProviderKind.XaiGrokCli when deviceCode => ["login", "--device-code"],
            ProviderKind.XaiGrokCli => ["login"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var (file, args) = Command(path, nativeArgs, interactive: true);
        var info = new ProcessStartInfo(file) { WorkingDirectory = directory };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        WorkerProviderHomeEnvironment.ApplyDesktopCredentialSource(info.Environment, kind);
        return info;
    }

    private static (string File, IReadOnlyList<string> Args) Command(string path, string[] args, bool interactive)
    {
        if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return (path, args);
        var escapedPath = path.Replace("'", "''");
        var command = $"& '{escapedPath}' {string.Join(' ', args)}; exit $LASTEXITCODE";
        return (WorkerShell.Executable, interactive
            ? ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command]
            : [.. WorkerShell.BaseArguments(), command]);
    }

    private static IProviderSignInSession StartInNewConsole(ProcessStartInfo info) =>
        new NativeSession(HandleListProcessLauncher.Start(info, creationFlags: CreateNewConsole));

    private sealed class NativeSession(HandleListLaunchedProcess process) : IProviderSignInSession
    {
        public async Task<ProviderSignInExit> WaitAsync(CancellationToken token)
        {
            await process.WaitForExitAsync(token);
            return ProviderSignInExit.FromExitCode(process.ExitCode);
        }

        public void Dispose() => process.Dispose();
    }
}
