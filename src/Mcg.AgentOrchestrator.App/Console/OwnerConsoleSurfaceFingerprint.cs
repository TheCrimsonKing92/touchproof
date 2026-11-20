using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleSurfaceFingerprint
{
    private const string Prefix = "sha256:";

    internal static string Compute(IEnumerable<OwnerConsoleBinding> rows, IEnumerable<OwnerConsoleSurfaceText.Entry> surfaceText)
    {
        var encodedRows = rows.Select(row =>
        {
            string?[] fields = [row.Command, row.Usage, row.Key?.ToString(), row.Ctrl ? "True" : "False",
                row.Scope.ToString(), row.Effect.ToString(), row.Owner.ToString(), row.Confirms ? "True" : "False",
                row.Noun, row.HintLabel, row.KeyHelp, row.CommandHelp];
            return string.Join("\u001f", fields.Select(Encode));
        });
        var canonical = string.Join("\u001e", encodedRows.Concat(surfaceText.Select(entry => Encode(entry.Text))));
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static bool IsValid(string fingerprint) =>
        fingerprint.StartsWith(Prefix, StringComparison.Ordinal) &&
        fingerprint.Length == Prefix.Length + 64 &&
        fingerprint.Skip(Prefix.Length).All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    internal static string? FindMismatch(IEnumerable<OwnerConsoleBinding> rows,
        IEnumerable<OwnerConsoleSurfaceText.Entry> surfaceText,
        IReadOnlyList<OwnerConsoleChangelogEntry>? entries)
    {
        var current = Compute(rows, surfaceText);
        var recorded = entries is { Count: > 0 } ? entries[^1].Fingerprint : null;
        return recorded == current ? null :
            $"Console command surface fingerprint is {current} (recorded: {recorded ?? "missing or invalid"}). " +
            $"Append an entry to docs/{OwnerConsoleChangelog.FileName} and update its surface fingerprint.";
    }

    // Keep literal delimiters and the null marker distinct from characters in field values.
    private static string Encode(string? value) => value is null ? "\0" : value.ReplaceLineEndings("\n")
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal)
        .Replace("\u001e", "\\x1e", StringComparison.Ordinal)
        .Replace("\u001f", "\\x1f", StringComparison.Ordinal);
}
