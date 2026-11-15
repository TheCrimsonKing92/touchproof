using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class CandidateAddedClassEvidence
{
    internal sealed record AddedClass(string Name, string Path);

    internal static IReadOnlyList<AddedClass> Read(
        string repositoryDirectory, string integrationBranch, string candidateSha,
        Func<string, string[], GitCli.GitResult>? runGit = null)
    {
        runGit ??= GitCli.Run;
        try
        {
            var mergeBase = Git(repositoryDirectory, runGit, "merge-base", integrationBranch, candidateSha).Trim();
            var changes = Git(repositoryDirectory, runGit, "diff", "--no-renames", "--name-status", "-z",
                mergeBase, candidateSha, "--", "*.cs").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (changes.Length % 2 != 0) throw new InvalidOperationException("Incomplete class diff evidence.");
            var added = new List<AddedClass>();
            for (var index = 0; index < changes.Length; index += 2)
            {
                var status = changes[index];
                var path = changes[index + 1];
                if (status == "D") continue;
                if (status is not ("A" or "M")) throw new InvalidOperationException("Unexpected class diff status.");
                var names = Classes(repositoryDirectory, candidateSha, path, runGit);
                if (status == "M") names.ExceptWith(Classes(repositoryDirectory, mergeBase, path, runGit));
                added.AddRange(names.Select(name => new AddedClass(name, path)));
            }
            if (added.Count == 0) return [];

            // One revision-wide query excludes moved classes and new partials of existing classes.
            var previous = ClassesMentioning(repositoryDirectory, mergeBase,
                added.Select(item => item.Name).Distinct(StringComparer.Ordinal).ToArray(), runGit);
            return added.Where(item => !previous.Contains(item.Name)).Distinct()
                .OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray();
        }
        catch (Exception)
        {
            // Missing blobs, incomplete git output and recovered syntax cannot authorize a binding.
            return [];
        }
    }

    internal static bool ProvesClassAbsent(string repositoryDirectory, string revision, string className,
        Func<string, string[], GitCli.GitResult>? runGit = null)
    {
        if (!SyntaxFacts.IsValidIdentifier(className)) return false;
        try
        {
            return !ClassesMentioning(repositoryDirectory, revision, [className], runGit ?? GitCli.Run).Contains(className);
        }
        catch (Exception) { return false; }
    }

    private static HashSet<string> ClassesMentioning(string directory, string revision, IReadOnlyList<string> names,
        Func<string, string[], GitCli.GitResult> runGit)
    {
        // Search a superset of declarations, then compare Roslyn identifiers exactly. Escaped
        // identifiers can declare the requested name without spelling it literally in the blob.
        var args = new List<string> { "grep", "-l", "-z", "--full-name", "-F", "-e", @"\u", "-e", @"\U" };
        foreach (var name in names) args.AddRange(["-e", name]);
        args.AddRange([revision, "--", "*.cs"]);
        var result = runGit(directory, args.ToArray());
        if (result.DrainTimedOut) throw new InvalidOperationException("Git evidence drain timed out.");
        if (result.ExitCode == 1 && result.Output.Length == 0 && result.ProcessStarted && result.Error.Length == 0)
            return new(StringComparer.Ordinal);
        if (!result.Succeeded) throw new InvalidOperationException("Git class evidence unavailable.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var prefix = revision + ":";
            if (!entry.StartsWith(prefix, StringComparison.Ordinal) || entry.Length == prefix.Length)
                throw new InvalidOperationException("Invalid class search path.");
            found.UnionWith(Classes(directory, revision, entry[prefix.Length..], runGit));
        }
        return found;
    }

    private static HashSet<string> Classes(string directory, string revision, string path,
        Func<string, string[], GitCli.GitResult> runGit)
    {
        var syntax = CSharpSyntaxTree.ParseText(Git(directory, runGit, "show", $"{revision}:{path}").TrimStart('\uFEFF'));
        if (syntax.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Invalid C# class evidence.");
        return syntax.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(declaration => declaration.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
    }

    private static string Git(string directory, Func<string, string[], GitCli.GitResult> runGit, params string[] args)
    {
        var result = runGit(directory, args);
        if (!result.Succeeded || result.DrainTimedOut) throw new InvalidOperationException("Git class evidence unavailable.");
        return result.Output;
    }
}
