using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One comparison owns its blob reads; no working-tree source participates in the proof.
internal sealed class TestClassExtractionEvidence
{
    internal sealed record ClassDeclaration(
        string Name, string ShortName, string BareName, string Identifier, string Project,
        IReadOnlySet<string> Methods, bool Ambiguous);
    internal sealed record ClassExtraction(
        string OldClass, string NewClass, string MergeBase, string CandidateSha);

    private sealed record Part(string Name, string ShortName, string BareName, string Identifier,
        string Project, bool Partial, HashSet<string> Methods);

    private readonly string directory;
    private readonly Func<string, string[], GitCli.GitResult> runGit;
    private readonly Dictionary<(string Revision, string Path), SyntaxTree> blobs = new();
    private readonly Dictionary<(string Revision, string Identifier), ClassDeclaration[]> classes = new();
    private readonly Dictionary<string, string[]> projects = new(StringComparer.Ordinal);
    internal string MergeBase { get; }
    internal string CandidateSha { get; }

    internal TestClassExtractionEvidence(string repositoryDirectory, string integrationBranch, string candidateSha,
        Func<string, string[], GitCli.GitResult>? runGit = null)
    {
        directory = repositoryDirectory;
        this.runGit = runGit ?? GitCli.Run;
        // Resolve both endpoints once so moving refs cannot mix revisions within this comparison.
        CandidateSha = Git("rev-parse", "--verify", candidateSha + "^{commit}").Trim();
        if (!string.Equals(CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Class evidence requires an exact candidate sha.");
        MergeBase = Git("merge-base", integrationBranch, CandidateSha).Trim();
    }

    internal IReadOnlyList<ClassDeclaration> Classes(string revision, string identifier)
    {
        if (!SyntaxFacts.IsValidIdentifier(identifier) && !SyntaxFacts.IsValidIdentifier("@" + identifier))
            return [];
        if (classes.TryGetValue((revision, identifier), out var found)) return found;
        var parts = Search(revision, identifier).Where(part => part.Identifier == identifier);
        return classes[(revision, identifier)] = parts.GroupBy(part => (part.Name, part.Project))
            .Select(group => new ClassDeclaration(group.Key.Name, group.First().ShortName,
                group.First().BareName, identifier, group.Key.Project,
                group.SelectMany(part => part.Methods).ToHashSet(StringComparer.Ordinal),
                group.Count() > 1 && group.Any(part => !part.Partial)))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    internal ClassExtraction? Read(ClassDeclaration oldClass)
    {
        var historical = Classes(MergeBase, oldClass.Identifier)
            .Where(item => item.Name == oldClass.Name && item.Project == oldClass.Project).ToArray();
        if (historical.Length != 1) return null;
        oldClass = historical[0];
        if (oldClass.Ambiguous || oldClass.Methods.Count == 0 ||
            Classes(CandidateSha, oldClass.Identifier).Any(item => item.Name == oldClass.Name)) return null;
        // Every complete destination contains this method. Search that smaller set, then combine
        // every partial declaration of each discovered type before checking the complete set.
        var firstMethod = oldClass.Methods.OrderBy(name => name, StringComparer.Ordinal).First();
        var holders = Search(CandidateSha, firstMethod, method: true).Where(part => part.Methods.Contains(firstMethod))
            .Select(part => part.Identifier).Distinct(StringComparer.Ordinal)
            .SelectMany(identifier => Classes(CandidateSha, identifier))
            .Where(item => oldClass.Methods.All(item.Methods.Contains)).ToArray();
        return holders.Length == 1 && !holders[0].Ambiguous
            ? new(oldClass.Name, holders[0].Name, MergeBase, CandidateSha) : null;
    }

    // Returns a selector rewrite only after canonical, historical identity and both lineage
    // endpoints are unique. Passing result rows never choose among destinations.
    internal (string Selector, string OldClass, string NewClass, string Kind)? Rewrite(
        string selector, IReadOnlyList<TestClassRenameEvidence.ClassRename> renames)
    {
        var head = selector.Split('(', 2)[0];
        var tokens = new[] { head, head.Contains('.') ? head[..head.LastIndexOf('.')] : head }
            .Distinct(StringComparer.Ordinal).ToArray();
        var origins = tokens.SelectMany(token => Classes(MergeBase, Identifier(token))
            .Where(item => Forms(item).Contains(token, StringComparer.Ordinal))
            .Select(item => (Token: token, Class: item))).ToArray();
        if (origins.Length != 1 || origins[0].Class.Ambiguous) return null;
        var (oldToken, oldClass) = origins[0];
        if (Classes(CandidateSha, oldClass.Identifier).Any(item =>
                Forms(item).Contains(oldToken, StringComparer.Ordinal))) return null;

        var links = new List<(string Name, string Kind)>();
        if (Read(oldClass) is { } extraction) links.Add((extraction.NewClass, "extraction"));
        foreach (var rename in renames.Where(item => item.OldClass == oldClass.Identifier))
        {
            var previous = Classes(MergeBase, rename.OldClass);
            var current = Classes(CandidateSha, rename.NewClass);
            if (previous.Count != 1 || previous[0].Ambiguous || current.Count != 1 || current[0].Ambiguous)
                return null;
            links.Add((current[0].Name, "rename"));
        }
        if (links.Select(link => link.Name).Distinct(StringComparer.Ordinal).Count() != 1) return null;
        var destination = links[0].Name;
        var declarations = Classes(CandidateSha, Identifier(destination)).Where(item => item.Name == destination).ToArray();
        if (declarations.Length != 1 || declarations[0].Ambiguous) return null;
        var target = declarations[0];
        var replacement = oldToken == oldClass.Name && oldClass.Name != oldClass.ShortName ? target.Name
            : oldToken == oldClass.ShortName ? target.ShortName : target.BareName;
        // A short destination must remain unique even when the other declaration has no lineage.
        if (Classes(CandidateSha, target.Identifier).Count(item =>
                Forms(item).Contains(replacement, StringComparer.Ordinal)) != 1) return null;
        return (replacement + selector[oldToken.Length..], oldClass.Name, target.Name,
            links.Any(link => link.Kind == "rename") ? "rename" : "extraction");
    }

    private static string[] Forms(ClassDeclaration item) => [item.Name, item.ShortName, item.BareName];
    private static string Identifier(string token)
    {
        var name = token[(Math.Max(token.LastIndexOf('.'), token.LastIndexOf('+')) + 1)..];
        var arity = name.IndexOf('`');
        return arity < 0 ? name : name[..arity];
    }

    private IEnumerable<Part> Search(string revision, string name, bool method = false)
    {
        // Escaped identifiers are a search superset, never an identity comparison.
        var result = runGit(directory, ["grep", "-l", "-z", "--full-name", "-F",
            "-e", name, "-e", @"\u", "-e", @"\U", revision, "--", "*.cs"]);
        if (result.DrainTimedOut) throw new InvalidOperationException("Git class evidence drain timed out.");
        if (result.ExitCode == 1 && result.ProcessStarted && result.Output.Length == 0 && result.Error.Length == 0)
            return [];
        if (!result.Succeeded) throw new InvalidOperationException("Git class evidence unavailable.");
        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).SelectMany(entry =>
        {
            var prefix = revision + ":";
            if (!entry.StartsWith(prefix, StringComparison.Ordinal) || entry.Length == prefix.Length)
                throw new InvalidOperationException("Invalid class search path.");
            return Parse(revision, entry[prefix.Length..], name, method);
        }).ToArray();
    }

    private Part[] Parse(string revision, string path, string name, bool method)
    {
        if (!blobs.TryGetValue((revision, path), out var syntax))
            blobs[(revision, path)] = syntax = CSharpSyntaxTree.ParseText(
                Git("show", $"{revision}:{path}").TrimStart('\uFEFF'));
        var root = syntax.GetRoot();
        // Search includes escaped-identifier spellings, so a hit may be only a string literal
        // in an unrelated file. Only declarations that could carry this class/method matter.
        // Disabled code is uncertain only when its identifier tokens include the searched name.
        if (root.DescendantTrivia().Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            .Any(trivia => SyntaxFactory.ParseTokens(trivia.ToFullString()).Any(token =>
                token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == name)))
            throw new InvalidOperationException($"Disabled C# class evidence for {name} in {path}.");
        var declarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(node => node is ClassDeclarationSyntax || node is RecordDeclarationSyntax record &&
                !record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
            .Where(node => method
                ? node.Members.OfType<MethodDeclarationSyntax>().Any(member => member.Identifier.ValueText == name)
                : node.Identifier.ValueText == name).ToArray();
        if (declarations.Length == 0) return [];
        if (declarations.Any(node => node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>()
                .Any(type => type.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error)) ||
            node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any(space =>
                space.Name.ContainsDiagnostics || space is NamespaceDeclarationSyntax block &&
                    (block.OpenBraceToken.IsMissing || block.CloseBraceToken.IsMissing) ||
                space is FileScopedNamespaceDeclarationSyntax file && file.SemicolonToken.IsMissing)))
            throw new InvalidOperationException($"Invalid C# class evidence for {name} in {path}.");
        var owner = Project(revision, path);
        return declarations
            .Select(node =>
            {
                if (node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Any(type => type.Modifiers.Any(SyntaxKind.FileKeyword)))
                    throw new InvalidOperationException($"File-local class evidence for {name} in {path} is indeterminate.");
                static string TypeName(TypeDeclarationSyntax type) => type.Identifier.ValueText +
                    (type.TypeParameterList is { Parameters.Count: > 0 } parameters ? "`" + parameters.Parameters.Count : "");
                var containing = node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Append(node);
                var shortName = string.Join("+", containing.Select(TypeName));
                var namespaces = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
                    .Select(item => string.Join(".", item.Name.DescendantTokens().Where(token =>
                        token.IsKind(SyntaxKind.IdentifierToken)).Select(token => token.ValueText)));
                return new Part(string.Join(".", namespaces.Append(shortName)), shortName, TypeName(node),
                    node.Identifier.ValueText, owner, node.Modifiers.Any(SyntaxKind.PartialKeyword),
                    node.Members.OfType<MethodDeclarationSyntax>().Select(method => method.Identifier.ValueText)
                        .ToHashSet(StringComparer.Ordinal));
            }).ToArray();
    }

    private string Project(string revision, string path)
    {
        if (!projects.TryGetValue(revision, out var paths))
            projects[revision] = paths = Git("ls-tree", "-r", "--name-only", "-z", revision)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(item => item.EndsWith(".csproj", StringComparison.Ordinal)).ToArray();
        var owners = paths.Select(project => (Path: project, Directory: project.Contains('/')
                ? project[..(project.LastIndexOf('/') + 1)] : ""))
            .Where(project => path.StartsWith(project.Directory, StringComparison.Ordinal)).ToArray();
        if (owners.Length == 0) return "";
        var nearest = owners.Where(project => project.Directory.Length == owners.Max(item => item.Directory.Length)).ToArray();
        if (nearest.Length != 1) throw new InvalidOperationException("Ambiguous class project identity.");
        return nearest[0].Path;
    }

    private string Git(params string[] args)
    {
        var result = runGit(directory, args);
        if (!result.Succeeded || result.DrainTimedOut)
            throw new InvalidOperationException("Git class evidence unavailable.");
        return result.Output;
    }
}
