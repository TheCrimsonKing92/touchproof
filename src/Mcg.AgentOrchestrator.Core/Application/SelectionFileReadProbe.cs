using System.Text;

namespace Mcg.AgentOrchestrator.Core;

// Observes physical content reads; neither selection nor reuse depends on these counters.
internal static class SelectionFileReadProbe
{
    private static readonly AsyncLocal<Action<string, string, long>?> Observer = new();
    private static readonly AsyncLocal<string?> Part = new();

    internal static T Observe<T>(Action<string, string, long> observer, Func<T> action)
    {
        var previous = Observer.Value;
        Observer.Value = observer;
        try { return action(); }
        finally { Observer.Value = previous; }
    }

    internal static T InPart<T>(string part, Func<T> action)
    {
        var previous = Part.Value;
        Part.Value = part;
        try { return action(); }
        finally { Part.Value = previous; }
    }

    internal static string ReadAllText(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        try { return reader.ReadToEnd(); }
        finally { Observer.Value?.Invoke(Part.Value ?? "unattributed", Path.GetFullPath(path), stream.Position); }
    }

    internal static IEnumerable<string> ReadLines(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        try
        {
            while (reader.ReadLine() is { } line) yield return line;
        }
        finally { Observer.Value?.Invoke(Part.Value ?? "unattributed", Path.GetFullPath(path), stream.Position); }
    }
}
