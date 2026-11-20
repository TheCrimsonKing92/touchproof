using System.Security;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns console news loading and persistence for one console session.
internal sealed class OwnerConsoleNewsRecord
{
    internal const string FileName = "owner-console-news.json";

    private readonly string _recordPath;
    private readonly Action _dismiss;
    private readonly Action<string> _failed;
    private readonly object _acknowledgementGate = new();
    private volatile IReadOnlyList<OwnerConsoleChangelogEntry> _unseen = Array.Empty<OwnerConsoleChangelogEntry>();
    private int _highestSeen;

    private OwnerConsoleNewsRecord(string changelogPath, string recordPath, Action<string> announce, Action dismiss,
        Action<string> failed)
    {
        _recordPath = recordPath;
        _dismiss = dismiss;
        _failed = failed;
        Loaded = Task.Run(() => Load(changelogPath, announce));
    }

    internal Task Loaded { get; }
    internal IReadOnlyList<OwnerConsoleChangelogEntry> Unseen => _unseen;

    internal static OwnerConsoleNewsRecord Start(string changelogPath, string recordPath, Action<string> announce,
        Action dismiss, Action<string> failed) => new(changelogPath, recordPath, announce, dismiss, failed);

    internal Task AcknowledgeAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (_acknowledgementGate)
        {
            var unseen = _unseen;
            if (!Loaded.IsCompletedSuccessfully || unseen.Count == 0 || cancellationToken.IsCancellationRequested) return;
            var highest = Math.Max(_highestSeen, unseen[^1].Id);
            try { Write(highest); }
            catch (Exception error) when (FileUnavailable(error))
            {
                _failed("Could not save console news acknowledgement. Run :news to retry.");
                return;
            }
            _highestSeen = highest;
            _unseen = Array.Empty<OwnerConsoleChangelogEntry>();
            _dismiss();
        }
    });

    private void Load(string changelogPath, Action<string> announce)
    {
        try
        {
            if (OwnerConsoleChangelog.Parse(File.ReadAllText(changelogPath)) is not { } entries) return;
            int seen;
            try { seen = JsonSerializer.Deserialize<int>(File.ReadAllText(_recordPath)); }
            catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
            {
                // A missing record means no entries have been acknowledged.
                seen = 0;
            }
            if (seen < 0) return;
            _highestSeen = seen;
            _unseen = OwnerConsoleChangelog.Unseen(entries, seen);
        }
        catch (Exception error) when (FileUnavailable(error) || error is JsonException) { return; }

        if (_unseen.Count > 0) announce(OwnerConsoleChangelog.FormatAnnouncement(_unseen));
    }

    private void Write(int id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
        var temporaryPath = _recordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(id));
            File.Move(temporaryPath, _recordPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (FileUnavailable(error)) { }
        }
    }

    private static bool FileUnavailable(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;
}
