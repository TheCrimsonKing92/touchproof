using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalCitedDefectAdvisory
{
    private static readonly string[] WasteCues = ["attempt", "ran", "running", "in flight", "burning", "host time", "paid round"];
    private static readonly Regex Cue = new(@"(?<!\w)(?:" + string.Join("|", WasteCues.Select(value =>
        string.Join(@"\s+", value.Split(' ').Select(Regex.Escape)))) + @")(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Citation = new(@"(?<!\w)(?:[a-f0-9]{32}|[a-f0-9]{8})(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static void Warn(string brief, Func<IEnumerable<Goal>> readGoals, Goal newGoal, string conductEventsLogPath)
    {
        try
        {
            var goals = readGoals().ToArray();
            var seen = new HashSet<GoalId>();
            var lines = new List<string>();
            var new8 = newGoal.Id.Value[..8].ToLowerInvariant();
            foreach (var citation in ReadContexts(brief).SelectMany(context => context.CuedCitations()).OrderBy(item => item.Position))
            {
                var matches = goals.Where(goal => goal.Id.Value.StartsWith(citation.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) continue;
                var cited = matches[0];
                if (cited.IsTerminal || cited.Id == newGoal.Id || newGoal.DependsOn.Contains(cited.Id) ||
                    cited.DependsOn.Contains(newGoal.Id) || !seen.Add(cited.Id)) continue;
                var cited8 = cited.Id.Value[..8].ToLowerInvariant();
                lines.Add($"DEPENDENCY_SUGGESTION goal={new8} cited={cited8} status={cited.Status} command=goal-depends {cited8} --on {new8}");
            }

            // Evaluate everything before emitting, and preserve all stderr suggestions if logging fails.
            foreach (var line in lines) Console.Error.WriteLine(line);
            foreach (var line in lines)
                new ConductEventLogWriter(conductEventsLogPath).Append("dependency-suggestion", new8, line);
        }
        catch (Exception exception)
        {
            try { Console.Error.WriteLine($"Dependency suggestion advisory unavailable: {exception.Message.Replace('\r', ' ').Replace('\n', ' ')}"); }
            catch (Exception) { /* The advisory must not prevent filing even when stderr is unavailable. */ }
        }
    }

    private static List<CitationContext> ReadContexts(string brief)
    {
        var lines = brief.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var contexts = new List<CitationContext>();
        var bullets = new List<(int Indent, int ContentIndent, CitationContext Context)>();
        CitationContext? prose = null;
        int? premiseLevel = null;
        char fence = '\0';
        var fenceLength = 0;
        var offset = 0;
        var blank = true;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var position = offset;
            offset += line.Length + 1;
            var fenceMatch = Regex.Match(line, @"^\s*(`{3,}|~{3,})(.*)$");
            if (fence != '\0')
            {
                if (fenceMatch.Success && fenceMatch.Groups[1].Value[0] == fence &&
                    fenceMatch.Groups[1].Length >= fenceLength && string.IsNullOrWhiteSpace(fenceMatch.Groups[2].Value)) fence = '\0';
                continue;
            }
            if (fenceMatch.Success)
            {
                fence = fenceMatch.Groups[1].Value[0];
                fenceLength = fenceMatch.Groups[1].Length;
                prose = null;
                continue;
            }

            var heading = Regex.Match(line, @"^ {0,3}(#{1,6})(?:[ \t]+|$)(.*)$");
            var level = heading.Success ? heading.Groups[1].Length : 0;
            var title = heading.Success ? Regex.Replace(heading.Groups[2].Value, @"[ \t]+#+[ \t]*$", "").Trim() : "";
            if (level == 0 && !string.IsNullOrWhiteSpace(line) && index + 1 < lines.Length)
            {
                var underline = Regex.Match(lines[index + 1], @"^ {0,3}(=+|-+)\s*$");
                if (underline.Success)
                {
                    level = underline.Groups[1].Value[0] == '=' ? 1 : 2;
                    title = line.Trim();
                    offset += lines[++index].Length + 1;
                }
            }
            if (level > 0)
            {
                prose = null;
                bullets.Clear();
                if (premiseLevel is not null && level <= premiseLevel) premiseLevel = null;
                title = title.Trim();
                if (title.EndsWith(':')) title = title[..^1].TrimEnd();
                if (title.Equals("Measured premise", StringComparison.OrdinalIgnoreCase))
                    premiseLevel ??= level;
                blank = true;
                continue;
            }
            if (premiseLevel is null) continue;
            if (string.IsNullOrWhiteSpace(line))
            {
                prose = null;
                blank = true;
                continue;
            }
            // Destinations are not assertions; retain labels and inline code as visible text.
            if (Regex.IsMatch(line, @"^ {0,3}\[[^\]]+\]:")) continue;
            var bullet = Regex.Match(line, @"^(\s*)(?:[-+*]|\d+[.)])\s+");
            var indent = line.Length - line.TrimStart().Length;
            CitationContext context;
            var contentStart = 0;
            if (bullet.Success)
            {
                prose = null;
                while (bullets.Count > 0 && bullets[^1].Indent >= indent) bullets.RemoveAt(bullets.Count - 1);
                context = new CitationContext(isBullet: true);
                contexts.Add(context);
                bullets.Add((indent, bullet.Length, context));
                contentStart = bullet.Length;
            }
            else
            {
                while (bullets.Count > 1 && indent < bullets[^1].ContentIndent) bullets.RemoveAt(bullets.Count - 1);
                if (blank && bullets.Count > 0 && indent < bullets[0].ContentIndent) bullets.Clear();
                if (bullets.Count > 0) context = bullets[^1].Context;
                else
                {
                    if (prose is null) { prose = new CitationContext(isBullet: false); contexts.Add(prose); }
                    context = prose;
                }
            }
            context.Add(line[contentStart..], position + contentStart);
            blank = false;
        }
        return contexts;
    }

    private sealed class CitationContext(bool isBullet)
    {
        private readonly StringBuilder _text = new();
        private readonly List<int> _positions = [];

        internal void Add(string line, int position)
        {
            if (_text.Length > 0) { _text.Append('\n'); _positions.Add(position); }
            _text.Append(line);
            for (var index = 0; index < line.Length; index++) _positions.Add(position + index);
        }

        internal IEnumerable<(string Id, int Position)> CuedCitations()
        {
            // Mask destinations after joining wrapped lines, preserving code spans and source positions.
            var text = Regex.Replace(_text.ToString(), @"(?<code>(?<ticks>`+)(?!`)[\s\S]*?(?<!`)\k<ticks>(?!`))|(?<=\])\((?:[^()\\]|\\.|(?<open>\()|(?<-open>\)))*(?(open)(?!))\)|(?<=\])\[[^\]]*\]|<(?:[a-zA-Z][a-zA-Z0-9+.-]*:[^<>\s]*|[^<>\s]+@[^<>\s]+)>",
                match => match.Groups["code"].Success ? match.Value : new string(' ', match.Length));
            MatchSegment[] sentences = isBullet ? [new MatchSegment(text, 0)] : Regex.Matches(text, @".*?(?:[.!?](?=\s|$)|$)", RegexOptions.Singleline)
                .Where(match => match.Length > 0).Select(match => new MatchSegment(match.Value, match.Index)).ToArray();
            foreach (var sentence in sentences)
            {
                if (!Cue.IsMatch(sentence.Text)) continue;
                foreach (Match match in Citation.Matches(sentence.Text))
                    yield return (match.Value, _positions[sentence.Start + match.Index]);
            }
        }
    }

    private sealed record MatchSegment(string Text, int Start);
}
