using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Explicit gate manifests bind receipts to their TRX files; the host ledger has no
// attempt id. Replay is retrospective reconstruction and never updates history.
internal sealed class AcceptanceBalancedLaneReplay
{
    internal sealed record LaneInput(string Name, string[] TrxPaths);
    internal sealed record GateInput(string GateId, string ReceiptsPath, LaneInput[] Lanes,
        AcceptanceTestClassDescriptor[]? Classes = null, int? SlotCount = null);
    internal sealed record LaneResult(string Lane, double ActualWallSeconds, double PredictedWallSeconds);
    internal sealed record Result(string GateId, IReadOnlyList<LaneResult> Lanes, double NewMaxPredictedWallSeconds);

    internal Result Read(string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        var gate = JsonSerializer.Deserialize<GateInput>(File.ReadAllText(fullPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException($"Invalid gate manifest: {fullPath}");
        try { return Replay(gate, directory); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or
            System.Xml.XmlException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException($"Gate '{gate.GateId}': {error.Message}", error); }
    }

    internal Result Replay(GateInput gate, string directory)
    {
        if (string.IsNullOrWhiteSpace(gate.GateId) || gate.Lanes is not { Length: > 0 } ||
            gate.Lanes.Any(lane => lane is null || string.IsNullOrWhiteSpace(lane.Name) || lane.TrxPaths is not { Length: > 0 }) ||
            gate.Lanes.Select(lane => lane.Name).Distinct(StringComparer.Ordinal).Count() != gate.Lanes.Length ||
            gate.SlotCount is <= 0)
            throw new InvalidDataException("Invalid gate identity, lanes or slot count.");
        var receipts = new Dictionary<string, (double Wall, int? Slots)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Path.GetFullPath(gate.ReceiptsPath, directory)))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var lane = row.GetProperty("lane").GetString();
            var wall = row.GetProperty("wall_ms").GetDouble();
            int? slots = row.TryGetProperty("slot_count", out var count) ? count.GetInt32() : null;
            if (lane is null || !double.IsFinite(wall) || wall < 0 || slots is <= 0 ||
                !receipts.TryAdd(lane, (wall / 1000, slots)))
                throw new InvalidDataException("Invalid or duplicate lane receipt; supply one gate's receipts.");
        }
        if (!receipts.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(gate.Lanes.Select(lane => lane.Name)))
            throw new InvalidDataException("Receipt lanes do not match TRX lane bindings.");
        var measurements = new Dictionary<string, AcceptanceLaneWallModel.Input>(StringComparer.Ordinal);
        var membership = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var lane in gate.Lanes)
        {
            var paths = lane.TrxPaths.Select(path => Path.GetFullPath(path, directory)).ToArray();
            var identities = paths.SelectMany(path => ValidateTimes(path, receipts[lane.Name].Wall))
                .ToHashSet(StringComparer.Ordinal);
            var timing = LaneSlotTimingReader.Read(paths);
            // Counts can change within a lane; the reader's maximum is the capacity input.
            if (timing.UnavailableReason?.Split("; ", StringSplitOptions.RemoveEmptyEntries)
                .Any(problem => !problem.StartsWith("slot-timing-inconsistent-counts:", StringComparison.Ordinal)) == true)
                throw new InvalidDataException($"Lane '{lane.Name}' slot timing: {timing.UnavailableReason}");
            var inputs = AcceptanceClassDurationResultReader.ReadModelInputs(paths)
                ?? throw new InvalidDataException($"Lane '{lane.Name}' has incomplete or unsuccessful TRX: {string.Join(',', paths)}");
            if (inputs.Count == 0) throw new InvalidDataException($"Lane '{lane.Name}' has no executed classes.");
            foreach (var identity in timing.HeldByFile.Values.SelectMany(file => file.Keys))
                if (!identities.Contains(identity))
                    throw new InvalidDataException($"Lane '{lane.Name}' has unattributed slot row '{identity}'.");
            if (timing.Totals is { } totals && receipts[lane.Name].Slots is { } recorded && totals.SlotCount != recorded)
                throw new InvalidDataException($"Lane '{lane.Name}' slot count differs between receipt and timing rows.");
            foreach (var (name, input) in inputs)
                if (!measurements.TryAdd(name, input with { SlotCount = gate.SlotCount ?? receipts[lane.Name].Slots ?? input.SlotCount }))
                    throw new InvalidDataException($"Class '{name}' is attributed to two lanes.");
            membership.Add(lane.Name, inputs.Keys.Order(StringComparer.Ordinal).ToArray());
        }
        var descriptors = (gate.Classes ?? []).ToDictionary(item => item.FullName, StringComparer.Ordinal);
        var classes = measurements.Keys.Order(StringComparer.Ordinal)
            .Select(name => descriptors.GetValueOrDefault(name) ?? new AcceptanceTestClassDescriptor(name, null)).ToArray();
        foreach (var collection in classes.Where(item => item.Collection is not null).GroupBy(item => item.Collection))
            if (gate.Lanes.Count(lane => membership[lane.Name].Intersect(collection.Select(item => item.FullName), StringComparer.Ordinal).Any()) > 1)
                throw new InvalidDataException($"Collection '{collection.Key}' is split across recorded lanes.");
        var slotsForGate = gate.SlotCount ?? measurements.Values.Max(input => input.SlotCount ?? 1);
        var model = new AcceptanceLaneWallModel(slotsForGate);
        var rows = gate.Lanes.OrderBy(lane => lane.Name, StringComparer.Ordinal).Select(lane =>
        {
            var units = classes.Where(item => membership[lane.Name].Contains(item.FullName, StringComparer.Ordinal))
                .GroupBy(item => item.Collection is null ? "class:" + item.FullName : "collection:" + item.Collection, StringComparer.Ordinal)
                .Select(group => new AcceptanceLaneWallModel.Unit(group.Sum(item => measurements[item.FullName].Seconds),
                    group.Sum(item => measurements[item.FullName].SlotHeldSeconds ?? measurements[item.FullName].Seconds),
                    group.First().Collection is not null));
            return new LaneResult(lane.Name, receipts[lane.Name].Wall, model.Predict(units));
        }).ToArray();
        var layout = AcceptanceBalancedLanePacker.Pack(classes,
            measurements.ToDictionary(pair => pair.Key, pair => pair.Value.Seconds, StringComparer.Ordinal),
            new() { LaneCount = gate.Lanes.Length }, measurements);
        return new(gate.GateId, rows, layout.Lanes.Max(lane => lane.PredictedWallSeconds));
    }

    private static IReadOnlySet<string> ValidateTimes(string path, double wall)
    {
        var document = XDocument.Load(path);
        var definitions = document.Descendants().Where(node => node.Name.LocalName == "UnitTest")
            .ToDictionary(node => (string?)node.Attribute("id") ?? "", StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? first = null, last = null;
        foreach (var row in document.Descendants().Where(node => node.Name.LocalName == "UnitTestResult"))
        {
            if ((string?)row.Attribute("outcome") is "NotExecuted" or "Skipped") continue;
            definitions.TryGetValue((string?)row.Attribute("testId") ?? "", out var definition);
            var identity = AcceptanceTrxTestIdentityResolver.Resolve(row, definition);
            if (string.IsNullOrWhiteSpace(identity))
                throw new InvalidDataException($"Unattributed TRX result in '{path}'.");
            identities.Add(AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity));
            if (!DateTimeOffset.TryParse((string?)row.Attribute("startTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !DateTimeOffset.TryParse((string?)row.Attribute("endTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end < start ||
                !TimeSpan.TryParse((string?)row.Attribute("duration"), CultureInfo.InvariantCulture, out var duration) ||
                Math.Abs(duration.TotalSeconds - (end - start).TotalSeconds) > 0.01)
                throw new InvalidDataException($"Missing or inconsistent TRX start/end/duration in '{path}'.");
            first = first is null || start < first ? start : first;
            last = last is null || end > last ? end : last;
        }
        if (first is null || (last!.Value - first.Value).TotalSeconds > wall + 0.01)
            throw new InvalidDataException($"TRX span exceeds receipt wall or has no executed tests: '{path}'.");
        return identities;
    }
}
