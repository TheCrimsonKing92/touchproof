using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// An immutable note records the checked evidence and the item stamp before the note write.
internal sealed record BoardFillContradictionHold(string MainHead, DateTimeOffset ItemChangeStamp,
    IReadOnlyList<string> EvidencePaths, string Claims)
{
    internal const string Prefix = "BOARD_FILL_CONTRADICTION_HOLD";
    private const string ClaimsHeader = "claims:";

    internal static bool IsMarker(string text) => text.ReplaceLineEndings("\n").Split('\n')[0] == Prefix;

    internal static string Format(string mainHead, DateTimeOffset itemChangeStamp, string premise,
        IReadOnlyList<BoardFillPremiseVerdict> verdicts)
    {
        var contradicted = verdicts.Where(verdict => verdict.Verdict == "contradicted").ToArray();
        var paths = contradicted.Select(verdict => verdict.Evidence)
            .Where(evidence => Regex.IsMatch(evidence, @"^[^\s:]+:[1-9][0-9]*(-[1-9][0-9]*)?$"))
            .Select(evidence => Normalize(BoardFillCitation.Parse(evidence).Path))
            .Where(IsRelativePath).Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join('\n', new[] { Prefix, "main-head: " + mainHead,
            "item-change-stamp: " + itemChangeStamp.ToString("O", CultureInfo.InvariantCulture) }
            .Concat(paths.Select(path => "evidence: " + path)).Append(ClaimsHeader)
            .Concat(contradicted.Select(verdict => BoardFillVerdictRevision.BulletText(premise, verdict.Bullet))));
    }

    internal static BoardFillContradictionHold? Parse(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var claims = Array.IndexOf(lines, ClaimsHeader);
        if (lines.Length < 4 || lines[0] != Prefix || claims < 3 ||
            !lines[1].StartsWith("main-head: ", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(lines[1][11..]) ||
            !lines[2].StartsWith("item-change-stamp: ", StringComparison.Ordinal) ||
            !DateTimeOffset.TryParseExact(lines[2][19..], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var stamp)) return null;
        var paths = new List<string>();
        foreach (var line in lines[3..claims])
        {
            if (!line.StartsWith("evidence: ", StringComparison.Ordinal)) return null;
            var path = Normalize(line[10..]);
            if (!IsRelativePath(path)) return null;
            paths.Add(path);
        }
        return new(lines[1][11..], stamp, paths, string.Join('\n', lines[(claims + 1)..]));
    }

    internal static BacklogNote? NewestMarker(BacklogItem item) => item.Notes
        .Where(note => IsMarker(note.Text)).OrderByDescending(note => note.CreatedAt).FirstOrDefault();

    internal static BacklogNote? ActiveMarker(BacklogItem item) => NewestMarker(item) is { } marker &&
        item.UpdatedAt <= marker.CreatedAt &&
        !item.Notes.Any(note => !IsMarker(note.Text) && note.CreatedAt > marker.CreatedAt) ? marker : null;

    internal static bool Blocks(BacklogItem item, string? currentHead,
        Func<string, string, IReadOnlyList<string>?>? changedFiles) => ActiveMarker(item) is { } marker &&
        !(Parse(marker.Text)?.EvidenceChanged(currentHead, changedFiles) ?? false);

    internal bool EvidenceChanged(string? currentHead, Func<string, string, IReadOnlyList<string>?>? changedFiles)
    {
        if (string.IsNullOrWhiteSpace(currentHead) || currentHead == MainHead ||
            changedFiles is null || EvidencePaths.Count == 0) return false;
        try
        {
            var paths = changedFiles(MainHead, currentHead);
            return paths is not null && paths.Select(Normalize).Intersect(EvidencePaths,
                StringComparer.OrdinalIgnoreCase).Any();
        }
        catch (Exception) { return false; } // Missing evidence of a change keeps the hold in force.
    }

    internal string ReleaseNote(string currentHead) =>
        $"Board-fill contradiction evidence changed between {MainHead} and {currentHead}; retry permitted.";

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static bool IsRelativePath(string path) => !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith('/') && !path.Contains(':') &&
        !path.Split('/').Any(part => part is ".." or "." or "");
}
