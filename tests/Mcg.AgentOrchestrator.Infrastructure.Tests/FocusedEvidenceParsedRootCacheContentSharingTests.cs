using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: isolated cache stores and temporary roots; the verified checkout is read-only.
public sealed class FocusedEvidenceParsedRootCacheContentSharingTests : IDisposable
{
    private const int FileCount = 40;
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    private readonly FocusedEvidenceParsedRootCache.Store _store = new(FocusedEvidenceParsedRootCache.Capacity);
    private int _parses;

    [Fact]
    public void TwoRoots_SameFortyFiles_ParseEachContentOnceAndKeepRequestedPaths()
    {
        var (first, second) = WriteTwoRoots();
        foreach (var path in first.Concat(second))
            Assert.Equal(path, ReadFile(path).Root.SyntaxTree.FilePath);

        Assert.Equal(FileCount, _parses);
        Assert.Equal(FileCount, _store.Count);
    }

    [Fact]
    public void SecondRootEdit_ReparsesOnlyChangedCopyAndKeepsEachText()
    {
        var (first, second) = WriteTwoRoots();
        foreach (var path in first.Concat(second))
            ReadFile(path);
        Assert.Equal(FileCount, _parses);

        const int index = 7;
        File.WriteAllText(second[index], "namespace ContentSharing; public class EditedSecondCopy {}");

        Assert.Equal(File.ReadAllText(first[index]), ReadFile(first[index]).Root.ToFullString());
        Assert.Equal(File.ReadAllText(second[index]), ReadFile(second[index]).Root.ToFullString());
        Assert.Equal(FileCount + 1, _parses);
    }

    [Fact]
    public void CoreProjectCopy_ParsesDistinctContentsNotPaths()
    {
        var source = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.Core");
        var files = EnumerateSourceFiles(source).ToArray();
        Assert.NotEmpty(files);

        var copies = new List<string>(files.Length);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            hashes.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            var copy = Path.Combine(_root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
            copies.Add(copy);
        }

        foreach (var path in files.Concat(copies))
            ReadFile(path);

        Assert.Equal(hashes.Count, _parses);
        Assert.True(_parses < files.Length + copies.Count);
    }

    [Fact]
    public void SecondRootTrees_CompilationReportsSecondRootPath()
    {
        var (first, second) = WriteTwoRoots();
        foreach (var path in first)
            ReadFile(path);

        var trees = second.Select(path => ReadFile(path).Root.SyntaxTree).ToArray();
        Assert.Equal(FileCount, _parses);
        var compilation = CSharpCompilation.Create("ContentSharing", trees);
        var declaration = compilation.GetTypeByMetadataName("ContentSharing.Type07");
        Assert.NotNull(declaration);
        var location = Assert.Single(declaration.Locations);
        Assert.Equal(second[7], location.GetLineSpan().Path);
        Assert.Equal(second[7], location.SourceTree!.FilePath);
    }

    [Fact]
    public void IdenticalDecodedTextWithDifferentByteOrderMarks_ParsesDistinctBytes()
    {
        const string source = "class Café {}";
        var first = Path.Combine(_root, "Utf8.cs");
        var second = Path.Combine(_root, "Utf16.cs");
        File.WriteAllText(first, source, Encoding.UTF8);
        File.WriteAllText(second, source, Encoding.Unicode);

        Assert.Equal(source, ReadFile(first).Root.ToFullString());
        Assert.Equal(source, ReadFile(second).Root.ToFullString());
        Assert.Equal(2, _parses);
        Assert.Equal(2, _store.Count);
    }

    private FocusedEvidenceParsedRootCache.ParsedFile ReadFile(string path) =>
        CSharpSourceFileRootReader.ReadFile(_store, path, () => _parses++);

    private (string[] First, string[] Second) WriteTwoRoots()
    {
        var firstRoot = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        var first = new string[FileCount];
        var second = new string[FileCount];
        for (var index = 0; index < FileCount; index++)
        {
            var type = FormattableString.Invariant($"Type{index:00}");
            var filename = type + ".cs";
            first[index] = Path.Combine(firstRoot, filename);
            second[index] = Path.Combine(secondRoot, filename);
            File.WriteAllText(first[index], $"namespace ContentSharing; public class {type} {{}}");
            File.Copy(first[index], second[index]);
        }
        return (first, second);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj") continue;
            foreach (var file in EnumerateSourceFiles(child))
                yield return file;
        }
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
