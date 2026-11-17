using Mcg.AgentOrchestrator.Core;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class SubscriptionPreflightPromptProjection
{
    public static string ReadLegacyPromptSection(string contextDirectory)
    {
        var reportPath = Path.Combine(contextDirectory, "subscription-preflight.md");
        var (bytes, _) = Apply("subscription-preflight.md", reportPath, File.ReadAllBytes(reportPath), null);
        return Encoding.UTF8.GetString(bytes);
    }

    public static (byte[] Bytes, ContextDeliveryMode? DeliveryMode) Apply(
        string artifactPath,
        string fullReportPath,
        byte[] bytes,
        ContextDeliveryMode? deliveryMode)
    {
        if (!artifactPath.Equals("subscription-preflight.md", StringComparison.OrdinalIgnoreCase))
            return (bytes, deliveryMode);

        var report = Encoding.UTF8.GetString(bytes);
        if (!report.Contains("GATE_TEST_SELECTOR_DIAGNOSTIC", StringComparison.Ordinal))
            return (bytes, deliveryMode);

        var summary = ProjectSelectorDiagnosticPreflight(report, Path.GetFullPath(fullReportPath));
        return (summary is null ? bytes : Encoding.UTF8.GetBytes(summary), ContextDeliveryMode.InlineFull);
    }

    private static string? ProjectSelectorDiagnosticPreflight(string report, string fullReportPath)
    {
        const string marker = "GATE_TEST_SELECTOR_DIAGNOSTIC";
        const int maxBytes = 3584;
        var records = new List<string>();
        var advisories = new List<string>();
        var unparsed = 0;
        using var reader = new StringReader(report);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.StartsWith("- ", StringComparison.Ordinal) ? rawLine[2..] : rawLine;
            if (line.StartsWith(marker + ":", StringComparison.Ordinal))
            {
                advisories.Add(line);
                continue;
            }
            if (!line.StartsWith(marker + " ", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(line[(marker.Length + 1)..]);
                var entry = document.RootElement;
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    unparsed++;
                    continue;
                }
                var obligation = ReadString(entry, "ObligationId", "");
                var criterion = entry.TryGetProperty("CriterionIndex", out var index) &&
                    index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var value)
                    ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
                var selector = ReadString(entry, "EffectiveSelector", "unknown");
                var status = ReadString(entry, "Status", "unknown");
                var compact = $"- criterion={criterion} obligation={obligation[..Math.Min(8, obligation.Length)]} selector={selector} status={status}";
                if (entry.TryGetProperty("DeveloperAssertions", out var assertions) && assertions.ValueKind == JsonValueKind.Array)
                {
                    var differing = assertions.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => OneLine(item.GetString()!))
                        .Where(assertion => !assertion.Equals(selector, StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (differing.Length > 0)
                    {
                        compact += " developer-asserted=" + string.Join(", ", differing.Take(3)
                            .Select(assertion => assertion.Length <= 120 ? assertion : assertion[..119] + "…"));
                        if (differing.Length > 3) compact += $" (+{differing.Length - 3} more)";
                    }
                }
                records.Add(compact);
            }
            catch (JsonException)
            {
                unparsed++;
            }
        }
        if (records.Count == 0) return null;

        var summary = new StringBuilder("# Subscription Preflight selector summary\n\n");
        foreach (var advisory in advisories.Distinct(StringComparer.Ordinal)) summary.AppendLine(advisory);
        summary.AppendLine($"Full report: {fullReportPath}");
        var bytes = Encoding.UTF8.GetByteCount(summary.ToString());
        var included = 0;
        foreach (var record in records)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(record + Environment.NewLine);
            // Reserve space for both omission notices, including their record counts.
            if (bytes + lineBytes > maxBytes - 160) break;
            summary.AppendLine(record);
            bytes += lineBytes;
            included++;
        }
        if (included < records.Count)
            summary.AppendLine($"… {records.Count - included} further records omitted; read the full report");
        if (unparsed > 0) summary.AppendLine($"{unparsed} unparsed records; read the full report");
        return summary.ToString();

        static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        static string ReadString(JsonElement entry, string name, string fallback) =>
            entry.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? OneLine(property.GetString()!) : fallback;
    }
}
