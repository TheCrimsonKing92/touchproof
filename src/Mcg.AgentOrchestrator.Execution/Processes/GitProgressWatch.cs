namespace Mcg.AgentOrchestrator.Infrastructure;

// A live child's idle time is evidence only while both CPU telemetry and watcher scheduling
// are available. The cadence controls sampling, never the child's permitted runtime.
internal static class GitProgressWatch
{
    internal static readonly TimeSpan SamplingInterval = TimeSpan.FromMilliseconds(100);

    internal interface IChild
    {
        bool HasExited { get; }
        bool WaitForExit(TimeSpan interval);
        TimeSpan? ProcessorTime { get; }
        long StdoutBytes { get; }
        long StderrBytes { get; }
        void KillTree();
    }

    internal readonly record struct Observation(TimeSpan? ProcessorTime, long StdoutBytes,
        long StderrBytes, long Samples, long StarvedSamples, TimeSpan EligibleIdle)
    {
        internal string Describe() => FormattableString.Invariant(
            $"processorTimeMs={ProcessorTime?.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; stdoutBytes={StdoutBytes}; stderrBytes={StderrBytes}; samples={Samples}; onTimeSamples={Samples - StarvedSamples}; starvedSamples={StarvedSamples}; eligibleIdleMs={EligibleIdle.TotalMilliseconds}");
    }

    internal readonly record struct Result(bool Stalled, Observation Last, TimeSpan? StallWindow,
        TimeSpan LongestIdle);

    internal static Result Wait(IChild child, TimeSpan? stallWindow, TimeProvider clock,
        Action<Observation>? reportIdle = null, Action<Observation>? sampled = null)
    {
        var previousTick = clock.GetTimestamp();
        var previousCpu = child.ProcessorTime;
        var previousOut = child.StdoutBytes;
        var previousErr = child.StderrBytes;
        var observation = new Observation(previousCpu, previousOut, previousErr, 0, 0, TimeSpan.Zero);
        var unreportedIdle = TimeSpan.Zero;
        var reportAfter = SamplingInterval;
        var longestIdle = TimeSpan.Zero;

        while (!child.WaitForExit(SamplingInterval))
        {
            var tick = clock.GetTimestamp();
            var interval = clock.GetElapsedTime(previousTick, tick);
            previousTick = tick;
            var cpu = child.ProcessorTime;
            var stdout = child.StdoutBytes;
            var stderr = child.StderrBytes;
            var starved = interval > SamplingInterval * 2;
            var progress = (cpu.HasValue && previousCpu.HasValue && cpu > previousCpu) ||
                stdout > previousOut || stderr > previousErr;
            var knownCpu = cpu.HasValue && previousCpu.HasValue && cpu >= previousCpu;
            var idle = progress ? TimeSpan.Zero : observation.EligibleIdle;
            if (!progress && !starved && knownCpu) idle += interval;
            if (idle > longestIdle) longestIdle = idle;
            observation = new(cpu, stdout, stderr, observation.Samples + 1,
                observation.StarvedSamples + (starved ? 1 : 0), idle);
            previousCpu = cpu;
            previousOut = stdout;
            previousErr = stderr;
            sampled?.Invoke(observation);

            if (progress)
            {
                unreportedIdle = TimeSpan.Zero;
                reportAfter = SamplingInterval;
            }
            else if (stallWindow is null || !knownCpu)
            {
                unreportedIdle += interval;
                if (unreportedIdle >= reportAfter)
                {
                    reportIdle?.Invoke(observation);
                    unreportedIdle = TimeSpan.Zero;
                    reportAfter = SamplingInterval * observation.Samples;
                }
            }

            if (stallWindow is { } window && !progress && !starved && knownCpu && idle > window)
            {
                // An exit observed at the decision boundary takes precedence over idle evidence.
                if (child.HasExited) return new(false, observation, stallWindow, longestIdle);
                child.KillTree();
                return new(true, observation, stallWindow, longestIdle);
            }
        }
        return new(false, observation, stallWindow, longestIdle);
    }
}
