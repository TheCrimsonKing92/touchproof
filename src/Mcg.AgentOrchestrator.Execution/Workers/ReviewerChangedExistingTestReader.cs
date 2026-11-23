using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ReviewerChangedExistingTestRead(IReadOnlyList<ChangedExistingTest> Entries, string? Diagnostic);

internal static class ReviewerChangedExistingTestReader
{
    internal static ReviewerChangedExistingTestRead Read(
        string workingDirectory, string? mergeBase, string? headCommit, IReadOnlyList<string>? changedPaths,
        Func<string, string[], GitCli.GitResult>? git = null)
        => Read(workingDirectory, mergeBase, headCommit, changedPaths, git, GitBlobBatchReader.Read);

    internal static ReviewerChangedExistingTestRead Read(
        string workingDirectory, string? mergeBase, string? headCommit, IReadOnlyList<string>? changedPaths,
        Func<string, string[], GitCli.GitResult>? git,
        Func<string, IReadOnlyList<string>, GitBlobBatchReader.Result> batch)
    {
        try
        {
            var paths = (changedPaths ?? []).Select(path => path.Replace('\\', '/'))
                .Where(path => path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) return new([], null);
            var requested = paths.ToHashSet(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(mergeBase))
                return new([], "Changed existing tests unavailable because the reviewer merge base is missing.");
            if (string.IsNullOrWhiteSpace(headCommit))
                return new([], "Changed existing tests unavailable because the candidate head is missing.");
            git ??= (directory, args) => GitCli.Run(directory, args);
            // Resolve once: comparisons must use these immutable commits, even if a branch moves.
            var baseline = Run("rev-parse", "--verify", mergeBase + "^{commit}").Trim();
            var candidate = Run("rev-parse", "--verify", headCommit + "^{commit}").Trim();
            if (baseline.Length == 0 || candidate.Length == 0)
                throw new IOException("git rev-parse returned an empty commit identity.");
            var beforeBlobs = ListFiles(baseline);
            var afterBlobs = ListFiles(candidate);
            var ids = beforeBlobs.Values.Concat(afterBlobs.Values).Distinct(StringComparer.Ordinal).ToArray();
            var contents = batch(workingDirectory, ids);
            Check(contents.Git, ["cat-file", "--batch"]);
            if (contents.Contents.Count != ids.Length)
                throw new IOException("git cat-file --batch returned an incomplete content inventory.");
            var text = ids.Select((id, index) => (id, index)).ToDictionary(item => item.id,
                item => contents.Contents[item.index], StringComparer.Ordinal);
            var entries = new List<ChangedExistingTest>();
            foreach (var path in paths)
            {
                var before = Methods(beforeBlobs.TryGetValue(path, out var beforeId) ? text[beforeId] : string.Empty);
                var after = Methods(afterBlobs.TryGetValue(path, out var afterId) ? text[afterId] : string.Empty);
                foreach (var (key, method) in before)
                {
                    var removed = !after.TryGetValue(key, out var headMethod);
                    if (!removed && method.IsEquivalentTo(headMethod, topLevel: false)) continue;
                    var location = removed ? method : headMethod!;
                    var span = location.SyntaxTree.GetLineSpan(location.Span);
                    entries.Add(new(TypeName(method), method.Identifier.ValueText, path,
                        span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, removed));
                }
            }
            return new(entries, null);

            string Run(params string[] args)
            {
                var result = git(workingDirectory, args);
                Check(result, args);
                return result.Output;
            }

            static void Check(GitCli.GitResult result, string[] args)
            {
                if (!result.Succeeded || !result.ProcessStarted || result.DrainTimedOut)
                    throw new IOException($"git {string.Join(" ", args)} failed (exit {result.ExitCode}, started={result.ProcessStarted}, drainTimedOut={result.DrainTimedOut}): {result.Error}");
            }

            Dictionary<string, string> ListFiles(string commit)
            {
                // Quoted output is ASCII even when the runner uses a legacy Windows code page.
                // NUL output would contain raw UTF-8 paths decoded with that legacy encoding.
                var listing = Run(["-c", "core.quotePath=true", "ls-tree", commit, "--", .. paths]);
                var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
                if (listing.Length == 0) return blobs;
                if (listing[^1] != '\n') throw new IOException("git ls-tree returned an incomplete listing.");
                foreach (var record in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var tab = record.IndexOf('\t');
                    var fields = tab < 0 ? [] : record[..tab].Split(' ');
                    var path = tab < 0 ? string.Empty : GitCli.UnquotePath(record[(tab + 1)..]);
                    if (fields.Length != 3 || !requested.Contains(path) ||
                        fields[2].Length is not (40 or 64) || !fields[2].All(Uri.IsHexDigit) ||
                        fields[1] is not ("blob" or "tree"))
                        throw new IOException("git ls-tree returned an invalid file record.");
                    // A tree at a candidate file path contains no C# method declarations.
                    if (fields[1] == "blob") blobs.Add(path, fields[2]);
                }
                return blobs;
            }
        }
        catch (Exception exception)
        {
            // Partial results would falsely present a complete inventory to the Reviewer.
            return new([], "Changed existing tests unavailable: " + exception.Message.ReplaceLineEndings(" "));
        }
    }

    private static Dictionary<string, MethodDeclarationSyntax> Methods(string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .ToDictionary(method => TypeName(method) + "." + method.Identifier.ValueText + "(" +
                string.Join(",", method.ParameterList.Parameters.Select(parameter =>
                    string.Concat(parameter.Type?.DescendantTokens().Select(token => token.Text) ?? []))) + ")",
                StringComparer.Ordinal);

    private static string TypeName(MethodDeclarationSyntax method) =>
        method.Ancestors().OfType<BaseTypeDeclarationSyntax>().First().Identifier.ValueText;
}
