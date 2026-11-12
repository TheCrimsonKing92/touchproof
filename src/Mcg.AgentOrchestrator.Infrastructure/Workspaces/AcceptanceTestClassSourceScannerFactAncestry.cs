using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Cached facts describe one file only; visibility and ancestry belong to the complete scan.
internal static class AcceptanceTestClassSourceScannerFactAncestry
{
    internal sealed record AncestryUsing(string Target, string? Alias);
    internal sealed record AncestryScope(string Prefix, IReadOnlyList<AncestryUsing> Usings);
    internal sealed record AncestryClass(string FullName, string LookupName, string? BaseName,
        IReadOnlyList<string> Attributes, IReadOnlyList<AncestryScope> Scopes);
    internal sealed record AncestryFile(IReadOnlyList<AncestryClass> Classes,
        IReadOnlyList<AncestryUsing> GlobalUsings);

    internal static AncestryFile Extract(SyntaxNode root)
    {
        var globals = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(item => item.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
            .Select(ExtractUsing).ToArray();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(node =>
        {
            var fullName = AcceptanceTestClassSourceScanner.NameOf(node);
            var directives = node.Ancestors().Where(item => item is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
                .ToDictionary(item => NamespaceOf(item), item => (IReadOnlyList<AncestryUsing>)(item switch
                {
                    BaseNamespaceDeclarationSyntax ns => ns.Usings,
                    CompilationUnitSyntax unit => unit.Usings,
                    _ => default
                }).Where(item => !item.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                .Select(ExtractUsing).ToArray(), StringComparer.Ordinal);
            var lookupName = fullName.Replace('+', '.');
            var scopes = Prefixes(lookupName).Select(prefix => new AncestryScope(prefix,
                directives.GetValueOrDefault(prefix) ?? [])).ToArray();
            return new AncestryClass(fullName, lookupName, node.BaseList?.Types.FirstOrDefault()?.Type.ToString(),
                node.Members.OfType<MethodDeclarationSyntax>()
                    .SelectMany(method => method.AttributeLists.SelectMany(list => list.Attributes))
                    .Select(attribute => attribute.Name.ToString()).ToArray(), scopes);
        }).ToArray();
        return new(classes, globals);
    }

    internal static HashSet<string> Resolve(IEnumerable<AncestryFile?> files)
    {
        var facts = files.OfType<AncestryFile>().ToArray();
        var declarations = facts.SelectMany(file => file.Classes).ToArray();
        var byName = declarations.GroupBy(item => item.LookupName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var globals = facts.SelectMany(file => file.GlobalUsings).Distinct().ToArray();
        var namespaces = declarations.SelectMany(item => Prefixes(item.LookupName)).ToHashSet(StringComparer.Ordinal);
        var reachesRoot = new HashSet<string>(StringComparer.Ordinal);

        string ImportTarget(AncestryUsing directive, string prefix)
        {
            if (directive.Target.StartsWith("global::", StringComparison.Ordinal)) return directive.Target[8..];
            foreach (var parent in Prefixes(prefix))
            {
                var candidate = Join(parent, directive.Target);
                if (namespaces.Contains(candidate) || candidate == "Xunit" || IsRoot(candidate)) return candidate;
            }
            return directive.Target;
        }

        string[] Lookup(string written, AncestryClass site, bool attribute)
        {
            var names = attribute ? new[] { written, written + "Attribute" } : [written];
            string[] Find(IEnumerable<string> candidates) => candidates
                .Where(name => byName.ContainsKey(name) || IsRoot(name)).Distinct(StringComparer.Ordinal).ToArray();

            if (written.StartsWith("global::", StringComparison.Ordinal))
                return Find(names.Select(name => name[8..]));

            var separator = written.IndexOfAny(['.', ':']);
            var head = separator < 0 ? written : written[..separator];
            var tail = separator < 0 ? "" : written[separator..].Replace("::", ".", StringComparison.Ordinal);
            foreach (var scope in site.Scopes)
            {
                var aliases = scope.Usings.Concat(scope.Prefix.Length == 0 ? globals : [])
                    .Where(item => item.Alias == head).ToArray();
                if (aliases.Length > 0)
                {
                    var targets = aliases.Select(item => ImportTarget(item, scope.Prefix) + tail).ToArray();
                    return Find(attribute ? targets.Concat(targets.Select(name => name + "Attribute")) : targets);
                }
                var local = Find(names.Select(name => Join(scope.Prefix, name)));
                if (local.Length > 0) return local;
                var imports = scope.Usings.Concat(scope.Prefix.Length == 0 ? globals : [])
                    .Where(item => item.Alias is null);
                var imported = Find(imports.SelectMany(item => names.Select(name =>
                    Join(ImportTarget(item, scope.Prefix), name))));
                if (imported.Length > 0) return imported;
            }
            // Bare root bases are supported even when the supplied source set omits implicit usings.
            return names.Where(name => name is "FactAttribute" or "TheoryAttribute")
                .Select(name => "Xunit." + name).ToArray();
        }

        // A least fixed point proves ancestry without recursion or a depth limit. Cycles with
        // no path to a known root never enter the set, and ambiguous references prove nothing.
        var bases = byName.ToDictionary(pair => pair.Key, pair => pair.Value
            .Where(part => part.BaseName is not null)
            .Select(part => Lookup(part.BaseName!, part, attribute: false)).ToArray(), StringComparer.Ordinal);
        bool Proven(string name) => byName.ContainsKey(name)
            ? reachesRoot.Contains(name) : IsRoot(name);
        bool changed;
        do
        {
            changed = false;
            foreach (var (name, references) in bases)
            {
                if (reachesRoot.Contains(name)) continue;
                // Another partial declaration can list only interfaces; a rooted base on
                // any part establishes the merged class's ancestry.
                if (references.Any(matches => matches.Length == 1 && Proven(matches[0])))
                {
                    reachesRoot.Add(name);
                    changed = true;
                }
            }
        } while (changed);

        return declarations.Where(item => item.Attributes.Any(attribute =>
            Lookup(attribute, item, attribute: true) is { Length: 1 } matches && Proven(matches[0])))
            .Select(item => item.FullName).ToHashSet(StringComparer.Ordinal);
    }

    private static AncestryUsing ExtractUsing(UsingDirectiveSyntax node) =>
        new(node.Name?.ToString() ?? "", node.Alias?.Name.Identifier.ValueText);

    private static string NamespaceOf(SyntaxNode node) => string.Join(".", node.AncestorsAndSelf()
        .OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString()));

    private static IEnumerable<string> Prefixes(string name)
    {
        while (name.Length > 0)
        {
            yield return name;
            var separator = name.LastIndexOf('.');
            name = separator < 0 ? "" : name[..separator];
        }
        yield return "";
    }

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
    private static bool IsRoot(string name) => name is "Xunit.FactAttribute" or "Xunit.TheoryAttribute";
}
