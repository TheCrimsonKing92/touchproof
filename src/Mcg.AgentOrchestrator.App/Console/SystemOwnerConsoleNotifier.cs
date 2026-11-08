namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class SystemOwnerConsoleNotifier(IOwnerConsoleSoundOutput output,
    OwnerConsoleNotificationSettings settings, Func<string, bool> fileExists,
    Func<string, bool>? folderExists = null, Func<string, IEnumerable<string>>? listFiles = null,
    Func<int, int>? choose = null, Func<OwnerConsoleNotificationSettings>? current = null) : IOwnerConsoleNotifier
{
    private readonly object _playbackLock = new();
    private readonly Dictionary<OwnerConsoleNotificationTier, string?> _lastPlayed = [];

    public void Notify(OwnerConsoleNotificationTier tier)
    {
        var snapshot = current?.Invoke() ?? settings;
        var path = tier switch
        {
            OwnerConsoleNotificationTier.Calm => snapshot.CalmSoundFile,
            OwnerConsoleNotificationTier.Attention => snapshot.AttentionSoundFile,
            OwnerConsoleNotificationTier.Alarm => snapshot.AlarmSoundFile,
            _ => null
        };
        lock (_playbackLock)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && TryPlayCustomSound(tier, path)) return;
            }
            catch { /* Custom audio failure still falls back to the tier's system sound. */ }
            output.PlaySystemSound(tier switch
            {
                OwnerConsoleNotificationTier.Calm => 0x40u,
                OwnerConsoleNotificationTier.Attention => 0x30u,
                OwnerConsoleNotificationTier.Alarm => 0x10u,
                _ => 0x40u
            });
        }
    }

    private bool TryPlayCustomSound(OwnerConsoleNotificationTier tier, string path)
    {
        if (fileExists(path)) return TryPlayAndRemember(tier, path);
        if (folderExists is null || listFiles is null || choose is null || !folderExists(path)) return false;

        // Listing is materialized inside Notify's failure boundary on every notification.
        var distinct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in listFiles(path))
        {
            if (string.Equals(Path.GetExtension(candidate), ".wav", StringComparison.OrdinalIgnoreCase) &&
                PathIdentity(candidate) is { } identity)
                distinct.TryAdd(identity, candidate);
        }
        var candidates = distinct.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count > 1 && _lastPlayed.TryGetValue(tier, out var previous))
            candidates.RemoveAll(entry => string.Equals(entry.Key, previous, StringComparison.OrdinalIgnoreCase));
        if (candidates.Count == 0) return false;

        var index = choose(candidates.Count);
        return index >= 0 && index < candidates.Count && TryPlayAndRemember(tier, candidates[index].Value);
    }

    private bool TryPlayAndRemember(OwnerConsoleNotificationTier tier, string path)
    {
        if (!output.TryPlayFile(path)) return false;
        _lastPlayed[tier] = PathIdentity(path);
        return true;
    }

    private static string? PathIdentity(string path)
    {
        // Normalize only the history key; lookup and playback keep the original path semantics.
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
