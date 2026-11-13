using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ImpactReplayDeclaredGuardSelection
{
    internal static ImpactReplayDeclaredGuardReport Read(IEnumerable<string> changedFiles,
        ImpactReplayTestSourceIndex sources, IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        if (sources.Trees.Count == 0) return new([], "Declared guard source inventory is unavailable.");
        var changed = changedFiles.Select(path => path.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var membershipChanges = changed.Where(IsAssemblyInput).ToArray();
        var selected = new List<ImpactReplayDeclaredGuard>();
        var declarations = sources.Trees.SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>().Select(node => (Tree: tree, Node: node)))
            .ToLookup(item => ImpactReplayTestSourceIndex.ClassName(item.Node), StringComparer.Ordinal);
        if (classes.FirstOrDefault(item => !declarations.Contains(item.FullName)) is { } missing)
            return new([], $"Declared guard source inventory is incomplete: {missing.FullName} has no candidate declaration.");
        var globalAliases = sources.Trees.SelectMany(tree => tree.GetRoot().DescendantNodes()
            .OfType<UsingDirectiveSyntax>().Where(node => node.GlobalKeyword.ValueText == "global" && node.Alias is not null)).ToArray();
        foreach (var tree in sources.Trees)
        foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        foreach (var attribute in declaration.AttributeLists.SelectMany(list => list.Attributes))
        {
            // Match the existing structural declaration seam's qualified/suffixed marker syntax.
            var kind = MarkerKind(attribute.Name);
            if (kind is null && attribute.Name is IdentifierNameSyntax alias)
            {
                var aliases = declaration.Ancestors().SelectMany(node => node.ChildNodes().OfType<UsingDirectiveSyntax>())
                    .Concat(globalAliases).Where(node => node.Alias?.Name.Identifier.ValueText == alias.Identifier.ValueText).ToArray();
                var kinds = aliases.Select(node => node.Name is { } target ? MarkerKind(target) : null).Distinct().ToArray();
                if (kinds.Length > 1 && kinds.Any(value => value is not null))
                    return new(selected, $"Declared guard attribute alias is ambiguous: {alias} in {Display(tree.FilePath)}.");
                kind = kinds.SingleOrDefault();
            }
            if (kind is null) continue;
            var name = ImpactReplayTestSourceIndex.ClassName(declaration);
            if (attribute.ArgumentList is { Arguments.Count: > 0 })
                return new(selected, $"Declared guard metadata is malformed: {name} in {Display(tree.FilePath)}.");
            if (classes.Count(item => item.FullName == name) != 1)
                return new(selected, $"Declared guard class identity is missing or ambiguous: {name} in {Display(tree.FilePath)}.");
            // Resolve every part of this identity together. Equal fully qualified class names in
            // different assemblies cannot safely be flattened into the lane scanner's name set.
            var owners = sources.Projects?.Where(project => declarations[name].Any(part =>
                    project.Sources.Contains(part.Tree.FilePath, StringComparer.OrdinalIgnoreCase)))
                .Select(project => Display(project.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            if (owners.Length != 1 || declarations[name].Any(part => !sources.Projects!.Any(project =>
                    project.Sources.Contains(part.Tree.FilePath, StringComparer.OrdinalIgnoreCase))))
                return new(selected, $"Declared guard project ownership is missing or ambiguous: {name} in {Display(tree.FilePath)}.");
            // A topology declaration has no complete narrower scope: every changed repository path
            // invalidates it. Membership includes all source edits (even in an existing file), not
            // just added filenames. Removed and renamed paths need no candidate file to exist.
            // Without a complete assembly dependency closure, retain every declared membership guard
            // for source/build inputs, including inputs outside the test project's directory.
            var triggers = kind == "live-tree-structural" ? changed : membershipChanges;
            if (triggers.Length == 0) continue;
            selected.Add(new(owners[0], name, kind, Display(tree.FilePath), triggers,
                kind == "live-tree-structural" ? "Repository tree changed; declared topology input may change."
                    : "Source or build input changed; declared test-assembly membership may change."));
        }
        return new(selected.OrderBy(item => item.Project, StringComparer.Ordinal)
            .ThenBy(item => item.ClassName, StringComparer.Ordinal).ThenBy(item => item.DependencyKind, StringComparer.Ordinal)
            .ThenBy(item => item.DeclarationPath, StringComparer.Ordinal).ToArray());

        string Display(string path) => (sources.RepositoryRoot is { } root ? Path.GetRelativePath(root, path) : path).Replace('\\', '/');
    }

    private static string? MarkerKind(NameSyntax name) => (name switch
    {
        IdentifierNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => null
    }) switch
    {
        "LiveTreeStructural" or "LiveTreeStructuralAttribute" => "live-tree-structural",
        "TestAssemblyMembership" or "TestAssemblyMembershipAttribute" => "test-assembly-membership",
        _ => null
    };

    private static bool IsAssemblyInput(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" ||
        Path.GetFileName(path).Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("config/acceptance-manifest.json", StringComparison.OrdinalIgnoreCase);
}
