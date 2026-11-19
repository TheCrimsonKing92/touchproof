using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed partial class OwnerConsoleProviderConnectionsTab
{
    private readonly Dictionary<ProviderSignInTarget, bool> _deviceCode = [];

    private bool CanSignIn(ProfileState profile) => profile.Connection.SignIn is { } target &&
        !HasHostApiKey(profile.Connection.Report) &&
        !_profiles.Any(p => p.SignIn is { } operation && SharesCredentialSource(target, operation.Target));

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
        profile.SignIn = operation;
        profile.InteractionText = "Opening the provider's native sign-in…";
        _ = Task.Run(() => RunSignInAsync(profile, operation, deviceCode));
    }

    private async Task RunSignInAsync(ProfileState profile, SignInOperation operation, bool deviceCode)
    {
        ProviderSignInLaunch launch;
        try
        {
            _lifetime.ThrowIfCancellationRequested();
            var target = _connections.GetSignInTarget(profile.Id);
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
                if (profile.SignIn != operation) return;
                profile.SignIn = null;
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
            profile.InteractionText = "Native sign-in is open. Press t after returning, or x to stop waiting.";
        });
        using var observe = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        try
        {
            var exitTask = session.WaitAsync(observe.Token);
            if (await Task.WhenAny(exitTask, operation.Returned.Task) != exitTask) observe.Cancel();
            var exit = await exitTask;
            Post(() => FinishSignIn(profile, operation, exit.Outcome switch
            {
                ProviderSignInOutcome.Cancelled => "Native sign-in was cancelled.",
                ProviderSignInOutcome.Succeeded => "The provider reported successful native sign-in.",
                _ => $"Native sign-in exited ({exit.ExitCode}). Authentication outcome unknown."
            }));
        }
        catch (OperationCanceledException) when (observe.IsCancellationRequested) { }
        catch (Exception)
        {
            Post(() =>
            {
                if (profile.SignIn == operation)
                    profile.InteractionText = "Could not observe native sign-in. Press t after returning, or x to stop waiting.";
            });
        }
    }

    private void FinishSignIn(ProfileState profile, SignInOperation operation, string text)
    {
        if (profile.SignIn != operation) return;
        operation.Returned.TrySetResult();
        profile.SignIn = null;
        profile.InteractionText = text;
        if (_profiles.Contains(profile)) Check(profile);
    }

    private sealed class SignInOperation(ProviderSignInTarget target)
    {
        internal ProviderSignInTarget Target { get; } = target;
        internal bool Started { get; set; }
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
