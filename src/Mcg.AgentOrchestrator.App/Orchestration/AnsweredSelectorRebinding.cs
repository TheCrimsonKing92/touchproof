using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AnsweredSelectorRebinding
{
    internal sealed record Rebind(string OldClass, string NewClass, string RequestId, string AnswerId);

    // Only the keyword ignores case. Quotes must balance and names must be whole identifiers.
    private const string IdentifierPart = @"\p{L}\p{Nl}\p{Nd}\p{Pc}\p{Mn}\p{Mc}\p{Cf}";
    private static readonly Regex RenamePattern = new(
        $@"(?<![{IdentifierPart}.`])(?<oldQuote>`?)(?<old>[A-Z][{IdentifierPart}]*)\k<oldQuote>(?![{IdentifierPart}.`])" +
        $@"\s+(?:(?i:becomes)|->)\s+" +
        $@"(?<![{IdentifierPart}.`])(?<newQuote>`?)(?<new>[A-Z][{IdentifierPart}]*)\k<newQuote>" +
        // A single sentence period may follow the name; a member-qualification dot may not.
        $@"(?:(?![{IdentifierPart}.`])|(?=\.(?:\s|\z)))",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<(string OldClass, string NewClass)> Parse(string? answer)
    {
        if (string.IsNullOrEmpty(answer)) return [];
        return RenamePattern.Matches(answer)
            .Select(match => (OldClass: match.Groups["old"].Value, NewClass: match.Groups["new"].Value))
            .Where(pair => pair.OldClass != pair.NewClass).Distinct().ToArray();
    }

    internal static IReadOnlyList<Rebind> For(
        Goal goal, GateTestObligation obligation, IEnumerable<HumanInputRequest> requests) =>
        requests.Where(request => request.GoalId == goal.Id && !request.WasDismissed)
            .Select(request => new { Request = request, Answer = request.AuthoritativeAnswer })
            .Where(item => item.Answer is { Origin: HumanInputAnswerOrigin.Operator, IsAutoDefaulted: false } &&
                item.Answer.BriefVersion == obligation.BriefVersion)
            .OrderBy(item => item.Answer!.AnsweredAt)
            .ThenBy(item => item.Answer!.Id, StringComparer.Ordinal)
            .SelectMany(item => Parse(item.Answer!.Text).Select(pair =>
                new Rebind(pair.OldClass, pair.NewClass, item.Request.Id.Value, item.Answer.Id)))
            .GroupBy(rebind => rebind.OldClass, StringComparer.Ordinal)
            .Where(group => group.Select(rebind => rebind.NewClass).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => group.First()).ToArray();

    internal sealed record SelectorEdit(string Selector, string? Replacement,
        IReadOnlyList<string> ObligationIds, string RequestId = "", string AnswerId = "");

    private const string JsonString = "\"(?:[^\"\\\\]|\\\\.)*\"";
    private static readonly Regex SelectorEditPattern = new(
        $@"\Aselector (?<selector>{JsonString})(?: becomes (?<replacement>{JsonString})| dropped) in (?<ids>\[.*\])\z",
        RegexOptions.CultureInvariant);

    internal static string FormatSelectorEdit(string selector, string? replacement, IReadOnlyList<string> obligationIds)
    {
        // Escape spaces too: selector prose must never look like a legacy class mapping.
        static string Encode<T>(T value) => JsonSerializer.Serialize(value).Replace(" ", "\\u0020", StringComparison.Ordinal);
        return $"selector {Encode(selector)}" +
            (replacement is null ? " dropped" : $" becomes {Encode(replacement)}") + $" in {Encode(obligationIds)}";
    }

    internal static SelectorEdit? ParseSelectorEdit(string? answer)
    {
        if (string.IsNullOrEmpty(answer)) return null;
        var match = SelectorEditPattern.Match(answer);
        if (!match.Success) return null;
        try
        {
            var selector = JsonSerializer.Deserialize<string>(match.Groups["selector"].Value);
            var replacement = match.Groups["replacement"].Success
                ? JsonSerializer.Deserialize<string>(match.Groups["replacement"].Value) : null;
            var ids = JsonSerializer.Deserialize<string[]>(match.Groups["ids"].Value);
            if (string.IsNullOrWhiteSpace(selector) ||
                (match.Groups["replacement"].Success && string.IsNullOrWhiteSpace(replacement)) ||
                ids is null || ids.Length == 0 || ids.Any(string.IsNullOrWhiteSpace)) return null;
            return new(selector, replacement, ids);
        }
        catch (JsonException) { return null; }
    }

    internal static IReadOnlyList<SelectorEdit> SelectorEditsFor(
        Goal goal, GateTestObligation obligation, IEnumerable<HumanInputRequest> requests) =>
        requests.Where(request => request.GoalId == goal.Id && !request.WasDismissed)
            .Select(request => new { Request = request, Answer = request.AuthoritativeAnswer })
            .Where(item => item.Answer is { Origin: HumanInputAnswerOrigin.Operator, IsAutoDefaulted: false } &&
                item.Answer.BriefVersion == obligation.BriefVersion)
            .OrderBy(item => item.Answer!.AnsweredAt)
            .ThenBy(item => item.Answer!.Id, StringComparer.Ordinal)
            .Select(item => ParseSelectorEdit(item.Answer!.Text) is { } edit
                ? edit with { RequestId = item.Request.Id.Value, AnswerId = item.Answer.Id } : null)
            .OfType<SelectorEdit>()
            .Where(edit => edit.ObligationIds.Contains(obligation.Id, StringComparer.Ordinal)).ToArray();

    internal static GateTestObligation ApplySelectorEdits(
        Goal goal, GateTestObligation obligation, IEnumerable<HumanInputRequest> requests)
    {
        List<string>? selectors = null;
        foreach (var edit in SelectorEditsFor(goal, obligation, requests))
        {
            var current = selectors ?? obligation.Selectors;
            var index = -1;
            for (var i = 0; i < current.Count; i++)
                if (string.Equals(current[i], edit.Selector, StringComparison.Ordinal)) { index = i; break; }
            if (index < 0 || (edit.Replacement is null && current.Count <= 1)) continue;
            selectors ??= obligation.Selectors.ToList();
            if (edit.Replacement is null) selectors.RemoveAt(index);
            else selectors[index] = edit.Replacement;
        }
        return selectors is null ? obligation : obligation with { Selectors = selectors.ToArray() };
    }
}
