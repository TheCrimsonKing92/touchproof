using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns sound command parsing, library selection, previews and atomic persistence.
internal sealed class OwnerConsoleSoundCommand(string settingsPath,
    OwnerConsoleNotificationSettingsProvider settings, IOwnerConsoleNotifier notifier,
    IOwnerConsoleSoundOutput soundOutput, string defaultLibraryRoot,
    Func<string, bool> folderExists, Func<string, IEnumerable<string>> listFiles)
{
    private const string Usage = "usage: sound [list] | sound play <calm|attention|alarm> [<n|name>] | sound set <calm|attention|alarm> <n|name|system>";
    private static readonly string[] Tiers = ["calm", "attention", "alarm"];
    private readonly string _settingsPath = Path.GetFullPath(settingsPath);
    private readonly object _setLock = new();

    internal static string DefaultLibraryRoot(string localAppData) =>
        Path.Combine(localAppData, "Mcg.AgentOrchestrator", "sounds");

    internal void Run(string line, OwnerConsoleCapturedOutput output)
    {
        var text = line.Trim();
        if (text.StartsWith(':')) text = text[1..].TrimStart();
        var parts = text.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("sound", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine(Usage);
            return;
        }
        try
        {
            if (parts.Length == 1 || parts.Length == 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                List(output);
                return;
            }
            if (parts.Length < 3 || !TryTier(parts[2], out var tier) ||
                !(parts[1].Equals("play", StringComparison.OrdinalIgnoreCase) ||
                  parts[1].Equals("set", StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine(Usage);
                return;
            }
            var set = parts[1].Equals("set", StringComparison.OrdinalIgnoreCase);
            if (parts.Length == 3)
            {
                if (set) output.WriteLine(Usage);
                else
                {
                    notifier.Notify(tier);
                    output.WriteLine($"sound play {TierName(tier)}");
                }
                return;
            }
            var selector = parts[3].Trim();
            if (selector.Length >= 2 && selector[0] is '\'' or '"' && selector[^1] == selector[0])
                selector = selector[1..^1];
            if (set)
            {
                lock (_setLock) Set(tier, selector, output);
            }
            else if (Resolve(tier, selector, output) is { } clip)
            {
                output.WriteLine(soundOutput.TryPlayFile(clip)
                    ? $"sound play {TierName(tier)}: {Path.GetFileName(clip)}"
                    : $"could not play {Path.GetFileName(clip)}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"sound command failed: {ex.Message}");
        }
    }

    private void List(OwnerConsoleCapturedOutput output)
    {
        var snapshot = settings.Current;
        foreach (var name in Tiers)
        {
            TryTier(name, out var tier);
            var selected = Selected(snapshot, tier);
            output.WriteLine($"{name}: {(string.IsNullOrWhiteSpace(selected) ? "system sound" : Path.GetFileName(Path.TrimEndingDirectorySeparator(selected)))}");
            var clips = Library(tier, output);
            if (clips is null) continue;
            if (clips.Length == 0) output.WriteLine("  (no clips)");
            for (var index = 0; index < clips.Length; index++)
                output.WriteLine($"  [{index + 1}] {Path.GetFileName(clips[index])}");
        }
    }

    private string[]? Library(OwnerConsoleNotificationTier tier, OwnerConsoleCapturedOutput output)
    {
        var root = settings.Current.SoundLibrary;
        foreach (var field in ReadDocument())
            if (field.Key.Equals("soundLibrary", StringComparison.OrdinalIgnoreCase))
                root = field.Value is JsonValue value && value.TryGetValue<string>(out var configured) ? configured : null;
        root = string.IsNullOrWhiteSpace(root) ? defaultLibraryRoot : root;
        var folder = Path.GetFullPath(Path.Combine(root, TierName(tier)), Path.GetDirectoryName(_settingsPath)!);
        if (!folderExists(folder))
        {
            output.WriteLine($"{TierName(tier)} library folder not found: {folder}");
            return null;
        }
        // Even injected listings must obey the immediate-file library boundary.
        return listFiles(folder).Where(path => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Where(path => string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
    }

    private string? Resolve(OwnerConsoleNotificationTier tier, string selector, OwnerConsoleCapturedOutput output)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector.IndexOfAny(['/', '\\', ':']) >= 0 || selector.Contains(".."))
        {
            output.WriteLine(Usage);
            return null;
        }
        var clips = Library(tier, output);
        if (clips is null) return null;
        string? clip;
        if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            clip = number > 0 && number <= clips.Length ? clips[number - 1] : null;
        else clip = clips.FirstOrDefault(path => Path.GetFileName(path).Equals(selector, StringComparison.OrdinalIgnoreCase));
        if (clip is null) output.WriteLine($"{TierName(tier)} clip not found: {selector}");
        return clip;
    }

    private void Set(OwnerConsoleNotificationTier tier, string selector, OwnerConsoleCapturedOutput output)
    {
        var clip = selector.Equals("system", StringComparison.OrdinalIgnoreCase) ? null : Resolve(tier, selector, output);
        if (clip is null && !selector.Equals("system", StringComparison.OrdinalIgnoreCase)) return;
        var key = TierName(tier) + "SoundFile";
        var document = ReadDocument();
        foreach (var name in document.Select(field => field.Key).Where(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray())
            document.Remove(name);
        if (clip is not null) document[key] = clip;
        var directory = Path.GetDirectoryName(_settingsPath)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_settingsPath)) File.Replace(temporary, _settingsPath, null);
            else File.Move(temporary, _settingsPath);
        }
        catch (Exception ex)
        {
            output.WriteLine($"sound set failed: {ex.Message}");
            return;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* A cleanup failure must not hide the save result. */ }
        }
        var snapshot = settings.Current;
        settings.Publish(tier switch
        {
            OwnerConsoleNotificationTier.Calm => snapshot with { CalmSoundFile = clip },
            OwnerConsoleNotificationTier.Attention => snapshot with { AttentionSoundFile = clip },
            _ => snapshot with { AlarmSoundFile = clip }
        });
        output.WriteLine($"sound set {TierName(tier)}: {(clip is null ? "system sound" : Path.GetFileName(clip))}");
    }

    private JsonObject ReadDocument()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_settingsPath)) is JsonObject document)
            {
                _ = document.ToArray(); // Materialize within the malformed-document failure boundary.
                return document;
            }
        }
        catch { /* Missing, unreadable or invalid settings are replaced with a fresh object. */ }
        return new JsonObject();
    }

    private static string? Selected(OwnerConsoleNotificationSettings snapshot, OwnerConsoleNotificationTier tier) => tier switch
    {
        OwnerConsoleNotificationTier.Calm => snapshot.CalmSoundFile,
        OwnerConsoleNotificationTier.Attention => snapshot.AttentionSoundFile,
        _ => snapshot.AlarmSoundFile
    };

    private static string TierName(OwnerConsoleNotificationTier tier) => tier.ToString().ToLowerInvariant();
    private static bool TryTier(string name, out OwnerConsoleNotificationTier tier)
    {
        tier = name.ToLowerInvariant() switch
        {
            "calm" => OwnerConsoleNotificationTier.Calm,
            "attention" => OwnerConsoleNotificationTier.Attention,
            "alarm" => OwnerConsoleNotificationTier.Alarm,
            _ => default
        };
        return Tiers.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
