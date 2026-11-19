using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleProviderSignInFlow(
    IProviderConnectionsConsoleGateway connections,
    IProviderSignInLauncher launcher,
    Func<string, OwnerConsoleProviderConnectionState?> findProfile,
    Action<OwnerConsoleProviderConnectionState> check,
    Action<Action> post,
    CancellationToken lifetime)
{
    private readonly Dictionary<ProviderSignInTarget, bool> _deviceCode = [];
    private readonly Dictionary<string, SignInOperation> _activeSignIns = new(StringComparer.OrdinalIgnoreCase);

    internal bool CanSignIn(OwnerConsoleProviderConnectionState profile) => profile.Connection.SignIn is { } target &&
        !HasHostApiKey(profile.Connection.Report) &&
        !_activeSignIns.ContainsKey(profile.Id) &&
        !_activeSignIns.Values.Any(operation => SharesCredentialSource(target, operation.Target));

    private SignInOperation? AwaitingSignInReturn(OwnerConsoleProviderConnectionState profile) =>
        _activeSignIns.GetValueOrDefault(profile.Id) is { Started: true, RecheckRequested: false } operation
            ? operation : null;

    internal bool CanReportReturn(OwnerConsoleProviderConnectionState profile) => AwaitingSignInReturn(profile) is not null;

    internal bool HasActiveSignIn(OwnerConsoleProviderConnectionState profile) => _activeSignIns.ContainsKey(profile.Id);

    internal void ReportReturn(OwnerConsoleProviderConnectionState profile, bool stoppedWaiting)
    {
        if (AwaitingSignInReturn(profile) is not { } operation) return;
        ReportSignInReturn(profile, operation, stoppedWaiting
            ? "Stopped waiting for native sign-in. The native window may still be open."
            : "Returned from native sign-in. Authentication outcome unknown.");
    }

    internal string? StatusText(OwnerConsoleProviderConnectionState profile) =>
        _activeSignIns.GetValueOrDefault(profile.Id) is { ObservationFailed: true }
            ? $"{profile.InteractionText} Could not verify native exit. Sign-in is unavailable for this credential scope until the console is reopened.".TrimStart()
            : profile.InteractionText;

    internal bool DeviceCodeAvailable(OwnerConsoleProviderConnectionState profile) => profile.Connection.SignIn is { } target &&
        _deviceCode.GetValueOrDefault(target);

    internal void ProbeDeviceCode(OwnerConsoleProviderConnectionState? profile)
    {
        if (profile is null || HasHostApiKey(profile.Connection.Report) ||
            profile.Connection.SignIn is not { CanProbeDeviceCode: true } target ||
            !_deviceCode.TryAdd(target, false)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await launcher.SupportsDeviceCodeAsync(target, lifetime)) post(() => _deviceCode[target] = true);
            }
            catch (Exception) { /* Inconclusive capability probes leave the device-code option hidden. */ }
        });
    }

    internal void BeginSignIn(OwnerConsoleProviderConnectionState profile, bool deviceCode)
    {
        if (!CanSignIn(profile)) return;
        var operation = new SignInOperation(profile.Connection.SignIn!);
        _activeSignIns.Add(profile.Id, operation);
        profile.InteractionText = "Opening the provider's native sign-in…";
        _ = Task.Run(() => RunSignInAsync(profile.Id, operation, deviceCode));
    }

    private async Task RunSignInAsync(string profileId, SignInOperation operation, bool deviceCode)
    {
        ProviderSignInLaunch launch;
        try
        {
            lifetime.ThrowIfCancellationRequested();
            var target = connections.GetSignInTarget(profileId);
            launch = target is not null && target == operation.Target
                ? await launcher.LaunchAsync(target, deviceCode, lifetime)
                : new(null, ProviderSignInOutcome.Unsupported);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception) { launch = new(null, ProviderSignInOutcome.LaunchFailed); }

        using var session = launch.Session;
        if (session is null)
        {
            post(() =>
            {
                if (!ReleaseSignIn(profileId, operation) || findProfile(profileId) is not { } profile) return;
                profile.InteractionText = launch.Failure switch
                {
                    ProviderSignInOutcome.ToolMissing => "The provider CLI could not be found.",
                    ProviderSignInOutcome.Unsupported => "This native sign-in option is unavailable.",
                    _ => "Could not open native sign-in."
                };
            });
            return;
        }
        post(() =>
        {
            operation.Started = true;
            if (findProfile(profileId) is { } profile)
                profile.InteractionText = "Native sign-in is open. Press t after returning, or x to stop waiting.";
        });
        try
        {
            var exit = await session.WaitAsync(lifetime);
            post(() =>
            {
                if (!ReleaseSignIn(profileId, operation) || findProfile(profileId) is not { } profile) return;
                ReportSignInReturn(profile, operation, exit.Outcome switch
                {
                    ProviderSignInOutcome.Cancelled => "Native sign-in was cancelled.",
                    ProviderSignInOutcome.Succeeded => "The provider reported successful native sign-in.",
                    _ => $"Native sign-in exited ({exit.ExitCode}). Authentication outcome unknown."
                });
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            post(() =>
            {
                operation.ObservationFailed = true;
                if (!operation.RecheckRequested && findProfile(profileId) is { } profile)
                    profile.InteractionText = "Press t after returning, or x to stop waiting.";
            });
        }
    }

    private bool ReleaseSignIn(string profileId, SignInOperation operation) =>
        _activeSignIns.GetValueOrDefault(profileId) == operation && _activeSignIns.Remove(profileId);

    private void ReportSignInReturn(OwnerConsoleProviderConnectionState profile, SignInOperation operation, string text)
    {
        profile.InteractionText = text;
        if (operation.RecheckRequested) return;
        operation.RecheckRequested = true;
        check(profile);
    }

    private sealed class SignInOperation(ProviderSignInTarget target)
    {
        internal ProviderSignInTarget Target { get; } = target;
        internal bool Started { get; set; }
        internal bool RecheckRequested { get; set; }
        internal bool ObservationFailed { get; set; }
    }
}
