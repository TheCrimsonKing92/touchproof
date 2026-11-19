using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed partial class OwnerConsoleProviderConnectionsTab
{
    private readonly Dictionary<ProviderSignInTarget, bool> _deviceCode = [];
    private readonly Dictionary<string, SignInOperation> _activeSignIns = new(StringComparer.OrdinalIgnoreCase);

    private bool CanSignIn(ProfileState profile) => profile.Connection.SignIn is { } target &&
        !HasHostApiKey(profile.Connection.Report) &&
        !_activeSignIns.ContainsKey(profile.Id) &&
        !_activeSignIns.Values.Any(operation => SharesCredentialSource(target, operation.Target));

    private SignInOperation? AwaitingSignInReturn(ProfileState profile) =>
        _activeSignIns.GetValueOrDefault(profile.Id) is { Started: true, RecheckRequested: false } operation
            ? operation : null;

    private string? SignInText(ProfileState profile) =>
        _activeSignIns.GetValueOrDefault(profile.Id) is { ObservationFailed: true }
            ? $"{profile.InteractionText} Could not verify native exit. Sign-in is unavailable for this credential scope until the console is reopened.".TrimStart()
            : profile.InteractionText;

    private bool DeviceCodeAvailable(ProfileState profile) => profile.Connection.SignIn is { } target &&
        _deviceCode.GetValueOrDefault(target);

    private void ProbeDeviceCode()
    {
        if (Selected is not { } profile || HasHostApiKey(profile.Connection.Report) ||
            profile.Connection.SignIn is not { CanProbeDeviceCode: true } target ||
            !_deviceCode.TryAdd(target, false)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _launcher.SupportsDeviceCodeAsync(target, _lifetime)) Post(() => _deviceCode[target] = true);
            }
            catch (Exception) { /* Inconclusive capability probes leave the device-code option hidden. */ }
        });
    }

    private void BeginSignIn(ProfileState profile, bool deviceCode)
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
            _lifetime.ThrowIfCancellationRequested();
            var target = _connections.GetSignInTarget(profileId);
            launch = target is not null && target == operation.Target
                ? await _launcher.LaunchAsync(target, deviceCode, _lifetime)
                : new(null, ProviderSignInOutcome.Unsupported);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception) { launch = new(null, ProviderSignInOutcome.LaunchFailed); }

        using var session = launch.Session;
        if (session is null)
        {
            Post(() =>
            {
                if (!ReleaseSignIn(profileId, operation) || FindSignInProfile(profileId) is not { } profile) return;
                profile.InteractionText = launch.Failure switch
                {
                    ProviderSignInOutcome.ToolMissing => "The provider CLI could not be found.",
                    ProviderSignInOutcome.Unsupported => "This native sign-in option is unavailable.",
                    _ => "Could not open native sign-in."
                };
            });
            return;
        }
        Post(() =>
        {
            operation.Started = true;
            if (FindSignInProfile(profileId) is { } profile)
                profile.InteractionText = "Native sign-in is open. Press t after returning, or x to stop waiting.";
        });
        try
        {
            var exit = await session.WaitAsync(_lifetime);
            Post(() =>
            {
                if (!ReleaseSignIn(profileId, operation) || FindSignInProfile(profileId) is not { } profile) return;
                ReportSignInReturn(profile, operation, exit.Outcome switch
                {
                    ProviderSignInOutcome.Cancelled => "Native sign-in was cancelled.",
                    ProviderSignInOutcome.Succeeded => "The provider reported successful native sign-in.",
                    _ => $"Native sign-in exited ({exit.ExitCode}). Authentication outcome unknown."
                });
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            Post(() =>
            {
                operation.ObservationFailed = true;
                if (!operation.RecheckRequested && FindSignInProfile(profileId) is { } profile)
                    profile.InteractionText = "Press t after returning, or x to stop waiting.";
            });
        }
    }

    private bool ReleaseSignIn(string profileId, SignInOperation operation) =>
        _activeSignIns.GetValueOrDefault(profileId) == operation && _activeSignIns.Remove(profileId);

    private ProfileState? FindSignInProfile(string profileId) =>
        _profiles.Find(profile => StringComparer.OrdinalIgnoreCase.Equals(profile.Id, profileId));

    private void ReportSignInReturn(ProfileState profile, SignInOperation operation, string text)
    {
        profile.InteractionText = text;
        if (operation.RecheckRequested) return;
        operation.RecheckRequested = true;
        Check(profile);
    }

    private sealed class SignInOperation(ProviderSignInTarget target)
    {
        internal ProviderSignInTarget Target { get; } = target;
        internal bool Started { get; set; }
        internal bool RecheckRequested { get; set; }
        internal bool ObservationFailed { get; set; }
    }
}
