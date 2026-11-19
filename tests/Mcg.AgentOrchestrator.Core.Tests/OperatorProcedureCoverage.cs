using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static class OperatorProcedureCoverage
{
    private const string PathCharacter = @"[\w./+-]";
    private static readonly Regex PathToken = new(PathCharacter + "+", RegexOptions.CultureInvariant);
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    internal sealed record Reference(string Document, string Script)
    {
        public override string ToString() => $"{Document} -> {Script}";
    }

    internal static Reference[] FindUncovered(IReadOnlyDictionary<string, string> repository)
    {
        var files = repository.ToDictionary(file => NormalizePath(file.Key), file => file.Value, PathComparer);
        var scripts = files.Keys.Where(path => path.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(PathComparer);
        var byFileName = scripts.ToLookup(Path.GetFileName, PathComparer);
        var spacedPaths = scripts.Concat(byFileName.Select(group => group.Key))
            .Where(path => path.Any(char.IsWhiteSpace))
            .OrderByDescending(path => path.Length)
            .Select(path => path.Contains('/')
                ? $@"(?:{PathCharacter}*/)?{Regex.Escape(path)}"
                : Regex.Escape(path))
            .ToArray();
        // Keep a leading separator or path prefix in the token for repository-path resolution.
        var pathToken = spacedPaths.Length == 0 ? PathToken : new Regex(
            $@"(?<!{PathCharacter})(?:\./)?(?:{string.Join("|", spacedPaths)})\.*(?!{PathCharacter})|{PathToken}",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        IEnumerable<string> NamedScripts(string text)
        {
            foreach (Match match in pathToken.Matches(text.Replace('\\', '/')))
            {
                var token = NormalizePath(match.Value.TrimEnd('.'));
                if (token.Contains('/'))
                {
                    var start = token.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : token.IndexOf("/scripts/", StringComparison.OrdinalIgnoreCase) + 1;
                    if (scripts.TryGetValue(token[start..], out var script))
                        yield return script;
                }
                else
                {
                    foreach (var script in byFileName[token])
                        yield return script;
                }
            }
        }

        var references = files
            .Where(file => IsUnder(file.Key, "docs/negative-controls/", ".md"))
            .SelectMany(file => NamedScripts(file.Value).Select(script => new Reference(file.Key, script)))
            .Distinct()
            .ToArray();
        var uncovered = references.Select(reference => reference.Script).ToHashSet(PathComparer);
        foreach (var (path, contents) in files)
        {
            if (uncovered.Count == 0)
                break;

            if (IsUnder(path, "tests/", ".cs"))
            {
                new StringLiterals(contents, literal =>
                {
                    foreach (var script in NamedScripts(literal))
                        uncovered.Remove(script);
                }).ReadCode();
            }
        }

        return references.Where(reference => uncovered.Contains(reference.Script))
            .OrderBy(reference => reference.Document, StringComparer.Ordinal)
            .ThenBy(reference => reference.Script, StringComparer.Ordinal)
            .ToArray();
    }

    internal static (Reference[] Added, Reference[] Stale) CompareBaseline(
        IReadOnlyCollection<Reference> current, IReadOnlyCollection<Reference> baseline) =>
        (current.Except(baseline).ToArray(), baseline.Except(current).ToArray());

    private static bool IsUnder(string path, string directory, string extension) =>
        path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        path = path.Replace('\\', '/');
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }

    // Walk lexical regions independently. Interpolation holes are code again and can contain
    // their own strings and comments. Emit each text segment independently of adjacent segments.
    private sealed class StringLiterals(string source, Action<string> emit)
    {
        private int position;

        public void ReadCode(int closingBraces = 0)
        {
            var depth = 0;
            while (position < source.Length)
            {
                if (At("//"))
                {
                    while (position < source.Length && source[position] is not ('\r' or '\n'))
                        position++;
                }
                else if (At("/*"))
                {
                    var end = source.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    position = end < 0 ? source.Length : end + 2;
                }
                else if (source[position] == '\'')
                {
                    SkipCharacter();
                }
                else if (source[position] is '"' or '$' or '@')
                {
                    ReadString();
                }
                else if (closingBraces > 0 && source[position] == '}' && depth == 0 && RunOf('}') >= closingBraces)
                {
                    position += closingBraces;
                    return;
                }
                else
                {
                    if (source[position] == '{') depth++;
                    if (source[position] == '}') depth--;
                    position++;
                }
            }
        }

        private void ReadString()
        {
            var start = position;
            var verbatim = Consume('@');
            var dollars = RunOf('$');
            position += dollars;
            verbatim |= Consume('@');
            if (!At("\""))
            {
                position = start + 1;
                return;
            }

            var quotes = RunOf('"');
            var raw = !verbatim && quotes >= 3;
            var delimiter = raw ? quotes : 1;
            position += delimiter;
            var literal = new StringBuilder();
            while (position < source.Length)
            {
                var next = source[position];
                if (verbatim && At("\"\""))
                {
                    literal.Append('"');
                    position += 2;
                }
                else if (next == '"' && RunOf('"') >= delimiter)
                {
                    position += delimiter;
                    emit(literal.ToString());
                    return;
                }
                else if (!raw && !verbatim && next == '\\')
                {
                    position++;
                    ReadEscape(literal);
                }
                else if (!raw && dollars > 0 && (At("{{") || At("}}")))
                {
                    literal.Append(next);
                    position += 2;
                }
                else if (dollars > 0 && next == '{' && RunOf('{') >= dollars)
                {
                    var braces = RunOf('{');
                    literal.Append('{', braces - dollars);
                    emit(literal.ToString());
                    literal.Clear();
                    position += braces;
                    ReadCode(dollars);
                }
                else if (!raw && !verbatim && next is '\r' or '\n')
                {
                    return;
                }
                else
                {
                    literal.Append(next);
                    position++;
                }
            }
        }

        private void SkipCharacter()
        {
            position++;
            while (position < source.Length && source[position] is not ('\r' or '\n'))
            {
                if (Consume('\'')) return;
                if (Consume('\\') && position == source.Length) return;
                position++;
            }
        }

        private void ReadEscape(StringBuilder literal)
        {
            if (position == source.Length) return;
            var escape = source[position++];
            if (escape is 'u' or 'U' or 'x')
            {
                var start = position;
                var limit = escape == 'U' ? 8 : 4;
                while (position < source.Length && position - start < limit && Uri.IsHexDigit(source[position]))
                    position++;

                if (int.TryParse(source.AsSpan(start, position - start), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var value))
                {
                    if (escape != 'U') literal.Append((char)value);
                    else if (Rune.TryCreate(value, out var rune)) literal.Append(rune.ToString());
                }
                return;
            }

            literal.Append(escape switch
            {
                '0' => '\0', 'a' => '\a', 'b' => '\b', 'e' => '\u001b', 'f' => '\f',
                'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', _ => escape
            });
        }

        private bool At(string value) => source.AsSpan(position).StartsWith(value, StringComparison.Ordinal);

        private bool Consume(char value)
        {
            if (position == source.Length || source[position] != value) return false;
            position++;
            return true;
        }

        private int RunOf(char value)
        {
            var end = position;
            while (end < source.Length && source[end] == value) end++;
            return end - position;
        }
    }
}
