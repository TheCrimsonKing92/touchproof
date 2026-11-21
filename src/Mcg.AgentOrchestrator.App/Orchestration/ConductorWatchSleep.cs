namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns a run's stop-file polling and exit-artifact wakes between conductor ticks.
internal sealed class ConductorWatchSleep
{
    private readonly string _stopFilePath;
    private (DateTime LastWriteUtc, DateTime CreationUtc, long Length)? _ignoredStopFile;

    internal ConductorWatchSleep(string stopFilePath, bool ignoreInitialStopFile = false)
    {
        _stopFilePath = stopFilePath;
        _ignoredStopFile = ignoreInitialStopFile ? ReadStopFileStamp(stopFilePath) : null;
    }

    internal bool IsIgnoringInitialStopFile => _ignoredStopFile is not null;

    internal bool IsStopRequested()
    {
        if (_ignoredStopFile is null)
            return IsStopRequested(_stopFilePath);

        var currentStopFile = ReadStopFileStamp(_stopFilePath);
        if (currentStopFile == _ignoredStopFile)
            return false;

        // Once the initial file changes or disappears, a present file requests a stop.
        _ignoredStopFile = null;
        return currentStopFile is not null;
    }

    internal string GetWakeStopFilePath() =>
        _ignoredStopFile is not null && ReadStopFileStamp(_stopFilePath) == _ignoredStopFile
            ? string.Empty
            : _stopFilePath;

    internal static bool IsStopRequested(string stopFilePath) =>
        !string.IsNullOrEmpty(stopFilePath) && File.Exists(stopFilePath);

    internal WatchSleepResult Wait(
        TimeSpan interval,
        IConductorWakeSignal? wakeSignal,
        IReadOnlyList<string> trackedExitCodePaths,
        IReadOnlyList<string>? runningAttemptExitCodePaths = null,
        Func<bool>? observeDrain = null)
    {
        wakeSignal?.UpdateTrackedExitArtifacts(trackedExitCodePaths);
        if (wakeSignal is IConductorAttemptExitWakeSignal attemptWake) attemptWake.UpdateTrackedAttemptExitArtifacts(runningAttemptExitCodePaths ?? []);
        var remaining = interval;
        var poll = TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds);
        while (remaining > TimeSpan.Zero)
        {
            if (IsStopRequested())
                return WatchSleepResult.StopRequested;
            if (observeDrain?.Invoke() == true)
                return WatchSleepResult.DrainRequested;
            var slice = remaining < poll ? remaining : poll;
            if (wakeSignal is not null)
            {
                if (wakeSignal.Wait(slice))
                    return WatchSleepResult.WakeSignaled;
            }
            else
            {
                Thread.Sleep(slice);
            }
            remaining -= slice;
        }

        if (IsStopRequested())
            return WatchSleepResult.StopRequested;
        return observeDrain?.Invoke() == true ? WatchSleepResult.DrainRequested : WatchSleepResult.FallbackElapsed;
    }

    private static (DateTime LastWriteUtc, DateTime CreationUtc, long Length)? ReadStopFileStamp(string stopFilePath)
    {
        if (string.IsNullOrEmpty(stopFilePath))
            return null;

        var file = new FileInfo(stopFilePath);
        return file.Exists ? (file.LastWriteTimeUtc, file.CreationTimeUtc, file.Length) : null;
    }
}
