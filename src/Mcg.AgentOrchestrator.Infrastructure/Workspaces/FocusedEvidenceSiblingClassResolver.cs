using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DeclaredType = Mcg.AgentOrchestrator.Infrastructure.FocusedEvidenceDeclarationFacts.DeclaredType;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceSiblingClassResolver
{
    internal static IReadOnlyList<string> ResolveSiblingTestClassNames(
        string worktreePath,
        string project,
        string requestedClass)
    {
        try
        {
            if (!TryGetProjectDirectory(worktreePath, project, out var projectDirectory))
            {
                return [];
            }

            var store = FocusedEvidenceDeclarationFactStore.Current;
            return FocusedEvidenceSiblingClassMemo.Shared.GetOrCompute(worktreePath, project, requestedClass,
                projectDirectory, () => ResolveFromFacts(worktreePath, projectDirectory, requestedClass, store));
        }
        catch
        {
            // Keep failures outside the memo so a later call can retry.
            return [];
        }
    }

    internal static IReadOnlyList<string> ResolveSiblingTestClassNames(
        string worktreePath,
        string project,
        string requestedClass,
        FocusedEvidenceSiblingClassMemo memo,
        Func<string, string> readText,
        Func<string, string, SyntaxTree> parse)
    {
        try
        {
            if (!TryGetProjectDirectory(worktreePath, project, out var projectDirectory))
            {
                return ResolveSiblingTestClassNamesCore(worktreePath, project, requestedClass, readText, parse);
            }

            return memo.GetOrCompute(worktreePath, project, requestedClass, projectDirectory,
                () => ResolveSiblingTestClassNamesCore(worktreePath, project, requestedClass, readText, parse));
        }
        catch
        {
            // Keep failures outside the memo so a later call can retry.
            return [];
        }
    }

    internal static IReadOnlyList<string> ResolveSiblingTestClassNames(
        string worktreePath,
        string project,
        string requestedClass,
        Func<string, string> readText,
        Func<string, string, SyntaxTree> parse,
        bool parseEveryFamilyFile = false)
    {
        try
        {
            return ResolveSiblingTestClassNamesCore(
                worktreePath, project, requestedClass, readText, parse, parseEveryFamilyFile);
        }
        catch
        {
            // Sibling expansion is best-effort and may only widen the original selection.
            return [];
        }
    }

    private static IReadOnlyList<string> ResolveFromFacts(
        string worktreePath,
        string projectDirectory,
        string requestedClass,
        FocusedEvidenceDeclarationFactStore store)
    {
        var normalizedRequestedClass = requestedClass.Trim();
        if (normalizedRequestedClass.Length == 0)
        {
            return [];
        }

        var requestedSimpleName = normalizedRequestedClass.Split('.').Last();
        var sourcePaths = DiscoverSourcePaths(projectDirectory);
        var sourceTexts = new string[sourcePaths.Length];
        var fileFacts = new FocusedEvidenceDeclarationFacts?[sourcePaths.Length];
        var requestedDeclarations = new List<(int Index, DeclaredType Declaration)>();

        for (var index = 0; index < sourcePaths.Length; index++)
        {
            var source = File.ReadAllText(sourcePaths[index]);
            sourceTexts[index] = source;
            if (!source.Contains(requestedSimpleName, StringComparison.Ordinal))
            {
                continue;
            }

            var facts = ReadCandidateFacts(store, worktreePath, sourcePaths[index], source);
            if (facts.ContainsErrorDiagnostics)
            {
                return [];
            }

            fileFacts[index] = facts;
            requestedDeclarations.AddRange(facts.Types
                .Where(declaration => !declaration.Shape.IsNested && TypeMatches(declaration, normalizedRequestedClass))
                .Select(declaration => (index, declaration)));
        }

        if (requestedDeclarations.Count != 1)
        {
            return [];
        }

        var (declaringIndex, requestedDeclaration) = requestedDeclarations[0];
        var abstractClasses = fileFacts[declaringIndex]!.Types
            .Where(declaration => IsTopLevelClass(declaration) && declaration.Shape.IsAbstract)
            .Select(declaration => declaration.SimpleName)
            .ToHashSet(StringComparer.Ordinal);
        var familyBaseNames = requestedDeclaration.Shape.BaseTypeNames.Select(SimpleTypeName)
            .Where(abstractClasses.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var siblingFileIndices = new List<int> { declaringIndex };
        if (familyBaseNames.Count > 0)
        {
            ExpandFamilyFromFacts(store, worktreePath, sourcePaths, sourceTexts, fileFacts, familyBaseNames);
            for (var index = 0; index < sourcePaths.Length; index++)
            {
                if (fileFacts[index] is { ContainsErrorDiagnostics: false } &&
                    !sourcePaths[index].Equals(sourcePaths[declaringIndex], StringComparison.OrdinalIgnoreCase))
                {
                    siblingFileIndices.Add(index);
                }
            }
        }

        var declarations = siblingFileIndices.SelectMany(index =>
            fileFacts[index]!.Types.Select(declaration => (Index: index, Declaration: declaration))).ToArray();
        var classDeclarationsByName = declarations.Select(item => item.Declaration)
            .Where(IsTopLevelClass)
            .GroupBy(declaration => declaration.SimpleName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        return declarations
            .Where(item => IsQualifyingTestClass(item.Declaration))
            .Where(item => !TypeMatches(item.Declaration, normalizedRequestedClass))
            .Where(item => item.Index == declaringIndex ||
                DerivesFromFamily(item.Declaration, familyBaseNames, classDeclarationsByName, []))
            .Select(item => item.Declaration.SimpleName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void ExpandFamilyFromFacts(
        FocusedEvidenceDeclarationFactStore store,
        string worktreePath,
        string[] sourcePaths,
        string[] sourceTexts,
        FocusedEvidenceDeclarationFacts?[] fileFacts,
        HashSet<string> familyBaseNames)
    {
        var familyNames = new HashSet<string>(familyBaseNames, StringComparer.Ordinal);
        int previousNameCount;
        do
        {
            previousNameCount = familyNames.Count;
            foreach (var facts in fileFacts)
            {
                if (facts is { ContainsErrorDiagnostics: false })
                {
                    AddFamilyNames(facts, familyNames);
                }
            }

            for (var index = 0; index < sourcePaths.Length; index++)
            {
                if (fileFacts[index] is not null || !familyNames.Any(name =>
                        sourceTexts[index].Contains(name, StringComparison.Ordinal)))
                {
                    continue;
                }

                var facts = ReadCandidateFacts(store, worktreePath, sourcePaths[index], sourceTexts[index]);
                fileFacts[index] = facts;
                if (!facts.ContainsErrorDiagnostics)
                {
                    AddFamilyNames(facts, familyNames);
                }
            }
        }
        while (familyNames.Count != previousNameCount);
    }

    private static FocusedEvidenceDeclarationFacts ReadCandidateFacts(
        FocusedEvidenceDeclarationFactStore store, string worktreePath, string path, string source)
    {
        try
        {
            return store.ReadSnapshot(path, worktreePath, source);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var root = CSharpSyntaxTree.ParseText(source, path: path).GetCompilationUnitRoot();
            FocusedEvidenceParsedRootCache.RecordParse(path);
            return FocusedEvidenceDeclarationFacts.Extract(root);
        }
    }

    private static void AddFamilyNames(FocusedEvidenceDeclarationFacts facts, HashSet<string> familyNames)
    {
        foreach (var declaration in facts.Types)
        {
            if (!declaration.Shape.IsNested && declaration.Shape.BaseTypeNames.Select(SimpleTypeName).Any(familyNames.Contains))
            {
                familyNames.Add(declaration.SimpleName);
            }
        }
    }

    private static bool IsTopLevelClass(DeclaredType declaration) =>
        !declaration.Shape.IsNested && !declaration.Shape.IsRecord && declaration.IsClassOrNonStructRecord;

    private static bool IsQualifyingTestClass(DeclaredType declaration) =>
        !declaration.Shape.IsNested && declaration.Shape.IsPublic &&
        !declaration.Shape.IsAbstract && !declaration.Shape.IsStatic && declaration.IsClassOrNonStructRecord &&
        declaration.Shape.Methods.Any(method => method.Attributes.Any(attribute => IsTestAttribute(attribute.Name)));

    private static bool TypeMatches(DeclaredType declaration, string requestedClass) =>
        declaration.SimpleName.Equals(requestedClass, StringComparison.Ordinal) ||
        declaration.QualifiedName.Equals(requestedClass, StringComparison.Ordinal) ||
        declaration.QualifiedName.EndsWith('.' + requestedClass, StringComparison.Ordinal);

    private static bool DerivesFromFamily(
        DeclaredType declaration,
        IReadOnlySet<string> familyBaseNames,
        IReadOnlyDictionary<string, DeclaredType[]> classDeclarationsByName,
        HashSet<string> visited)
    {
        foreach (var baseName in declaration.Shape.BaseTypeNames.Select(SimpleTypeName))
        {
            if (familyBaseNames.Contains(baseName))
            {
                return true;
            }

            if (visited.Add(baseName) && classDeclarationsByName.TryGetValue(baseName, out var baseDeclarations) &&
                baseDeclarations.Length == 1 &&
                DerivesFromFamily(baseDeclarations[0], familyBaseNames, classDeclarationsByName, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ResolveSiblingTestClassNamesCore(
        string worktreePath,
        string project,
        string requestedClass,
        Func<string, string> readText,
        Func<string, string, SyntaxTree> parse,
        bool parseEveryFamilyFile = false)
    {
        var normalizedRequestedClass = requestedClass.Trim();
        if (normalizedRequestedClass.Length == 0)
        {
            return [];
        }

        if (!TryGetProjectDirectory(worktreePath, project, out var projectDirectory))
        {
            return [];
        }

        var requestedSimpleName = normalizedRequestedClass.Split('.').Last();
        var sourcePaths = DiscoverSourcePaths(projectDirectory);
        var requestedDeclarations = new List<TypeDeclarationSyntax>();
        var sourceTexts = new string[sourcePaths.Length];
        var parsedRoots = new CompilationUnitSyntax?[sourcePaths.Length];
        var attempted = new bool[sourcePaths.Length];

        // Deliberately mirrors GoalAcceptanceVerifier's source-discovery mechanism without
        // changing that verifier: resolve one declaration, then inspect its file and any
        // concrete test descendants of an abstract base declared beside it.
        for (var index = 0; index < sourcePaths.Length; index++)
        {
            var path = sourcePaths[index];
            var source = readText(path);
            sourceTexts[index] = source;
            if (!source.Contains(requestedSimpleName, StringComparison.Ordinal))
            {
                continue;
            }

            var syntaxTree = parse(path, source);
            if (syntaxTree.GetDiagnostics().Any(diagnostic =>
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            {
                return [];
            }

            var root = syntaxTree.GetCompilationUnitRoot();
            parsedRoots[index] = root;
            attempted[index] = true;
            requestedDeclarations.AddRange(root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(declaration => IsTopLevel(declaration) && TypeMatches(declaration, normalizedRequestedClass)));
        }

        if (requestedDeclarations.Count != 1)
        {
            return [];
        }

        var requestedDeclaration = requestedDeclarations[0];
        var declaringRoot = requestedDeclaration.SyntaxTree.GetCompilationUnitRoot();
        var familyBaseNames = requestedDeclaration.BaseList?.Types
            .Select(SimpleTypeName)
            .Where(baseName => declaringRoot.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Any(declaration =>
                    IsTopLevel(declaration) &&
                    declaration.Identifier.ValueText.Equals(baseName, StringComparison.Ordinal) &&
                    declaration.Modifiers.Any(modifier =>
                        modifier.RawKind == (int)SyntaxKind.AbstractKeyword)))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var sourceRoots = new List<CompilationUnitSyntax> { declaringRoot };
        if (familyBaseNames.Count > 0)
        {
            if (parseEveryFamilyFile)
            {
                for (var index = 0; index < sourcePaths.Length; index++)
                {
                    if (sourcePaths[index].Equals(declaringRoot.SyntaxTree.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var syntaxTree = parse(sourcePaths[index], sourceTexts[index]);
                    parsedRoots[index] = syntaxTree.GetDiagnostics().Any(diagnostic =>
                        diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                        ? null : syntaxTree.GetCompilationUnitRoot();
                }
            }
            else
            {
                var familyNames = new HashSet<string>(familyBaseNames, StringComparer.Ordinal);
                int previousNameCount;
                do
                {
                    previousNameCount = familyNames.Count;
                    // Include first-pass roots and revisit declarations when new base names appear.
                    foreach (var root in parsedRoots)
                    {
                        if (root is not null)
                        {
                            AddFamilyNames(root, familyNames);
                        }
                    }

                    for (var index = 0; index < sourcePaths.Length; index++)
                    {
                        if (attempted[index] || !familyNames.Any(name =>
                                sourceTexts[index].Contains(name, StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        attempted[index] = true;
                        var syntaxTree = parse(sourcePaths[index], sourceTexts[index]);
                        if (!syntaxTree.GetDiagnostics().Any(diagnostic =>
                                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
                        {
                            var root = syntaxTree.GetCompilationUnitRoot();
                            parsedRoots[index] = root;
                            AddFamilyNames(root, familyNames);
                        }
                    }
                }
                while (familyNames.Count != previousNameCount);
            }

            // Discovery order must not change the downstream declaration inventory.
            for (var index = 0; index < sourcePaths.Length; index++)
            {
                if (parsedRoots[index] is { } root &&
                    !sourcePaths[index].Equals(declaringRoot.SyntaxTree.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    sourceRoots.Add(root);
                }
            }
        }

        var classDeclarationsByName = sourceRoots
            .SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Where(IsTopLevel)
            .GroupBy(declaration => declaration.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        return sourceRoots
            .SelectMany(root => root.DescendantNodes())
            .OfType<TypeDeclarationSyntax>()
            .Where(IsQualifyingTestClass)
            .Where(declaration => !TypeMatches(declaration, normalizedRequestedClass))
            .Where(declaration =>
                declaration.SyntaxTree == declaringRoot.SyntaxTree ||
                DerivesFromFamily(declaration, familyBaseNames, classDeclarationsByName, []))
            .Select(declaration => declaration.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddFamilyNames(CompilationUnitSyntax root, HashSet<string> familyNames)
    {
        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(IsTopLevel))
        {
            if (declaration.BaseList?.Types.Select(SimpleTypeName).Any(familyNames.Contains) == true)
            {
                familyNames.Add(declaration.Identifier.ValueText);
            }
        }
    }

    private static string[] DiscoverSourcePaths(string projectDirectory)
    {
        var nestedProjectDirectories = Directory
            .EnumerateFiles(projectDirectory, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                !path.Equals(projectDirectory, StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !HasGeneratedPathSegment(path) &&
                !nestedProjectDirectories.Any(directory => IsWithinDirectory(path, directory)))
            .ToArray();
    }

    private static bool TryGetProjectDirectory(string worktreePath, string project, out string projectDirectory)
    {
        projectDirectory = string.Empty;
        var rootPath = Path.GetFullPath(worktreePath);
        var projectPath = Path.GetFullPath(Path.Combine(
            rootPath,
            project.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithinDirectory(projectPath, rootPath) || !File.Exists(projectPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        projectDirectory = directory;
        return true;
    }

    private static bool DerivesFromFamily(
        TypeDeclarationSyntax declaration,
        IReadOnlySet<string> familyBaseNames,
        IReadOnlyDictionary<string, ClassDeclarationSyntax[]> classDeclarationsByName,
        HashSet<string> visited)
    {
        foreach (var baseName in declaration.BaseList?.Types.Select(SimpleTypeName) ?? [])
        {
            if (familyBaseNames.Contains(baseName))
            {
                return true;
            }

            if (!visited.Add(baseName) ||
                !classDeclarationsByName.TryGetValue(baseName, out var baseDeclarations) ||
                baseDeclarations.Length != 1)
            {
                continue;
            }

            if (DerivesFromFamily(baseDeclarations[0], familyBaseNames, classDeclarationsByName, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static string SimpleTypeName(BaseTypeSyntax baseType) => SimpleTypeName(baseType.Type.ToString());

    private static string SimpleTypeName(string name)
    {
        var genericStart = name.IndexOf('<');
        if (genericStart >= 0)
        {
            name = name[..genericStart];
        }

        return name.Split('.').Last().Split("::").Last();
    }

    private static bool IsQualifyingTestClass(TypeDeclarationSyntax declaration)
    {
        if (!IsTopLevel(declaration) ||
            declaration.Modifiers.All(modifier => modifier.RawKind != (int)SyntaxKind.PublicKeyword) ||
            declaration.Modifiers.Any(modifier =>
                modifier.RawKind == (int)SyntaxKind.AbstractKeyword ||
                modifier.RawKind == (int)SyntaxKind.StaticKeyword) ||
            declaration is not ClassDeclarationSyntax &&
            declaration is not RecordDeclarationSyntax)
        {
            return false;
        }

        if (declaration is RecordDeclarationSyntax record &&
            record.ClassOrStructKeyword.RawKind == (int)SyntaxKind.StructKeyword)
        {
            return false;
        }

        return declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(method => method.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute => IsTestAttribute(attribute.Name.ToString())));
    }

    private static bool IsTestAttribute(string attributeName)
    {
        var simpleName = attributeName.Split('.').Last().Split("::").Last();
        if (simpleName.EndsWith("Attribute", StringComparison.Ordinal))
        {
            simpleName = simpleName[..^"Attribute".Length];
        }

        return simpleName.EndsWith("Fact", StringComparison.Ordinal) ||
            simpleName.EndsWith("Theory", StringComparison.Ordinal);
    }

    private static bool TypeMatches(TypeDeclarationSyntax declaration, string requestedClass)
    {
        if (declaration.Identifier.ValueText.Equals(requestedClass, StringComparison.Ordinal))
        {
            return true;
        }

        var namespaceNames = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Name.ToString());
        var qualifiedName = string.Join('.', namespaceNames.Append(declaration.Identifier.ValueText));
        return qualifiedName.Equals(requestedClass, StringComparison.Ordinal) ||
            qualifiedName.EndsWith('.' + requestedClass, StringComparison.Ordinal);
    }

    private static bool IsTopLevel(TypeDeclarationSyntax declaration) =>
        !declaration.Ancestors().OfType<TypeDeclarationSyntax>().Any();

    private static bool HasGeneratedPathSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static bool IsWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
