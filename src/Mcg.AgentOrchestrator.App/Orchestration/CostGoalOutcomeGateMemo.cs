namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class CostGoalOutcomeGateMemo
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal int ReadCount { get; private set; }
    internal int Count => entries.Count;
    internal bool Contains(string prefix) => entries.ContainsKey(prefix);

    internal void Retain(IReadOnlySet<string> prefixes)
    {
        foreach (var prefix in entries.Keys.Where(prefix => !prefixes.Contains(prefix)).ToArray())
            entries.Remove(prefix);
    }

    internal OutcomeGateObservation? GetOrRead(string prefix, string resultPath, string metadataPath,
        string lanePlanPath, Func<OutcomeGateObservation?> read)
    {
        var before = Capture(resultPath, metadataPath, lanePlanPath);
        if (before is { } fingerprints && entries.TryGetValue(prefix, out var entry) &&
            entry.Fingerprints == fingerprints)
            return entry.Observation;

        entries.Remove(prefix);
        ReadCount++;
        var observation = read();
        // A publication during ReadGate can change what it reads. Cache only a stable set of fingerprints.
        if (before is { } validated && Capture(resultPath, metadataPath, lanePlanPath) == before)
            entries[prefix] = new(validated, observation);
        return observation;
    }

    private static GateFingerprints? Capture(string resultPath, string metadataPath, string lanePlanPath)
    {
        if (Fingerprint(resultPath) is { } result && Fingerprint(metadataPath) is { } metadata &&
            Fingerprint(lanePlanPath) is { } lanePlan)
            return new(result, metadata, lanePlan);
        return null;
    }

    private static FileFingerprint? Fingerprint(string path)
    {
        try
        {
            var file = new FileInfo(path);
            // Read the timestamp first so metadata access failures surface before Exists can hide them.
            var lastWriteUtc = file.LastWriteTimeUtc;
            return file.Exists ? new FileFingerprint(true, file.Length, lastWriteUtc) : default(FileFingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private sealed record Entry(GateFingerprints Fingerprints, OutcomeGateObservation? Observation);
    private readonly record struct GateFingerprints(FileFingerprint Result, FileFingerprint Metadata, FileFingerprint LanePlan);
    private readonly record struct FileFingerprint(bool Exists, long Length, DateTime LastWriteUtc);
}
