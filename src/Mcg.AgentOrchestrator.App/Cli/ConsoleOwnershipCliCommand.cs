using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConsoleOwnershipCliCommand
{
    private const string ReceiptError = "Launch receipts missing, unreadable or malformed.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static int Run(IReadOnlyList<string> parts, TextWriter output, TextWriter error) =>
        Run(parts, output, error, ProcessCommandLines.Snapshot);

    internal static int Run(IReadOnlyList<string> parts, TextWriter output, TextWriter error,
        Func<ProcessCommandLineSnapshot> inventory)
    {
        int? harnessId = null;
        string? snapshotPath = null, receiptsPath = null;
        var json = false;
        try
        {
            for (var index = 1; index < parts.Count; index++)
            {
                var flag = parts[index].ToLowerInvariant();
                if (flag == "--json") { json = true; continue; }
                if (flag is not ("--harness-pid" or "--snapshot" or "--receipts"))
                    throw new ArgumentException($"Unknown option '{parts[index]}'.");
                if (++index >= parts.Count || parts[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {flag}.");
                if (flag == "--harness-pid")
                {
                    if (!int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        throw new ArgumentException("--harness-pid requires an integer.");
                    harnessId = parsed;
                }
                else if (flag == "--snapshot") snapshotPath = parts[index];
                else receiptsPath = parts[index];
            }
            if (harnessId is null) throw new ArgumentException("--harness-pid is required.");
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        Dictionary<int, HostProcess> processes;
        var refusalReason = "snapshot-invalid";
        try
        {
            IEnumerable<HostProcess> snapshot;
            if (string.IsNullOrWhiteSpace(snapshotPath))
            {
                refusalReason = "inventory-unavailable";
                snapshot = ReadInventory(inventory);
                refusalReason = "snapshot-invalid";
            }
            else snapshot = ReadRecords(snapshotPath).Select(ReadProcess);
            processes = new();
            foreach (var process in snapshot)
            {
                if (process.Id < 1 || process.ParentId < 0 || string.IsNullOrWhiteSpace(process.Name))
                    throw new ArgumentException("Invalid snapshot identity fields.");
                if (!processes.TryAdd(process.Id, process))
                    throw new ArgumentException("Duplicate snapshot process ids.");
            }
            if (!processes.ContainsKey(harnessId.Value))
            {
                refusalReason = "harness-missing";
                throw new ArgumentException("Harness process missing from snapshot.");
            }
        }
        catch (Exception ex)
        {
            if (json) WriteJson(output, harnessId.Value, null, null, null,
                new Refusal(refusalReason, ex.Message), [], [], []);
            else output.WriteLine($"Ownership ambiguous: snapshot unavailable or invalid ({ex.Message}); candidate group suppressed.");
            return 2;
        }

        string? receiptError = null;
        Receipt[] receipts;
        try
        {
            if (string.IsNullOrWhiteSpace(receiptsPath)) throw new ArgumentException("Launch receipts path missing.");
            receipts = ReadRecords(receiptsPath).Select(fields =>
            {
                var id = Field(fields, "processId");
                var start = Field(fields, "startTime");
                var dispatch = Field(fields, "dispatchId");
                if (id is not int pid || pid < 1 || start is not string ||
                    dispatch is not string text || string.IsNullOrWhiteSpace(text) || Regex.IsMatch(text, @"[\r\n\x00-\x1f]"))
                    throw new ArgumentException("Invalid receipt identity or dispatch fields.");
                return new Receipt(pid, Instant(start), text);
            }).ToArray();
        }
        catch
        {
            receiptError = ReceiptError;
            receipts = [];
        }

        try
        {
            var ancestors = ParentChain(harnessId.Value, processes);
            var withheld = new List<ReasonHost>();
            var unplaced = new List<ReasonHost>();
            var candidates = new List<CandidateHost>();
            foreach (var host in processes.Values.Where(p => Regex.IsMatch(p.Name,
                @"^(OpenConsole|conhost)(\.exe)?$", RegexOptions.IgnoreCase)).OrderBy(p => p.Id))
            {
                var reason = host.Id == harnessId.Value ? "harness process" :
                    ancestors.Contains(host.Id) ? "harness ancestor" :
                    ParentChain(host.Id, processes).Contains(harnessId.Value) ? "harness descendant-console" : null;
                if (reason is not null)
                {
                    withheld.Add(new(host.Id, host.Name, Format(host.Instant), reason));
                    continue;
                }
                var evidence = Matches(host, receipts).Select(r => Evidence(r, host, "direct")).ToList();
                if (host.Instant is not null && processes.TryGetValue(host.ParentId, out var parent) &&
                    parent.Instant is not null && parent.Instant <= host.Instant)
                    evidence.AddRange(Matches(parent, receipts).Select(r => Evidence(r, parent, "launched-process")));
                if (evidence.Count > 0)
                {
                    // PowerShell Sort-Object -Unique uses a culture-aware, case-insensitive order.
                    var comparer = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
                    candidates.Add(new(host.Id, host.Name, Format(host.Instant),
                        evidence.OrderBy(e => e, comparer).Distinct(comparer).ToArray()));
                    continue;
                }
                reason = receiptError is not null ? "receipts unavailable" :
                    host.Instant is null ? "host start time invalid or missing offset" :
                    receipts.Any(r => r.Id == host.Id && r.Instant is null) ? "receipt start time invalid or missing offset" :
                    receipts.Any(r => r.Id == host.Id) ? "start-time mismatch (reused id)" : "no matching receipt";
                unplaced.Add(new(host.Id, host.Name, Format(host.Instant), reason));
            }
            var ambiguous = receiptError is not null || unplaced.Count > 0;
            if (json)
                WriteJson(output, harnessId.Value, withheld.Count, candidates.Count, unplaced.Count, null,
                    withheld, unplaced, ambiguous ? [] : candidates, ambiguous);
            else
            {
                // Finish formatting before emitting: a failure cannot expose a partial candidate group.
                var lines = new List<string>
                {
                    $"Console ownership harness={harnessId} harness-owned={withheld.Count} orchestrator-created={candidates.Count} unattributed={unplaced.Count} result={(ambiguous ? "ambiguous" : "resolved")}",
                    "harness-owned:"
                };
                lines.AddRange(withheld.Select(h => $"  WITHHELD id={h.Id} name={h.Name} startTime={h.StartTime} reason={h.Reason}"));
                lines.Add("unattributed:");
                lines.AddRange(unplaced.Select(h => $"  UNPLACED id={h.Id} name={h.Name} startTime={h.StartTime} reason={h.Reason}"));
                if (ambiguous)
                    lines.Add($"Ownership ambiguous: {receiptError} unplaced=[{string.Join(",", unplaced.Select(h => h.Id))}]; candidate group suppressed ({candidates.Count} matched hosts withheld).");
                else
                {
                    lines.Add("orchestrator-created candidates:");
                    lines.AddRange(candidates.Select(h => $"  CANDIDATE id={h.Id} name={h.Name} startTime={h.StartTime} evidence={string.Join("; ", h.Evidence)}"));
                }
                foreach (var line in lines) output.WriteLine(line);
            }
            return ambiguous ? 2 : 0;
        }
        catch
        {
            output.WriteLine("Ownership ambiguous: unexpected attribution failure; candidate group suppressed.");
            return 1;
        }
    }

    private static IReadOnlyList<Dictionary<string, object?>> ReadRecords(string path)
    {
        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex) { throw new ArgumentException($"Exception calling \"ReadAllText\" with \"1\" argument(s): \"{ex.Message}\""); }
        if (!Regex.IsMatch(raw.Trim(), @"^\[[\s\S]*\]$")) throw new ArgumentException("Expected a JSON array.");
        JsonDocument document;
        try { document = JsonDocument.Parse(raw); }
        catch (JsonException ex) { throw new ArgumentException($"Exception calling \"Parse\" with \"1\" argument(s): \"{ex.Message}\""); }
        using (document)
        {
            var records = new List<Dictionary<string, object?>>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object record.");
                var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in entry.EnumerateObject())
                {
                    var value = property.Value;
                    fields[property.Name] = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.Number => value.TryGetInt32(out var number) ? (object)number : value.GetRawText(),
                        JsonValueKind.Null => null,
                        _ => throw new ArgumentException("Record fields must be scalar strings or integers.")
                    };
                }
                records.Add(fields);
            }
            return records;
        }
    }

    private static object? Field(Dictionary<string, object?> fields, string key) => fields.GetValueOrDefault(key);

    private static HostProcess ReadProcess(Dictionary<string, object?> fields)
    {
        if (Field(fields, "processId") is not int id || id < 1 ||
            Field(fields, "parentProcessId") is not int parent || parent < 0 ||
            Field(fields, "name") is not string name || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Invalid snapshot identity fields.");
        return new(id, parent, name, Instant(Field(fields, "startTime")));
    }

    private static IReadOnlyList<HostProcess> ReadInventory(Func<ProcessCommandLineSnapshot> inventory)
    {
        ProcessCommandLineSnapshot snapshot;
        try { snapshot = inventory(); }
        catch { throw new ArgumentException("Live process inventory unavailable."); }
        if (snapshot.Failure is not null) throw new ArgumentException("Live process inventory unavailable.");
        var records = snapshot.Records.Values;
        if (records.Any(p => p.Status is not (ProcessInspectionStatus.Available or ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled)))
            throw new ArgumentException("Live process inventory contains inaccessible evidence.");
        if (snapshot.Records.Count == 0) throw new ArgumentException("Live process inventory empty.");
        return records.Select(p => new HostProcess(p.ProcessId, p.ParentProcessId, p.Name, p.StartedAt?.ToUniversalTime())).ToArray();
    }

    private static DateTimeOffset? Instant(object? value) => value is string text &&
        Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant.ToUniversalTime() : null;

    private static string Format(DateTimeOffset? instant) =>
        instant?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) ?? "unreadable";

    private static HashSet<int> ParentChain(int first, Dictionary<int, HostProcess> processes)
    {
        var seen = new HashSet<int>();
        while (processes.TryGetValue(first, out var current) && seen.Add(first))
        {
            if (!processes.TryGetValue(current.ParentId, out var parent) || current.Instant is null ||
                parent.Instant is null || parent.Instant > current.Instant) break;
            first = parent.Id;
        }
        return seen;
    }

    private static IEnumerable<Receipt> Matches(HostProcess process, Receipt[] receipts) =>
        receipts.Where(r => r.Id == process.Id && r.Instant is not null && process.Instant is not null &&
            Math.Abs((r.Instant.Value - process.Instant.Value).Ticks) <= TimeSpan.TicksPerSecond);

    private static string Evidence(Receipt receipt, HostProcess process, string route) =>
        $"dispatchId={receipt.DispatchId} route={route} matched(processId={process.Id},startTime={Format(process.Instant)},receiptStartTime={Format(receipt.Instant)},tolerance=1s)";

    private static void WriteJson(TextWriter output, int harnessProcessId, int? harnessOwned,
        int? orchestratorCreated, int? unattributed, Refusal? refusal,
        IReadOnlyList<ReasonHost> withheld, IReadOnlyList<ReasonHost> unplaced,
        IReadOnlyList<CandidateHost> candidates, bool ambiguous = true) =>
        output.WriteLine(JsonSerializer.Serialize(new
        {
            harnessProcessId, harnessOwned, orchestratorCreated, unattributed,
            result = ambiguous ? "ambiguous" : "resolved", refusal, withheld, unplaced, candidates
        }, JsonOptions));

    private sealed record HostProcess(int Id, int ParentId, string Name, DateTimeOffset? Instant);
    private sealed record Receipt(int Id, DateTimeOffset? Instant, string DispatchId);
    private sealed record ReasonHost(int Id, string Name, string StartTime, string Reason);
    private sealed record CandidateHost(int Id, string Name, string StartTime, string[] Evidence);
    private sealed record Refusal(string Reason, string Detail);
}
