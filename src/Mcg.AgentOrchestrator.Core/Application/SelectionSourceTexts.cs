using System.Collections.Concurrent;

namespace Mcg.AgentOrchestrator.Core;

// Direct handoff of source contents for one selection. The caller owns its entire lifetime.
internal sealed class SelectionSourceTexts
{
    private readonly ConcurrentDictionary<string, Lazy<(string Text, long Length, DateTime LastWriteUtc)>> _sources = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal string Read(string path) => Read(path, out _, out _);

    // Keep the pre-read metadata with the text so an existing extract cache cannot
    // publish an earlier selection snapshot under a later file version's stamp.
    internal string Read(string path, out long length, out DateTime lastWriteUtc)
    {
        path = Path.GetFullPath(path);
        var source = _sources.GetOrAdd(path, static file => new(() =>
        {
            var info = new FileInfo(file);
            var size = info.Length;
            var stamp = info.LastWriteTimeUtc;
            return (SelectionFileReadProbe.ReadAllText(file), size, stamp);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        length = source.Length;
        lastWriteUtc = source.LastWriteUtc;
        return source.Text;
    }
}
