using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Maxima are evidence from commands that exited naturally; terminated commands must not train them.
internal sealed class GitIdleGapHistory
{
    internal const string FileName = "git-idle-gap-history.json";
    // Wall duration says nothing about time without CPU or output. Only idle gaps bound the window.
    internal const int IdleGapMultiplier = 2;
    internal static readonly TimeSpan MinimumNoProgressWindow = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Store(
        [property: JsonRequired] int Version,
        [property: JsonRequired] double LongestIdleGapMilliseconds,
        [property: JsonRequired] Dictionary<string, double> Subcommands);

    private readonly Dictionary<string, double> longest = new(StringComparer.Ordinal);
    private readonly string? path;
    private double longestAny;

    internal GitIdleGapHistory(string? filePath = null)
    {
        if (filePath is null) return;
        try
        {
            path = Path.GetFullPath(filePath);
            Merge(Read(path));
        }
        catch (Exception) { /* Persistence must never prevent a git command. */ }
    }

    internal TimeSpan? Window(string subcommand)
    {
        lock (longest)
        {
            var gap = longest.GetValueOrDefault(subcommand);
            if (gap < GitProgressWatch.SamplingInterval.TotalMilliseconds) gap = longestAny;
            if (gap < GitProgressWatch.SamplingInterval.TotalMilliseconds) return null;
            var milliseconds = Math.Max(gap * IdleGapMultiplier, MinimumNoProgressWindow.TotalMilliseconds);
            // Preserve arbitrarily large evidence up to TimeSpan's representable window.
            return milliseconds >= TimeSpan.MaxValue.TotalMilliseconds
                ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(milliseconds);
        }
    }

    internal void Record(string subcommand, TimeSpan idleGap)
    {
        if (idleGap < GitProgressWatch.SamplingInterval) return;
        lock (longest)
        {
            var milliseconds = idleGap.TotalMilliseconds;
            if (milliseconds <= longest.GetValueOrDefault(subcommand)) return;
            longest[subcommand] = milliseconds;
            longestAny = Math.Max(longestAny, milliseconds);
            if (path is not null) Save();
        }
    }

    // Called under the instance lock; the named mutex serializes read-merge-publish across processes.
    private void Save()
    {
        string? temporary = null;
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            var identity = OperatingSystem.IsWindows() ? path!.ToUpperInvariant() : path!;
            mutex = new Mutex(false, "mcg-git-idle-gap-history-" +
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            Merge(Read(path!));
            Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Store(1, longestAny, longest), JsonOptions));
            File.Move(temporary, path!, overwrite: true);
        }
        catch (Exception) { /* Keep in-memory evidence when synchronization or storage fails. */ }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); } catch (Exception) { }
            try { if (acquired) mutex!.ReleaseMutex(); } catch (Exception) { }
            mutex?.Dispose();
        }
    }

    private void Merge(Store? store)
    {
        if (store is null) return;
        foreach (var (key, milliseconds) in store.Subcommands)
            if (milliseconds >= GitProgressWatch.SamplingInterval.TotalMilliseconds &&
                milliseconds > longest.GetValueOrDefault(key)) longest[key] = milliseconds;
        longestAny = Math.Max(longestAny, longest.Values.DefaultIfEmpty().Max());
    }

    private static Store? Read(string filePath)
    {
        try
        {
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(filePath), JsonOptions);
            if (store is null || store.Version != 1 || store.Subcommands is null ||
                !ValidGap(store.LongestIdleGapMilliseconds) || store.Subcommands.Values.Any(value => !ValidGap(value)) ||
                store.LongestIdleGapMilliseconds != store.Subcommands.Values.DefaultIfEmpty().Max()) return null;
            return store;
        }
        catch (Exception) { return null; }
    }

    private static bool ValidGap(double milliseconds) =>
        double.IsFinite(milliseconds) && milliseconds >= 0 && milliseconds <= TimeSpan.MaxValue.TotalMilliseconds;
}
