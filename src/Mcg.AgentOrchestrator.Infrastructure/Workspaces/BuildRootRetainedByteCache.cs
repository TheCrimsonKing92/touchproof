namespace Mcg.AgentOrchestrator.Infrastructure;

// Only age-based retention can use cached receipt measurements. Byte-budget decisions
// require a live measurement because a filesystem notification can still be in flight.
internal sealed class BuildRootRetainedByteCache : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, Sample> samples = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private FileSystemWatcher? watcher;
    private DateTime goalsCreatedAt;

    private sealed class Sample(DateTime modifiedAt)
    {
        internal readonly DateTime ModifiedAt = modifiedAt;
        internal TerminalBuildRootRetainedBytes.Measurement? Measurement;
    }

    internal Func<string, TerminalBuildRootRetainedBytes.Measurement>? ForStorage(DotnetBuildStorageRoot storage)
    {
        var goals = new DirectoryInfo(Path.Combine(storage.RootPath, "goals"));
        lock (sync)
        {
            if (!goals.Exists || (goals.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Reset();
                return null;
            }

            if (watcher is not null && watcher.Path == goals.FullName && goalsCreatedAt == goals.CreationTimeUtc)
                return Measure;

            Reset();
            try
            {
                watcher = new FileSystemWatcher(goals.FullName)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                        NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.Security
                };
                watcher.Created += OnChange;
                watcher.Changed += OnChange;
                watcher.Deleted += OnChange;
                watcher.Renamed += OnRename;
                watcher.Error += OnError;
                goalsCreatedAt = goals.CreationTimeUtc;
                watcher.EnableRaisingEvents = true;
                return Measure;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                Reset();
                return null;
            }
        }
    }

    private TerminalBuildRootRetainedBytes.Measurement Measure(string root)
    {
        DateTime modifiedAt;
        try
        {
            modifiedAt = Directory.GetLastWriteTimeUtc(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TerminalBuildRootRetainedBytes.Measure(root);
        }
        Sample sample;
        lock (sync)
        {
            if (samples.TryGetValue(root, out var previous) && previous.ModifiedAt == modifiedAt &&
                previous.Measurement is { } cached)
                return cached;
            samples[root] = sample = new Sample(modifiedAt);
        }

        var measured = TerminalBuildRootRetainedBytes.Measure(root);
        lock (sync)
        {
            // Invalidation removes the sample, including any scan still in progress.
            if (samples.TryGetValue(root, out var current) && ReferenceEquals(sample, current))
            {
                if (measured.Complete) sample.Measurement = measured;
                else samples.Remove(root);
            }
        }
        return measured;
    }

    private void OnChange(object sender, FileSystemEventArgs args) => Invalidate(sender, args.Name);

    private void OnRename(object sender, RenamedEventArgs args)
    {
        Invalidate(sender, args.OldName);
        Invalidate(sender, args.Name);
    }

    private void Invalidate(object sender, string? relativePath)
    {
        lock (sync)
        {
            if (watcher is null || !ReferenceEquals(sender, watcher)) return;
            if (string.IsNullOrEmpty(relativePath))
            {
                samples.Clear();
                return;
            }
            var separator = relativePath.AsSpan().IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootName = separator < 0 ? relativePath : relativePath[..separator];
            samples.Remove(Path.Combine(watcher.Path, rootName));
        }
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        lock (sync)
        {
            if (ReferenceEquals(sender, watcher)) Reset();
        }
    }

    private void Reset()
    {
        var previous = watcher;
        watcher = null;
        samples.Clear();
        previous?.Dispose();
    }

    public void Dispose()
    {
        lock (sync) Reset();
    }
}
