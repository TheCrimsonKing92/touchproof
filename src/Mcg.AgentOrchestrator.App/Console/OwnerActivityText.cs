using System.Text;
using System.Text.RegularExpressions;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerActivityText
{
    private static readonly (Regex Pattern, string Phrase)[] Vocabulary = new (string Term, string Phrase)[]
    {
        ("ticket", "issue"), ("sticky", "persistent"), ("none", "no issue"), ("child result", "test result"),
        ("handoff", "switch"), ("cohort", "joint run"), ("receipt", "proof"), ("canary", "main check"),
        ("tick", "step"), ("eventKind", "event type")
    }.Select(entry => (
        new Regex(Regex.Escape(entry.Term) + "(?<plural>s)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        entry.Phrase)).ToArray();

    internal static string WaitingOn(string? value)
    {
        // Known category-only blockers have friendly labels; recorded sentences retain their subject.
        if (value?.StartsWith("waiting_for_approval", StringComparison.OrdinalIgnoreCase) == true)
            return "waiting for your approval";
        var reason = OwnerHoldReason.Read(value);
        if (reason is null) return "a blocker needs review";
        if (reason.Contains(' ') || reason.Contains('?')) return reason;
        var category = Blocker(reason);
        if (category != "a blocker needs review") return category;

        // Hyphens delimit the fields of a blocker code.
        var label = string.Join("-", reason.Split('-').Select(part => Words(part.Replace('_', ' '), "")));
        return string.IsNullOrWhiteSpace(label) ? category : label;
    }

    internal static string Blocker(string? value) => value?.ToLowerInvariant() switch
    {
        { } text when text.Contains("approval") || text.Contains("owner-review") || text.Contains("owner review") => "waiting for your approval",
        { } text when text.Contains("question") || text.Contains("human") => "waiting for your answer",
        { } text when text.Contains("history") || text.Contains("divergence") => "the goal's history needs review",
        { } text when text.Contains("capacity") || text.Contains("slot") => "waiting for test or worker capacity",
        { } text when text.Contains("main") => "main must be repaired before landing",
        { } text when text.Contains("receipt") || text.Contains("gate") || text.Contains("test") => "waiting for verification to finish",
        _ => "a blocker needs review"
    };

    internal static string RestartReason(OwnerConductEvent item) => Field(item, "reason")?.ToLowerInvariant() switch
    {
        { } value when value.Contains("publish") => "the new version could not be built",
        { } value when value.Contains("timeout") || value.Contains("deadline") => "the new version did not become ready",
        { } value when value.Contains("exit") => "the new process exited before becoming ready",
        _ => "the new version could not start"
    };
    internal static string Words(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var (pattern, phrase) in Vocabulary)
        {
            text = pattern.Replace(text, match =>
            {
                Rune.DecodeLastFromUtf16(text.AsSpan(0, match.Index), out var before, out _);
                Rune.DecodeFromUtf16(text.AsSpan(match.Index + match.Length), out var after, out _);
                if (IsWordNeighbor(before) || IsWordNeighbor(after)) return match.Value;
                if (!match.Groups["plural"].Success) return phrase;
                return phrase + (phrase.EndsWith("ch", StringComparison.Ordinal) ? "es" : "s");
            });
        }
        return text;
    }

    private static bool IsWordNeighbor(Rune value) =>
        Rune.IsLetterOrDigit(value) || value.Value is '_' or '-' or '.' or '`';
}
