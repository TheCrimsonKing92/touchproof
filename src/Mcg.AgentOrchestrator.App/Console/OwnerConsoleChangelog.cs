using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleChangelogEntry(int Id, string Summary, string Fingerprint,
    IReadOnlyList<string> Navigation);

internal static class OwnerConsoleChangelog
{
    internal const string FileName = "owner-console-changelog.md";
    private const string FingerprintLabel = "Surface fingerprint: ";

    internal static IReadOnlyList<OwnerConsoleChangelogEntry>? Parse(string markdown)
    {
        using var reader = new StringReader(markdown);
        var entries = new List<OwnerConsoleChangelogEntry>();
        var line = ReadNonBlank(reader);
        while (line is not null && !line.StartsWith("## ", StringComparison.Ordinal))
            line = ReadNonBlank(reader);

        while (line is not null)
        {
            if (!TryReadHeading(line, out var id, out var summary) ||
                (entries.Count > 0 && id <= entries[^1].Id)) return null;
            var fingerprintLine = ReadNonBlank(reader);
            if (fingerprintLine is null || !fingerprintLine.StartsWith(FingerprintLabel, StringComparison.Ordinal)) return null;
            var fingerprint = fingerprintLine[FingerprintLabel.Length..];
            if (!OwnerConsoleSurfaceFingerprint.IsValid(fingerprint)) return null;

            var navigation = new List<string>();
            while ((line = ReadNonBlank(reader)) is not null && line.StartsWith("- ", StringComparison.Ordinal))
                navigation.Add(line[2..].TrimStart());
            if (navigation.Count == 0) return null;
            entries.Add(new(id, summary, fingerprint, navigation.AsReadOnly()));
        }

        return entries.Count > 0 ? entries.AsReadOnly() : null;
    }

    internal static IReadOnlyList<OwnerConsoleChangelogEntry> Unseen(
        IReadOnlyList<OwnerConsoleChangelogEntry> entries, int highestSeen) =>
        Array.AsReadOnly(entries.Where(entry => entry.Id > highestSeen).ToArray());

    internal static string FormatAnnouncement(IReadOnlyList<OwnerConsoleChangelogEntry> entries) =>
        "Console news (:news): " + string.Join(" | ", entries.Select(entry =>
            $"{entry.Summary} {string.Join("; ", entry.Navigation.Select(PlainKeyText))}"));

    internal static string FormatDetails(IReadOnlyList<OwnerConsoleChangelogEntry> entries) =>
        string.Join("\n\n", entries.Select(entry =>
            $"{entry.Id.ToString(CultureInfo.InvariantCulture)} - {entry.Summary}\n" +
            string.Join("\n", entry.Navigation.Select(item => "- " + PlainKeyText(item)))));

    private static string PlainKeyText(string navigation)
    {
        if (!navigation.Contains('`')) return navigation;

        var parts = navigation.Split('`');
        // An even part count leaves the last delimiter unpaired.
        if (parts.Length % 2 == 0) parts[^2] += "`";
        return string.Concat(parts);
    }

    private static bool TryReadHeading(string line, out int id, out string summary)
    {
        id = 0;
        summary = "";
        if (!line.StartsWith("## ", StringComparison.Ordinal)) return false;
        var separator = line.IndexOf(" - ", 3, StringComparison.Ordinal);
        if (separator < 0) return false;
        summary = line[(separator + 3)..].Trim();
        return summary.Length > 0 &&
            int.TryParse(line.AsSpan(3, separator - 3), NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
            id > 0;
    }

    private static string? ReadNonBlank(StringReader reader)
    {
        while (reader.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
        return null;
    }
}
