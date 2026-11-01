using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One normalization owns this report. Counts describe visited boundaries and explicit
// successful writes, not descendants changed through mandatory-label inheritance.
internal sealed class AcceptanceGateIntegrityNormalizeReport
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly List<string> entryLines = [];
    private int entries;
    private int relabeled;
    private int skipped;

    internal void CountEntry() => entries++;
    internal void CountRelabel() => relabeled++;
    internal void CountSkip() => skipped++;

    internal void MeasureEntry(string name, Action normalize)
    {
        var started = Stopwatch.GetTimestamp();
        var initialEntries = entries;
        var initialRelabeled = relabeled;
        normalize();
        var milliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var pruned = entries == initialEntries ? "true" : "false";
        entryLines.Add(FormattableString.Invariant(
            $"INTEGRITY_NORMALIZE_ENTRY name={JsonSerializer.Serialize(name)} elapsed_ms={milliseconds} entries={entries - initialEntries} relabeled={relabeled - initialRelabeled} skipped={pruned}"));
    }

    internal void WriteSuccess()
    {
        elapsed.Stop();
        foreach (var line in entryLines)
            TryWrite(line);
        TryWrite(FormattableString.Invariant(
            $"INTEGRITY_NORMALIZE elapsed_ms={elapsed.ElapsedMilliseconds} entries={entries} relabeled={relabeled} skipped={skipped}"));
    }

    private static void TryWrite(string line)
    {
        // Diagnostics cannot turn a prepared workspace into a failed gate.
        try { Console.WriteLine(line); }
        catch { }
    }
}
