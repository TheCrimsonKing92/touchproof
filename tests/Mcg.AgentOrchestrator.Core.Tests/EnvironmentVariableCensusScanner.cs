using System.Text.Json;
using System.Text.RegularExpressions;

internal static class EnvironmentVariableCensusScanner
{
    // Full-text discovery also covers constants and raw/interpolated literal portions.
    // A comment-only name is conservatively included rather than silently omitted.
    private static readonly Regex SourceName = new("MCG_[A-Z0-9_]+", RegexOptions.CultureInvariant);
    private static readonly Regex TableName = new("^MCG_[A-Z0-9_]+$", RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> FindUnlisted(IEnumerable<string> sourceTexts, string tableText)
    {
        var listed = ReadRows(tableText).Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
        return DiscoverNames(sourceTexts).Where(name => !listed.Contains(name)).ToArray();
    }

    internal static IReadOnlyList<string> DiscoverNames(IEnumerable<string> sourceTexts) => sourceTexts
        .SelectMany(source => SourceName.Matches(source).Select(match => match.Value))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> ReadAllowlist(string json,
        string source = "config/environment-variable-allowlist.json")
    {
        FormatException Invalid(string detail) => new($"{source}: invalid environment variable allowlist: {detail}.");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Invalid("expected setting and identity groups");
            var groups = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            var allNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("setting" or "identity") || groups.ContainsKey(property.Name))
                    throw Invalid($"unknown or duplicate group {property.Name}");
                if (property.Value.ValueKind != JsonValueKind.Array)
                    throw Invalid($"{property.Name} must be an array");
                var names = new HashSet<string>(StringComparer.Ordinal);
                string? previous = null;
                foreach (var entry in property.Value.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String || !TableName.IsMatch(entry.GetString()!))
                        throw Invalid($"{property.Name} entries must be MCG_ names");
                    var name = entry.GetString()!;
                    if (!allNames.Add(name))
                        throw Invalid($"duplicate name {name}");
                    if (previous is not null && StringComparer.Ordinal.Compare(previous, name) >= 0)
                        throw Invalid($"{property.Name} names must be sorted ordinally");
                    names.Add(name);
                    previous = name;
                }
                groups.Add(property.Name, names);
            }
            if (!groups.ContainsKey("setting") || !groups.ContainsKey("identity"))
                throw Invalid("both setting and identity groups are required");
            return groups;
        }
        catch (JsonException exception)
        {
            throw new FormatException($"{source}: invalid environment variable allowlist JSON.", exception);
        }
    }

    internal static IReadOnlyList<string> FindUnallowlisted(IEnumerable<string[]> rows,
        IReadOnlyDictionary<string, IReadOnlySet<string>> allowlist)
    {
        var violations = new List<string>();
        foreach (var row in rows)
        {
            var category = row[2];
            var group = category.StartsWith("setting", StringComparison.Ordinal) ? "setting"
                : category.StartsWith("identity", StringComparison.Ordinal) ? "identity" : null;
            if (group is null || (allowlist.TryGetValue(group, out var names) && names.Contains(row[0])))
                continue;
            var home = group == "setting" ? ".orchestrator/conductor-policy.json"
                : "an explicit argument at the entry point";
            violations.Add($"{row[0]} (category: {category}) is not allowlisted under {group}; " +
                $"new values belong in {home}. See AGENTS.md, Architecture & Design Discipline: " +
                "Choose where a value lives by what kind of value it is.");
        }
        return violations;
    }

    internal static IReadOnlyList<string[]> ReadRows(string tableText)
    {
        var rows = new List<string[]>();
        foreach (var line in tableText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
                continue;

            var cells = trimmed[1..^1].Split('|').Select(cell => cell.Trim()).ToArray();
            cells[0] = cells[0].Trim('`');
            if (TableName.IsMatch(cells[0]))
                rows.Add(cells);
        }
        return rows;
    }
}
