using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum LaunchLockEntryPoint
{
    AcquireConsoleForChildSpawn,
    AcquireSuppressedChildSpawn,
    AcquireErrorModeForChildSpawn,
    Start,
    GitCli
}

internal readonly record struct LaunchLockSample(long WaitTicks, long HoldTicks, long CreateTicks);

internal sealed record LaunchLockSnapshot(
    long Launches,
    long WaitTicksTotal,
    long WaitP50Ticks,
    long WaitP95Ticks,
    long WaitMaxTicks,
    long HoldTicksTotal,
    long HoldP50Ticks,
    long HoldP95Ticks,
    long HoldMaxTicks,
    long CreateTicksTotal,
    long[] ByEntry);

internal sealed class LaunchLockCounters
{
    // Bucket zero is zero duration; subsequent buckets cover powers of two microseconds.
    private const int BucketCount = 48;
    private readonly long[] waitBuckets = new long[BucketCount];
    private readonly long[] holdBuckets = new long[BucketCount];
    private readonly long[] byEntry = new long[Enum.GetValues<LaunchLockEntryPoint>().Length];
    private long launches;
    private long waitTicksTotal;
    private long waitMaxTicks;
    private long holdTicksTotal;
    private long holdMaxTicks;
    private long createTicksTotal;

    internal void Record(LaunchLockEntryPoint entry, long waitTicks, long holdTicks, long createTicks)
    {
        waitTicks = Math.Max(0, waitTicks);
        holdTicks = Math.Max(0, holdTicks);
        createTicks = Math.Max(0, createTicks);
        Interlocked.Increment(ref waitBuckets[BucketFor(waitTicks)]);
        Interlocked.Increment(ref holdBuckets[BucketFor(holdTicks)]);
        Interlocked.Add(ref waitTicksTotal, waitTicks);
        Interlocked.Add(ref holdTicksTotal, holdTicks);
        Interlocked.Add(ref createTicksTotal, createTicks);
        UpdateMax(ref waitMaxTicks, waitTicks);
        UpdateMax(ref holdMaxTicks, holdTicks);
        Interlocked.Increment(ref byEntry[(int)entry]);
        Interlocked.Increment(ref launches);
    }

    internal LaunchLockSnapshot Snapshot()
    {
        var count = Interlocked.Read(ref launches);
        var entryCounts = new long[byEntry.Length];
        for (var i = 0; i < entryCounts.Length; i++)
        {
            entryCounts[i] = Interlocked.Read(ref byEntry[i]);
        }
        var waitMax = Interlocked.Read(ref waitMaxTicks);
        var holdMax = Interlocked.Read(ref holdMaxTicks);
        return new LaunchLockSnapshot(
            count,
            Interlocked.Read(ref waitTicksTotal),
            Math.Min(Percentile(waitBuckets, count, 50), waitMax),
            Math.Min(Percentile(waitBuckets, count, 95), waitMax),
            waitMax,
            Interlocked.Read(ref holdTicksTotal),
            Math.Min(Percentile(holdBuckets, count, 50), holdMax),
            Math.Min(Percentile(holdBuckets, count, 95), holdMax),
            holdMax,
            Interlocked.Read(ref createTicksTotal),
            entryCounts);
    }

    private static int BucketFor(long ticks)
    {
        if (ticks == 0) return 0;
        var microseconds = (long)Math.Ceiling(ticks * 1_000_000.0 / Stopwatch.Frequency);
        var bucket = 1;
        while (microseconds > 1 && bucket < BucketCount - 1)
        {
            microseconds = (microseconds + 1) / 2;
            bucket++;
        }
        return bucket;
    }

    private static long Percentile(long[] buckets, long count, int percentile)
    {
        if (count == 0) return 0;
        var rank = (long)Math.Ceiling(count * percentile / 100.0);
        long cumulative = 0;
        for (var bucket = 0; bucket < buckets.Length; bucket++)
        {
            cumulative += Interlocked.Read(ref buckets[bucket]);
            if (cumulative >= rank)
            {
                if (bucket == 0) return 0;
                var upperMicroseconds = 1L << Math.Min(bucket - 1, 46);
                return (long)Math.Ceiling(upperMicroseconds * Stopwatch.Frequency / 1_000_000.0);
            }
        }
        return 0;
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current) return;
            current = previous;
        }
    }
}

internal sealed record LaunchLockClassSnapshot(string Key, long Launches, long CreateTicksTotal, long[] ByEntry);

// Optional process-lifetime attribution. Tests use private instances rather than changing the live provider.
internal sealed class LaunchLockAttribution
{
    private Func<string?>? provider;
    private readonly ConcurrentDictionary<string, ClassCounters> counters = new(StringComparer.Ordinal);
    private readonly Func<LaunchLockEntryPoint, LaunchLockSite> captureSite;

    internal LaunchLockAttribution(Func<LaunchLockEntryPoint, LaunchLockSite>? captureSite = null) =>
        this.captureSite = captureSite ?? LaunchLockSiteCapture.Capture;

    internal bool IsActive => Volatile.Read(ref provider) is not null;

    internal void SetProvider(Func<string?>? value) => Volatile.Write(ref provider, value);

    internal string? CaptureCpuClass()
    {
        try { return Volatile.Read(ref provider)?.Invoke(); }
        catch { return null; }
    }

    internal void Record(LaunchLockEntryPoint entry, long createTicks)
        => RecordCore(entry, createTicks, capture: false);

    internal void RecordWithSite(LaunchLockEntryPoint entry, long createTicks)
        => RecordCore(entry, createTicks, capture: true);

    internal void Record(LaunchLockEntryPoint entry, long createTicks, LaunchLockSite site)
        => RecordCore(entry, createTicks, capture: false, site);

    private void RecordCore(LaunchLockEntryPoint entry, long createTicks, bool capture, LaunchLockSite? site = null)
    {
        var currentProvider = Volatile.Read(ref provider);
        if (currentProvider is null) return;
        string? key;
        try { key = currentProvider(); }
        catch { return; } // Diagnostics must never prevent a process launch.
        if (key is null) return;
        var current = counters.GetOrAdd(key, static _ => new ClassCounters());
        current.Record(entry, createTicks);
        if (capture)
        {
            try { site = captureSite(entry); }
            catch { site = new("Unknown.Unknown", "-"); }
        }
        if (site is { } captured) current.RecordSite(captured);
    }

    internal IReadOnlyList<LaunchLockClassSnapshot> Snapshot() =>
        counters.Select(pair => pair.Value.Snapshot(pair.Key)).ToArray();

    internal IReadOnlyList<LaunchLockSiteSnapshot> SiteSnapshot() =>
        counters.SelectMany(pair => pair.Value.SiteSnapshot(pair.Key)).ToArray();

    private sealed class ClassCounters
    {
        private readonly long[] byEntry = new long[Enum.GetValues<LaunchLockEntryPoint>().Length];
        private long launches;
        private long createTicksTotal;
        private readonly ConcurrentDictionary<LaunchLockSite, long> sites = new();

        internal void RecordSite(LaunchLockSite site) => sites.AddOrUpdate(site, 1, static (_, count) => count + 1);

        internal IEnumerable<LaunchLockSiteSnapshot> SiteSnapshot(string key) =>
            sites.Select(pair => new LaunchLockSiteSnapshot(key, pair.Key, pair.Value));

        internal void Record(LaunchLockEntryPoint entry, long createTicks)
        {
            Interlocked.Increment(ref byEntry[(int)entry]);
            Interlocked.Add(ref createTicksTotal, Math.Max(0, createTicks));
            Interlocked.Increment(ref launches);
        }

        internal LaunchLockClassSnapshot Snapshot(string key)
        {
            var entries = new long[byEntry.Length];
            for (var i = 0; i < entries.Length; i++) entries[i] = Interlocked.Read(ref byEntry[i]);
            return new(key, Interlocked.Read(ref launches), Interlocked.Read(ref createTicksTotal), entries);
        }
    }
}

internal static class LaunchLockTelemetry
{
    private static readonly AsyncLocal<Capture?> ActiveCapture = new();
    internal static LaunchLockCounters Process { get; } = new();
    internal static LaunchLockAttribution Attribution { get; } = new();

    internal static Capture BeginCapture()
    {
        var capture = new Capture(ActiveCapture.Value);
        ActiveCapture.Value = capture;
        return capture;
    }

    internal static void Record(LaunchLockEntryPoint entry, long waitTicks, long holdTicks, long createTicks = 0)
        => RecordInto(Process, Attribution, entry, waitTicks, holdTicks, createTicks);

    internal static void RecordInto(LaunchLockCounters process, LaunchLockAttribution attribution,
        LaunchLockEntryPoint entry, long waitTicks, long holdTicks, long createTicks = 0)
    {
        process.Record(entry, waitTicks, holdTicks, createTicks);
        attribution.RecordWithSite(entry, createTicks);
        var capture = ActiveCapture.Value;
        if (capture is not null)
        {
            capture.Counters.Record(entry, waitTicks, holdTicks, createTicks);
            capture.Samples.Enqueue(new LaunchLockSample(waitTicks, holdTicks, createTicks));
        }
    }

    internal static string FormatSummary(LaunchLockSnapshot snapshot, int processId)
    {
        static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);
        var entries = Enum.GetValues<LaunchLockEntryPoint>();
        var byEntry = new StringBuilder();
        for (var i = 0; i < entries.Length; i++)
        {
            if (i > 0) byEntry.Append(',');
            byEntry.Append(entries[i]).Append(':').Append(snapshot.ByEntry[i].ToString(CultureInfo.InvariantCulture));
        }

        return $"LAUNCH_LOCK_SUMMARY pid={processId} launches={snapshot.Launches} " +
               $"wait_ms_total={Ms(snapshot.WaitTicksTotal)} wait_p50_ms={Ms(snapshot.WaitP50Ticks)} " +
               $"wait_p95_ms={Ms(snapshot.WaitP95Ticks)} wait_max_ms={Ms(snapshot.WaitMaxTicks)} " +
               $"hold_ms_total={Ms(snapshot.HoldTicksTotal)} hold_p50_ms={Ms(snapshot.HoldP50Ticks)} " +
               $"hold_p95_ms={Ms(snapshot.HoldP95Ticks)} hold_max_ms={Ms(snapshot.HoldMaxTicks)} " +
               $"create_ms_total={Ms(snapshot.CreateTicksTotal)} by_entry={byEntry}";
    }

    internal static IEnumerable<string> FormatTeardownLines(
        LaunchLockSnapshot snapshot, LaunchLockAttribution? attribution, int processId)
    {
        yield return FormatSummary(snapshot, processId);
        if (attribution is not null)
        {
            var classes = attribution.Snapshot();
            foreach (var line in FormatByClass(classes, processId)) yield return line;
            foreach (var line in LaunchLockSiteFormatter.FormatBySite(classes, attribution.SiteSnapshot(), processId))
                yield return line;
            if (ReferenceEquals(attribution, Attribution) && attribution.IsActive)
            {
                LaunchLockChildCpuTracker.Live.FlushExited();
                foreach (var line in LaunchLockClassCpuLedger.Live.FormatCpuByClass(processId)) yield return line;
                yield return GitAncestryMemo.FormatSummary(processId);
            }
        }
    }

    internal static IEnumerable<LaunchLockClassSnapshot> ReportedClasses(IReadOnlyList<LaunchLockClassSnapshot> snapshots) =>
        snapshots.OrderByDescending(row => row.Launches).ThenBy(row => row.Key, StringComparer.Ordinal).Take(50);

    internal static string FormatKey(string key) => key.Length == 0 ? "(none)" :
        new string(key.Select(character => char.IsWhiteSpace(character) ? '_' : character).ToArray());

    internal static IEnumerable<string> FormatByClass(IReadOnlyList<LaunchLockClassSnapshot> snapshots, int processId)
    {
        foreach (var snapshot in ReportedClasses(snapshots))
        {
            var key = FormatKey(snapshot.Key);
            var byEntry = string.Join(',', Enum.GetValues<LaunchLockEntryPoint>().Select((entry, index) =>
                $"{entry}:{snapshot.ByEntry[index].ToString(CultureInfo.InvariantCulture)}"));
            yield return FormattableString.Invariant(
                $"LAUNCH_LOCK_BY_CLASS pid={processId} class={key} launches={snapshot.Launches} create_ms_total={snapshot.CreateTicksTotal * 1000.0 / Stopwatch.Frequency:F3} by_entry={byEntry}");
        }
    }

    internal sealed class Capture(Capture? previous) : IDisposable
    {
        internal LaunchLockCounters Counters { get; } = new();
        internal ConcurrentQueue<LaunchLockSample> Samples { get; } = new();
        internal LaunchLockSnapshot Snapshot() => Counters.Snapshot();

        public void Dispose() => ActiveCapture.Value = previous;
    }
}
