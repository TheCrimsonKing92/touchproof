using System.Collections.Immutable;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AssemblyScanningGuardTestClasses
{
    internal const string ReasonKind = "assembly-scan";

    // The source-reader cache key is independent of file paths. Bind these call sites to their
    // current trees and projects after a cache hit, including when only production files moved.
    internal sealed record ScanFile(string ContentHash, int TextLength, int[] CallPositions)
    {
        internal bool IsValid => ContentHash is { Length: 64 } && ContentHash.All(Uri.IsHexDigit) &&
            TextLength > 0 && CallPositions is { Length: > 0 } &&
            CallPositions.All(position => position >= 0 && position < TextLength);
    }

    internal sealed record SelectionReason(string ClassName, string Kind, string ScannedProject);

    internal static bool IsScanSite(InvocationExpressionSyntax call) =>
        (call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Get" } or
            IdentifierNameSyntax { Identifier.ValueText: "Get" }) &&
        call.ArgumentList.Arguments.Any(argument => ScannedType(argument) is not null);

    private static TypeSyntax? ScannedType(ArgumentSyntax argument) => argument.Expression is
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Assembly", Expression: TypeOfExpressionSyntax operand }
        ? operand.Type : null;

    internal static SelectionReason[] Match(ImpactReplayTestSourceIndex sources, IReadOnlyList<ScanFile> scanFiles,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, IReadOnlyList<string> changedFiles)
    {
        if (scanFiles.Count == 0 || sources.RepositoryRoot is not { } root || sources.Projects is not { } projects)
            return [];
        var changed = changedFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFullPath(path.Replace('\\', '/'), root)).Distinct(PathComparer).ToArray();
        if (changed.Length == 0) return [];

        var byHash = scanFiles.DistinctBy(file => file.ContentHash).ToDictionary(file => file.ContentHash);
        var lengths = scanFiles.Select(file => file.TextLength).ToHashSet();
        var trees = sources.Trees.ToDictionary(tree => Path.GetFullPath(tree.FilePath, root), PathComparer);
        var sites = trees.Where(pair => lengths.Contains(pair.Value.Length))
            .Select(pair => (pair.Key, File: byHash.GetValueOrDefault(SourceReaderFileFacts.Hash(pair.Value.GetText().ToString()))))
            .Where(pair => pair.File is not null).ToDictionary(pair => pair.Key, pair => pair.File!, PathComparer);
        var runnable = classes.Select(item => item.FullName).ToHashSet(StringComparer.Ordinal);
        var results = new HashSet<SelectionReason>();
        var graph = new ProjectGraph(root, projects, trees, changed);
        foreach (var project in projects)
        {
            var scanSources = project.Sources.Where(sites.ContainsKey).ToArray();
            if (scanSources.Length == 0) continue;
            var reachable = graph.References(project.Path);
            if (!reachable.Keys.Append(project.Path).Any(graph.HasChanges)) continue;
            var compilation = graph.Compile(project.Path);
            var scanned = new Dictionary<IAssemblySymbol, string>(SymbolEqualityComparer.Default);
            if (graph.HasChanges(project.Path)) scanned[compilation.Assembly] = RepositoryPath(root, project.Path);
            foreach (var reference in compilation.References.OfType<CompilationReference>())
            {
                var path = graph.PathOf(reference.Compilation);
                if (graph.HasChanges(path) && compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                    scanned[assembly] = RepositoryPath(root, path);
            }

            foreach (var path in scanSources)
            {
                var tree = trees[path];
                var model = compilation.GetSemanticModel(tree);
                foreach (var position in sites[path].CallPositions)
                {
                    var call = tree.GetRoot().FindToken(position).Parent?.AncestorsAndSelf()
                        .OfType<InvocationExpressionSyntax>().FirstOrDefault(node => node.SpanStart == position && IsScanSite(node));
                    var owner = call?.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                    if (call is null || owner is null) continue;
                    var name = ImpactReplayTestSourceIndex.ClassName(owner);
                    if (!runnable.Contains(name) || model.GetSymbolInfo(call).Symbol is not IMethodSymbol
                        { IsStatic: true, Name: "Get", ContainingType.Name: "DependencyIndexCache" }) continue;
                    foreach (var argument in call.ArgumentList.Arguments)
                    {
                        if (ScannedType(argument) is { } operand && model.GetTypeInfo(operand).Type is INamedTypeSymbol
                            { TypeKind: not TypeKind.Error, ContainingAssembly: { } assembly } &&
                            scanned.TryGetValue(assembly, out var scannedProject))
                            results.Add(new(name, ReasonKind, scannedProject));
                    }
                }
            }
        }
        return results.OrderBy(reason => reason.ClassName, StringComparer.Ordinal)
            .ThenBy(reason => reason.ScannedProject, StringComparer.Ordinal).ToArray();
    }

    private static StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    private static string RepositoryPath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    // Separate compilations preserve assembly identities and Roslyn's ambiguity and alias handling.
    // Their trees come from the same parsed-root cache as the test-source index.
    private sealed class ProjectGraph(string root, IReadOnlyList<ImpactReplayChangedSourceRouting.Project> tests,
        IReadOnlyDictionary<string, SyntaxTree> trees, IReadOnlyList<string> changed)
    {
        private readonly MetadataReference _runtime = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        private readonly Dictionary<string, Project> _projects = new(PathComparer);
        private readonly Dictionary<string, CSharpCompilation> _compilations = new(PathComparer);
        private readonly Dictionary<Compilation, string> _paths = new();
        private readonly Dictionary<string, bool> _changes = new(PathComparer);

        internal string PathOf(Compilation compilation) => _paths[compilation];

        internal bool HasChanges(string path)
        {
            if (_changes.TryGetValue(path, out var result)) return result;
            var project = Read(path);
            var directory = Path.GetDirectoryName(path)!;
            result = changed.Any(source => project.Sources.Contains(source) ||
                (!File.Exists(source) && BelongsToDirectory(source, directory)));
            return _changes[path] = result;
        }

        internal Dictionary<string, ImmutableArray<string>> References(string path)
        {
            var direct = Read(path).References;
            var references = new Dictionary<string, ImmutableArray<string>>(direct, PathComparer);
            var pending = new Queue<string>(direct.Keys);
            while (pending.TryDequeue(out var current))
            {
                if (PathComparer.Equals(path, current))
                    throw new InvalidDataException($"Circular project references at '{RepositoryPath(root, path)}'.");
                foreach (var referenced in Read(current).References.Keys)
                    if (references.TryAdd(referenced, default)) pending.Enqueue(referenced);
            }
            return references;
        }

        internal CSharpCompilation Compile(string path)
        {
            if (_compilations.TryGetValue(path, out var compilation)) return compilation;
            var project = Read(path);
            var references = References(path).Select(pair => Compile(pair.Key).ToMetadataReference(pair.Value));
            var sourceTrees = project.Sources.Where(File.Exists).Select(source =>
                trees.TryGetValue(source, out var tree) ? tree : CSharpSourceFileRootReader.Read(source).SyntaxTree);
            compilation = CSharpCompilation.Create(project.AssemblyName, sourceTrees, references.Prepend(_runtime),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            _paths.Add(compilation, path);
            return _compilations[path] = compilation;
        }

        private Project Read(string path)
        {
            if (_projects.TryGetValue(path, out var project)) return project;
            var document = XDocument.Load(path);
            var directory = Path.GetDirectoryName(path)!;
            var references = new Dictionary<string, ImmutableArray<string>>(PathComparer);
            foreach (var reference in document.Descendants().Where(node => node.Name.LocalName == "ProjectReference"))
            {
                if (reference.Attribute("Include")?.Value is not { Length: > 0 } include ||
                    string.Equals(Value(reference, "ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.GetFullPath(include.Replace('\\', '/'), directory);
                var relative = RepositoryPath(root, target);
                if (Path.IsPathRooted(relative) || relative.StartsWith("../", StringComparison.Ordinal) || !File.Exists(target)) continue;
                var aliases = Value(reference, "Aliases")?.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                references.TryAdd(target, aliases is { Length: > 0 } ? aliases.ToImmutableArray() : default);
            }
            var sources = tests.FirstOrDefault(test => PathComparer.Equals(test.Path, path))?.Sources ??
                FileSystemTestClassDeclarationReader.EnumerateProjectSourceFiles(directory);
            var assemblyName = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "AssemblyName")?.Value;
            return _projects[path] = new(assemblyName ?? Path.GetFileNameWithoutExtension(path),
                sources.ToHashSet(PathComparer), references);
        }

        private static string? Value(XElement element, string name) => element.Attribute(name)?.Value ??
            element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value.Trim();

        private static bool BelongsToDirectory(string source, string projectDirectory)
        {
            var relative = RepositoryPath(projectDirectory, source);
            if (Path.IsPathRooted(relative) || relative.StartsWith("../", StringComparison.Ordinal) ||
                relative.Split('/').Any(part => part is "bin" or "obj" or "artifacts" or ".scratch")) return false;
            for (var directory = Path.GetDirectoryName(source)!; !PathComparer.Equals(directory, projectDirectory);
                 directory = Path.GetDirectoryName(directory)!)
                if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.csproj").Any()) return false;
            return true;
        }

        private sealed record Project(string AssemblyName, HashSet<string> Sources,
            Dictionary<string, ImmutableArray<string>> References);
    }
}
