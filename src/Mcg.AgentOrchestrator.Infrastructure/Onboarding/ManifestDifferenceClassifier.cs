using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ManifestDifferenceKind
{
    NewSectionsOnly,
    ChecksChanged,
    Other
}

public sealed record ManifestDifference(
    ManifestDifferenceKind Kind,
    IReadOnlyList<string> SectionsAdded,
    IReadOnlyList<string> SectionsMissing,
    IReadOnlyList<string> SectionsChanged,
    IReadOnlyList<string> ChecksAdded,
    IReadOnlyList<string> ChecksMissing,
    IReadOnlyList<string> ChecksChanged);

/// <summary>Describes learned manifest content relative to the installed manifest.</summary>
public static class ManifestDifferenceClassifier
{
    public static ManifestDifference? Classify(string? installedText, string? learnedText)
    {
        if (installedText is null || learnedText is null) return null;
        try
        {
            var options = new JsonDocumentOptions { AllowDuplicateProperties = false };
            using var installed = JsonDocument.Parse(installedText, options);
            using var learned = JsonDocument.Parse(learnedText, options);
            if (installed.RootElement.ValueKind != JsonValueKind.Object ||
                learned.RootElement.ValueKind != JsonValueKind.Object) return null;

            var installedChecks = ReadChecks(installed.RootElement);
            var learnedChecks = ReadChecks(learned.RootElement);
            if (installedChecks is null || learnedChecks is null) return null;

            var installedSections = installed.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var learnedSections = learned.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var (sectionsAdded, sectionsMissing, sectionsChanged) = Compare(installedSections, learnedSections);
            var (checksAdded, checksMissing, checksChanged) = Compare(installedChecks, learnedChecks);
            var kind = checksAdded.Length + checksMissing.Length + checksChanged.Length > 0
                ? ManifestDifferenceKind.ChecksChanged
                : sectionsAdded.Length > 0 && sectionsMissing.Length == 0 && sectionsChanged.Length == 0
                    ? ManifestDifferenceKind.NewSectionsOnly
                    : ManifestDifferenceKind.Other;
            return new(kind, sectionsAdded, sectionsMissing, sectionsChanged,
                checksAdded, checksMissing, checksChanged);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Classification is advisory; unsupported input must preserve the byte-difference verdict.
            return null;
        }
    }

    private static Dictionary<string, JsonElement>? ReadChecks(JsonElement manifest)
    {
        var checks = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!manifest.TryGetProperty("checks", out var value)) return checks;
        if (value.ValueKind != JsonValueKind.Array) return null;
        foreach (var check in value.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object ||
                !check.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                return null;
            var text = name.GetString();
            if (string.IsNullOrEmpty(text) || !checks.TryAdd(text, check)) return null;
        }
        return checks;
    }

    private static (string[] Added, string[] Missing, string[] Changed) Compare(
        Dictionary<string, JsonElement> installed, Dictionary<string, JsonElement> learned)
    {
        var added = learned.Keys.Where(name => !installed.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        var missing = installed.Keys.Where(name => !learned.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        var changed = learned.Keys.Where(name => installed.TryGetValue(name, out var value) &&
            !JsonElement.DeepEquals(value, learned[name])).Order(StringComparer.Ordinal).ToArray();
        return (added, missing, changed);
    }
}
