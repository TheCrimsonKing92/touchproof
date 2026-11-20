using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class DependencyIndexFixtureAssembly
{
    public static byte[] Compile(string source, string assemblyName = "DependencyFixture",
        params byte[][] references)
    {
        var platformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source)],
            platformReferences.Concat(references.Select(image => MetadataReference.CreateFromImage(image))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, deterministic: true));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }
}
