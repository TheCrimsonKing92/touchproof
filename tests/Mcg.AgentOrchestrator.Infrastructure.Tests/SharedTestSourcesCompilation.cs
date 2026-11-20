using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class SharedTestSourcesCompilation
{
    private static readonly Lazy<CSharpCompilation> Compilation = new(CreateBase);

    internal static CSharpCompilation Base => Compilation.Value;

    private static CSharpCompilation CreateBase()
    {
        var testAssembly = typeof(SharedTestSourcesCompilation).Assembly;
        var references = CollectOutputReferences(testAssembly.Location);
        foreach (var path in Directory.EnumerateFiles(FindFrameworkReferences(testAssembly), "*.dll"))
            references.TryAdd(Path.GetFileNameWithoutExtension(path), path);

        var globals = CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.IO;
            global using System.Linq;
            global using System.Collections.Generic;
            global using System.Threading;
            global using System.Threading.Tasks;
            global using Xunit;
            """);
        // Match the test assembly's identity so its friend-assembly access applies to the scanned source.
        return CSharpCompilation.Create(testAssembly.GetName().Name!, [globals],
            references.Values.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static string FindFrameworkReferences(Assembly testAssembly)
    {
        var framework = new FrameworkName(testAssembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        var moniker = $"net{framework.Version.ToString(2)}";
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var packRoot = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", "packs", "Microsoft.NETCore.App.Ref"));
        var referenceDirectory = Directory.Exists(packRoot)
            ? Directory.EnumerateDirectories(packRoot)
                .Select(path => (Path: Path.Combine(path, "ref", moniker),
                    Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
                .Where(pack => pack.Version is not null)
                .OrderByDescending(pack => pack.Version)
                .Select(pack => pack.Path)
                .FirstOrDefault(Directory.Exists)
            : null;

        return referenceDirectory ?? throw new InvalidOperationException(
            $"Could not find the {moniker} framework reference directory under '{packRoot}'.");
    }

    private static Dictionary<string, string> CollectOutputReferences(string testAssemblyPath)
    {
        var outputDirectory = Path.GetDirectoryName(testAssemblyPath)!;
        var runtimeCoreName = typeof(object).Assembly.GetName().Name;
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFileNameWithoutExtension(testAssemblyPath)] = testAssemblyPath,
        };
        var pending = new Queue<string>();
        pending.Enqueue(testAssemblyPath);

        while (pending.TryDequeue(out var path))
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (StringComparer.OrdinalIgnoreCase.Equals(name, runtimeCoreName) || references.ContainsKey(name))
                    continue;

                var dependency = Path.Combine(outputDirectory, name + ".dll");
                if (File.Exists(dependency))
                {
                    references.Add(name, dependency);
                    pending.Enqueue(dependency);
                }
            }
        }

        return references;
    }
}
