using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owned by the class counters: each attributed launch increments exactly one image bucket.
internal sealed class LaunchLockImageCounters
{
    private readonly long[] counts = new long[7];
    // Recording is synchronous inside Start; nested launches restore the enclosing image.
    [ThreadStatic] private static int? currentImage;

    internal static ImageScope Begin(string? fileName)
    {
        var scope = new ImageScope(currentImage);
        currentImage = Classify(fileName);
        return scope;
    }

    internal void RecordCurrent() => Interlocked.Increment(ref counts[currentImage ?? 6]);

    internal long[] Snapshot()
    {
        var snapshot = new long[counts.Length];
        for (var i = 0; i < snapshot.Length; i++) snapshot[i] = Interlocked.Read(ref counts[i]);
        return snapshot;
    }

    private static int Classify(string? fileName)
    {
        try
        {
            var name = (fileName ?? "").Trim().Trim('"');
            name = name[(Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\')) + 1)..];
            var extension = name.LastIndexOf('.');
            if (extension >= 0) name = name[..extension];
            return name.ToLowerInvariant() switch
            {
                "git" => 0, "powershell" => 1, "pwsh" => 2, "icacls" => 3,
                "cmd" => 4, "dotnet" => 5, _ => 6
            };
        }
        catch { return 6; } // Diagnostics must not change launch results.
    }

    internal static string Format(string key, long[] snapshot, int processId) => string.Create(
        CultureInfo.InvariantCulture,
        $"LAUNCH_LOCK_BY_IMAGE pid={processId} class={LaunchLockTelemetry.FormatKey(key)} git={snapshot[0]} powershell={snapshot[1]} pwsh={snapshot[2]} icacls={snapshot[3]} cmd={snapshot[4]} dotnet={snapshot[5]} other={snapshot[6]}");

    internal readonly struct ImageScope(int? previous) : IDisposable
    {
        public void Dispose() => currentImage = previous;
    }
}
