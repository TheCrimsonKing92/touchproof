using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceProcessRunner
{
    internal static async Task<CommandResult> RunProcessAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken) =>
        await RunProcessAsync(
            arguments,
            workingDirectory,
            commandTimeout,
            forceUtf8ConsoleOutput: false,
            cancellationToken).ConfigureAwait(false);

    internal static Task<CommandResult> RunProcessForTestsAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken = default,
        Action<AcceptanceProcessCleanupObservation>? cleanupObserver = null,
        CancellationToken timeoutSignal = default,
        Action<SpawnProcessIdentity>? commandIdentityObserver = null,
        bool? keepCaptureFiles = null,
        Action<int>? resumeObserver = null,
        bool forceUtf8ConsoleOutput = false) =>
        RunProcessAsync(
            arguments,
            workingDirectory,
            commandTimeout,
            forceUtf8ConsoleOutput,
            cancellationToken,
            cleanupObserver,
            timeoutSignal,
            commandIdentityObserver: commandIdentityObserver,
            keepCaptureFiles: keepCaptureFiles,
            resumeObserver: resumeObserver);

    internal static Task<CommandResult> RunManifestCommandForTestsAsync(
        AcceptanceManifestCheck check,
        string workingDirectory,
        TimeSpan commandTimeout,
        IReadOnlyDictionary<string, string?> childEnvironmentOverrides,
        bool resolveBareCommand = true,
        CancellationToken cancellationToken = default) =>
        AcceptanceProcessTestContext.RunManifestCommandAsync(
            BuildCommandArguments(check), workingDirectory, commandTimeout,
            childEnvironmentOverrides, resolveBareCommand, cancellationToken);

    internal static Task<(CommandResult Result, string HeartbeatPath)> RunProcessWithHeartbeatForTestsAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        string heartbeatPath,
        Action<AcceptanceProcessCleanupObservation> cleanupObserver,
        CancellationToken cancellationToken = default,
        CancellationToken timeoutSignal = default) =>
        RunProcessWithHeartbeatForTestsAsync(
            arguments,
            workingDirectory,
            commandTimeout,
            heartbeatPath,
            cleanupObserver,
            cancellationToken,
            timeoutSignal,
            registrationIdentityReader: null);

    internal static async Task<(CommandResult Result, string HeartbeatPath)> RunProcessWithHeartbeatForTestsAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        string heartbeatPath,
        Action<AcceptanceProcessCleanupObservation> cleanupObserver,
        CancellationToken cancellationToken = default,
        CancellationToken timeoutSignal = default,
        Func<Process, SpawnProcessIdentity>? registrationIdentityReader = null)
    {
        var result = await RunProcessAsync(
            arguments, workingDirectory, commandTimeout, forceUtf8ConsoleOutput: false,
            cancellationToken, cleanupObserver, timeoutSignal,
            AcceptanceProcessTestContext.IdentityReader(registrationIdentityReader),
            heartbeatContext: AcceptanceProcessTestContext.Heartbeat(arguments, heartbeatPath)).ConfigureAwait(false);
        return (result, heartbeatPath);
    }

    private static async Task<CommandResult> RunUtf8DiscoveryProcessAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken) =>
        await RunProcessAsync(
            arguments,
            workingDirectory,
            commandTimeout,
            forceUtf8ConsoleOutput: true,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<CommandResult> RunProcessAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        bool forceUtf8ConsoleOutput,
        CancellationToken cancellationToken,
        Action<AcceptanceProcessCleanupObservation>? cleanupObserver = null,
        CancellationToken timeoutSignal = default,
        Func<Process, SpawnProcessIdentityReadResult>? registrationIdentityReader = null,
        Action<SpawnProcessIdentity>? commandIdentityObserver = null,
        GateHeartbeatContext? heartbeatContext = null,
        Action<AcceptanceGateProgress>? progressSink = null,
        AcceptanceGateEngineSettings? engineSettings = null,
        string? attemptResultsPrefix = null,
        string? gateInvocationId = null,
        string? apparatusReceiptPath = null,
        TimeSpan? heartbeatInterval = null, TimeSpan? progressInterval = null,
        TimeSpan? capturePublicationInterval = null,
        bool stopOnCaptureLimit = false,
        bool? keepCaptureFiles = null,
        Action<int>? resumeObserver = null,
        bool resolveBareCommand = false) =>
        await AcceptanceCheckProcessInvoker.RunAsync(
            arguments, workingDirectory, commandTimeout, forceUtf8ConsoleOutput, cancellationToken,
            timeoutSignal, registrationIdentityReader, new AcceptanceProcessRequest
            {
                CleanupObserver = cleanupObserver,
                CommandIdentityObserver = commandIdentityObserver,
                HeartbeatContext = heartbeatContext,
                ProgressSink = progressSink,
                EngineSettings = engineSettings,
                AttemptResultsPrefix = attemptResultsPrefix,
                GateInvocationId = gateInvocationId,
                ApparatusReceiptPath = apparatusReceiptPath,
                HeartbeatInterval = heartbeatInterval,
                ProgressInterval = progressInterval,
                CapturePublicationInterval = capturePublicationInterval,
                StopOnCaptureLimit = stopOnCaptureLimit,
                KeepCaptureFiles = keepCaptureFiles,
                ResumeObserver = resumeObserver,
                ResolveBareCommand = resolveBareCommand,
            }).ConfigureAwait(false);

    internal static class ProcessInvokerAccess
    {
        internal static string QuoteForDisplay(string value) => GoalAcceptanceVerifier.QuoteForDisplay(value);

        internal static TimeSpan DefaultHeartbeatInterval => GoalAcceptanceVerifier.DefaultHeartbeatInterval;
        internal static TimeSpan DefaultProgressInterval => GoalAcceptanceVerifier.DefaultProgressInterval;
    }
}
