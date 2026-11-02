using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Test-only cumulative counts with independent snapshots for each slot hold.</summary>
public sealed class HostCapacitySlotGitCensus(Func<string> currentTestName)
{
    private readonly object sync = new();
    private readonly Dictionary<string, Dictionary<string, int>> counts = new(StringComparer.Ordinal);

    public static HostCapacitySlotGitCensus Shared { get; } = new(HostCapacitySlotTimingWriter.CurrentTestName);

    private static Func<Action?>? currentTestAcquisition;

    // Installed independently of the optional census destination: capacity applies even
    // when no telemetry is requested. Resolve the owner in the launching ExecutionContext.
    public static void RegisterAcquisitionResolver(Func<Action?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (Interlocked.CompareExchange(ref currentTestAcquisition, resolver, null) is not null)
            throw new InvalidOperationException("The test capacity acquisition resolver is already registered.");
        Interlocked.CompareExchange(ref GitCli.LaunchStarting, BeforeLaunch, null);
    }

    public static void BeforeLaunch() => Volatile.Read(ref currentTestAcquisition)?.Invoke()?.Invoke();

    public sealed record GitLaunchDelta(int Launches, IReadOnlyDictionary<string, int> Subcommands);

    public static bool EnsureInstalled(string? destination) =>
        EnsureInstalled(destination, ref GitCli.LaunchObserver);

    // A ref target permits lifecycle checks without changing process-global instrumentation.
    public static bool EnsureInstalled(string? destination, ref Action<string>? target)
    {
        if (string.IsNullOrEmpty(destination)) return false;
        try
        {
            return Interlocked.CompareExchange(ref target, Shared.Record, null) is null;
        }
        catch (Exception error) { Diagnose(error); return false; }
    }

    public static void RecordProbeLaunch(string subcommand)
    {
        try { Volatile.Read(ref GitCli.LaunchObserver)?.Invoke(subcommand); }
        catch (Exception error) { Diagnose(error); }
    }

    public void Record(string subcommand)
    {
        try
        {
            var name = currentTestName();
            lock (sync)
            {
                if (!counts.TryGetValue(name, out var commands))
                    counts[name] = commands = new(StringComparer.Ordinal);
                commands[subcommand] = checked(commands.GetValueOrDefault(subcommand) + 1);
            }
        }
        catch (Exception error) { Diagnose(error); }
    }

    public Window? Open(string testName)
    {
        try
        {
            lock (sync)
                return new Window(this, testName, counts.TryGetValue(testName, out var commands)
                    ? new(commands, StringComparer.Ordinal) : new(StringComparer.Ordinal));
        }
        catch (Exception error) { Diagnose(error); return null; }
    }

    public sealed class Window
    {
        private readonly HostCapacitySlotGitCensus census;
        private readonly string testName;
        private readonly Dictionary<string, int> initial;

        internal Window(HostCapacitySlotGitCensus census, string testName, Dictionary<string, int> initial)
        {
            this.census = census;
            this.testName = testName;
            this.initial = initial;
        }

        public GitLaunchDelta? Close()
        {
            try
            {
                lock (census.sync)
                {
                    var delta = new Dictionary<string, int>(StringComparer.Ordinal);
                    var total = 0;
                    if (census.counts.TryGetValue(testName, out var commands))
                        foreach (var (command, count) in commands)
                        {
                            var difference = count - initial.GetValueOrDefault(command);
                            if (difference == 0) continue;
                            delta[command] = difference;
                            total = checked(total + difference);
                        }
                    return new(total, delta);
                }
            }
            catch (Exception error) { Diagnose(error); return null; }
        }
    }

    private static void Diagnose(Exception error) =>
        HostCapacitySlotTimingWriter.Diagnose("git census", error.GetType().Name + ": " + error.Message);
}
