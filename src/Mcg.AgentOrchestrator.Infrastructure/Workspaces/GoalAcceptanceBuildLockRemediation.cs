using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using CommandResult = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.CommandResult;
using TransientBuildLockWaitResult = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.TransientBuildLockWaitResult;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class GoalAcceptanceBuildLockRemediation(
    GoalAcceptanceVerifier verifier,
    GoalAcceptanceVerifierTestOverrides testOverrides,
    TimeProvider timeProvider)
{
    private readonly GoalAcceptanceVerifierTestOverrides _testOverrides = testOverrides;
    private readonly TimeProvider _timeProvider = timeProvider;
    private static readonly TimeSpan DefaultTransientNoHolderBuildLockWaitWindow = TimeSpan.FromSeconds(75), DefaultTransientNoHolderBuildLockPollInterval = TimeSpan.FromMilliseconds(250);
    private const int DefaultTransientNoHolderBuildLockMaxRetryCycles = 2;

    internal async Task<(CommandResult Result, bool Remediated)> RemediateBuildLockAndRetryAsync(
        string[] arguments,
        string worktreePath,
        AcceptanceManifestCheck check,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetBuildEnvironmentLease? recoveryLease,
        bool compilerLockRemediationRequired,
        DotnetBuildEnvironment currentEnvironment,
        string attemptName,
        BuildLockAttribution attribution,
        Action<DotnetBuildEnvironment> reacquireLease,
        CancellationToken cancellationToken,
        TimeSpan commandTimeout)
    {
        if (compilerLockRemediationRequired)
        {
            recoveryLease?.MarkCompilerLockRemediationRequired();
            stableSlotLease?.ReleaseExecutionLock();
        }
        if (IsTransientNoHolderBuildArtifactLock(attribution, currentEnvironment))
        {
            return await RetryTransientNoHolderBuildLockAsync(
                arguments,
                worktreePath,
                check,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                currentEnvironment,
                attribution,
                reacquireLease,
                cancellationToken, commandTimeout).ConfigureAwait(false);
        }

        var retryEnvironment = currentEnvironment;
        reacquireLease(retryEnvironment);
        CommandResult? retry = null;
        BuildLockAttribution? retryAttribution = null;
        try
        {
            retry = await verifier.RunManagedDotnetCommandAsync(
                check,
                arguments,
                retryEnvironment,
                worktreePath,
                goalId,
                stableSlotIndex,
                commandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (GoalAcceptanceVerifier.IsBuildArtifactIoException(ex))
        {
            var lockedPath = GoalAcceptanceVerifier.TryExtractPathFromException(ex) ?? retryEnvironment.ArtifactsPath;
            retryAttribution = verifier.AttributeBuildLock(lockedPath, retryEnvironment.ArtifactsPath, "acceptance-retry", check.Name);
        }

        if (retry is not null &&
            !verifier.IsBuildLockFailure(retry, retryEnvironment, check, out retryAttribution))
        {
            return (retry, true);
        }

        retryAttribution ??= attribution;
        if (IsTransientNoHolderBuildArtifactLock(retryAttribution, retryEnvironment))
        {
            return await RetryTransientNoHolderBuildLockAsync(
                arguments,
                worktreePath,
                check,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                retryEnvironment,
                retryAttribution,
                reacquireLease,
                cancellationToken, commandTimeout).ConfigureAwait(false);
        }

        var killed = false;
        foreach (var holder in attribution.Holders
            .Concat(retryAttribution.Holders)
            .Where(holder =>
                holder.IsOrchestratorOwned &&
                holder.ProcessId.HasValue &&
                holder.ProcessId.Value != Environment.ProcessId)
            .DistinctBy(holder => holder.ProcessId!.Value))
        {
            killed |= WorkerProcessJobs.TryKillOrFallbackAndWait(holder.ProcessId!.Value, TimeSpan.FromSeconds(5));
        }

        if (!killed)
        {
            throw new BuildLockBlockedException(retryAttribution);
        }

        var killRetryEnvironment = currentEnvironment;
        reacquireLease(killRetryEnvironment);
        CommandResult killRetry;
        try
        {
            killRetry = await verifier.RunManagedDotnetCommandAsync(
                check,
                arguments,
                killRetryEnvironment,
                worktreePath,
                goalId,
                stableSlotIndex,
                commandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (GoalAcceptanceVerifier.IsBuildArtifactIoException(ex))
        {
            var lockedPath = GoalAcceptanceVerifier.TryExtractPathFromException(ex) ?? killRetryEnvironment.ArtifactsPath;
            throw new BuildLockBlockedException(verifier.AttributeBuildLock(lockedPath, killRetryEnvironment.ArtifactsPath, "acceptance-kill-retry", check.Name));
        }

        if (verifier.IsBuildLockFailure(
            killRetry,
            killRetryEnvironment,
            check,
            out var killRetryAttribution))
        {
            throw new BuildLockBlockedException(killRetryAttribution);
        }

        return (killRetry, true);
    }

    private async Task<(CommandResult Result, bool Remediated)> RetryTransientNoHolderBuildLockAsync(
        string[] arguments,
        string worktreePath,
        AcceptanceManifestCheck check,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetBuildEnvironment currentEnvironment,
        BuildLockAttribution attribution,
        Action<DotnetBuildEnvironment> reacquireLease,
        CancellationToken cancellationToken,
        TimeSpan commandTimeout)
    {
        var retryEnvironment = currentEnvironment;
        var cycleAttribution = attribution;
        var maxRetryCycles = Math.Max(
            1,
            _testOverrides.TransientNoHolderBuildLockMaxRetryCycles ?? DefaultTransientNoHolderBuildLockMaxRetryCycles);
        for (var cycle = 1; cycle <= maxRetryCycles; cycle++)
        {
            var wait = await WaitForBuildArtifactWriteAccessAsync(
                cycleAttribution.Path,
                _testOverrides.TransientNoHolderBuildLockWaitWindow ?? DefaultTransientNoHolderBuildLockWaitWindow,
                _testOverrides.TransientNoHolderBuildLockPollInterval ?? DefaultTransientNoHolderBuildLockPollInterval,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
            EmitTransientNoHolderBuildLockWaitReceipt(cycleAttribution, wait, cycle, maxRetryCycles);
            if (!wait.Released)
            {
                var exhaustedAttribution = verifier.AttributeBuildLock(
                    cycleAttribution.Path,
                    retryEnvironment.ArtifactsPath,
                    "acceptance-transient-exhaustion",
                    check.Name);
                EmitTransientNoHolderBuildLockRetryReceipt(
                    check,
                    exhaustedAttribution,
                    cycle,
                    maxRetryCycles,
                    "wait-exhausted",
                    exitCode: null,
                    timedOut: false,
                    buildLock: true);
                throw new BuildLockBlockedException(exhaustedAttribution);
            }

            reacquireLease(retryEnvironment);
            CommandResult retry;
            try
            {
                retry = await verifier.RunManagedDotnetCommandAsync(
                    check,
                    arguments,
                    retryEnvironment,
                    worktreePath,
                    goalId,
                    stableSlotIndex,
                    commandTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (GoalAcceptanceVerifier.IsBuildArtifactIoException(ex))
            {
                var lockedPath = GoalAcceptanceVerifier.TryExtractPathFromException(ex) ?? retryEnvironment.ArtifactsPath;
                var exceptionAttribution = verifier.AttributeBuildLock(lockedPath, retryEnvironment.ArtifactsPath, "acceptance-transient-retry", check.Name);
                EmitTransientNoHolderBuildLockRetryReceipt(
                    check,
                    exceptionAttribution,
                    cycle,
                    maxRetryCycles,
                    "io-exception",
                    exitCode: null,
                    timedOut: false,
                    buildLock: true);
                if (cycle < maxRetryCycles && IsTransientNoHolderBuildArtifactLock(exceptionAttribution, retryEnvironment))
                {
                    cycleAttribution = exceptionAttribution;
                    continue;
                }

                var exhaustedAttribution = verifier.AttributeBuildLock(
                    exceptionAttribution.Path,
                    retryEnvironment.ArtifactsPath,
                    "acceptance-transient-exhaustion",
                    check.Name);
                throw new BuildLockBlockedException(exhaustedAttribution);
            }

            if (!verifier.IsBuildLockFailure(retry, retryEnvironment, check, out var retryAttribution))
            {
                EmitTransientNoHolderBuildLockRetryReceipt(
                    check,
                    cycleAttribution,
                    cycle,
                    maxRetryCycles,
                    "completed",
                    retry.ExitCode,
                    retry.TimedOut,
                    buildLock: false);
                return (retry, true);
            }

            var finalAttribution = cycle == maxRetryCycles
                ? verifier.AttributeBuildLock(
                    retryAttribution.Path,
                    retryEnvironment.ArtifactsPath,
                    "acceptance-transient-exhaustion",
                    check.Name)
                : retryAttribution;
            EmitTransientNoHolderBuildLockRetryReceipt(
                check,
                finalAttribution,
                cycle,
                maxRetryCycles,
                "blocked",
                retry.ExitCode,
                retry.TimedOut,
                buildLock: true);
            if (cycle < maxRetryCycles && IsTransientNoHolderBuildArtifactLock(retryAttribution, retryEnvironment))
            {
                cycleAttribution = retryAttribution;
                continue;
            }

            throw new BuildLockBlockedException(finalAttribution);
        }

        throw new BuildLockBlockedException(cycleAttribution);
    }

    private static Task<TransientBuildLockWaitResult> WaitForBuildArtifactWriteAccessAsync(
        string path,
        TimeSpan waitWindow,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        GoalAcceptanceVerifierBuildArtifactLock.WaitForBuildArtifactWriteAccessAsync(path, waitWindow, pollInterval, timeProvider, cancellationToken);

    private static void EmitTransientNoHolderBuildLockWaitReceipt(
        BuildLockAttribution attribution,
        TransientBuildLockWaitResult wait,
        int cycle,
        int maxCycles) =>
        GoalAcceptanceVerifierBuildArtifactLock.EmitTransientNoHolderBuildLockWaitReceipt(attribution, wait, cycle, maxCycles);

    private static void EmitTransientNoHolderBuildLockRetryReceipt(
        AcceptanceManifestCheck check,
        BuildLockAttribution attribution,
        int cycle,
        int maxCycles,
        string verdict,
        int? exitCode,
        bool timedOut,
        bool buildLock) =>
        GoalAcceptanceVerifierBuildArtifactLock.EmitTransientNoHolderBuildLockRetryReceipt(check, attribution, cycle, maxCycles, verdict, exitCode, timedOut, buildLock);

    private static bool IsTransientNoHolderBuildArtifactLock(BuildLockAttribution attribution, DotnetBuildEnvironment environment) =>
        GoalAcceptanceVerifierBuildArtifactLock.IsTransientNoHolderBuildArtifactLock(attribution, environment);
}
