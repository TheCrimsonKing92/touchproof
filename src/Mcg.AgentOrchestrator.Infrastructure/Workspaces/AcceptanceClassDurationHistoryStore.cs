using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Successful attempt observations only, oldest first in a bounded per-class window.
internal static class AcceptanceClassDurationHistoryStore
{
    internal const string FileName = "acceptance-class-duration-history.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal sealed record Sample(string AttemptId, string Lane, string ClassName, double Seconds);
    private sealed record Store(int Version, Dictionary<string, List<Sample>> Classes);

    internal static string ResolveStorePath(string worktreePath) => Path.Combine(HostLedgerStateDirectory.Resolve(worktreePath), FileName);

    internal static IReadOnlyDictionary<string, double> ReadMedians(string worktreePath, int window) =>
        ReadMedians(worktreePath, window, out _);

    internal static IReadOnlyDictionary<string, double> ReadMedians(string worktreePath, int window,
        out string? failureReason)
    {
        failureReason = null;
        try
        {
            return Read(ResolveStorePath(worktreePath)).Classes.Where(pair => pair.Value.Count > 0)
                .ToDictionary(pair => pair.Key, pair => AcceptanceBalancedLanePacker.Median(
                    pair.Value.TakeLast(window).Select(sample => sample.Seconds)), StringComparer.Ordinal);
        }
        catch (Exception error)
        {
            failureReason = "class-history-read:" + error.GetType().Name;
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
    }

    internal static string? RecordAttempt(string worktreePath, string attemptId, IReadOnlyList<Sample> samples,
        int window, bool completed, bool passed)
    {
        if (!completed || !passed || samples.Count == 0) return null;
        if (window is < 1 or > 100 || string.IsNullOrWhiteSpace(attemptId) || samples.Any(sample =>
                sample.AttemptId != attemptId || string.IsNullOrWhiteSpace(sample.ClassName) ||
                string.IsNullOrWhiteSpace(sample.Lane) || !double.IsFinite(sample.Seconds) || sample.Seconds < 0))
            return "class-history-invalid-sample";
        string? temporary = null;
        var acquired = false;
        Mutex? mutex = null;
        try
        {
            var path = Path.GetFullPath(ResolveStorePath(worktreePath));
            var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
            mutex = new Mutex(false, "mcg-class-history-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return "class-history-lock-timeout";
            var store = Read(path);
            foreach (var group in samples.GroupBy(sample => sample.ClassName, StringComparer.Ordinal))
            {
                var history = store.Classes.GetValueOrDefault(group.Key) ?? [];
                if (history.Any(sample => sample.AttemptId == attemptId)) continue;
                // A scanner-missed class can also match a constrained lane. Never count a
                // duplicate execution as two observations for the same attempt.
                history.Add(group.OrderByDescending(sample => sample.Seconds).ThenBy(sample => sample.Lane, StringComparer.Ordinal).First());
                store.Classes[group.Key] = history.TakeLast(window).ToList();
            }
            foreach (var name in store.Classes.Keys.ToArray()) store.Classes[name] = store.Classes[name].TakeLast(window).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(store, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception error) { return "class-history-write:" + error.GetType().Name; }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); } catch (Exception) { }
            try { if (acquired) mutex!.ReleaseMutex(); } catch (Exception) { }
            mutex?.Dispose();
        }
    }

    private static Store Read(string path)
    {
        try
        {
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(path), JsonOptions);
            if (store is null || store.Version != 1 || store.Classes is null || store.Classes.Any(pair =>
                    pair.Value is null || pair.Value.Any(sample => sample is null || sample.ClassName != pair.Key ||
                        string.IsNullOrWhiteSpace(sample.AttemptId) || string.IsNullOrWhiteSpace(sample.Lane) ||
                        !double.IsFinite(sample.Seconds) || sample.Seconds < 0)))
                throw new InvalidDataException("Invalid class duration history store.");
            return store;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(1, new(StringComparer.Ordinal)); }
    }
}
