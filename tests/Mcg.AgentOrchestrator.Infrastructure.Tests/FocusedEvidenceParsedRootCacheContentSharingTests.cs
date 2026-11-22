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

    [Fact]
    public void ParseCallback_ReadingIdenticalCopy_ReusesCompletedParse()
    {
        var (first, copy) = WriteIdenticalPair();
        var callbackCalls = 0;

        var file = CSharpSourceFileRootReader.ReadFile(_store, first, () =>
        {
            callbackCalls++;
            Assert.Equal(copy, ReadFile(copy).Root.SyntaxTree.FilePath);
        });

        Assert.Equal(first, file.Root.SyntaxTree.FilePath);
        Assert.Equal(1, callbackCalls);
        Assert.Equal(0, _parses);
        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public void ParseCallback_ColdReadFails_RetryParsesAgain()
    {
        var (path, _) = WriteIdenticalPair();
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);
        var failure = new InvalidOperationException("Parse notification failed.");

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            CSharpSourceFileRootReader.ReadFile(_store, path, () => throw failure)));
        Assert.Equal(0, _store.Count);
        Assert.Equal(1, parses.CountFor(path));

        var retried = ReadFile(path);

        Assert.Equal(path, retried.Root.SyntaxTree.FilePath);
        Assert.Same(retried, ReadFile(path));
        Assert.Equal(1, _parses);
        Assert.Equal(2, parses.CountFor(path));
        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public async Task ParseCallback_ThrowingWhileCopyIsRead_OnlyFailsOwningRead()
    {
        var (first, copy) = WriteIdenticalPair();
        var failure = new InvalidOperationException("Parse notification failed.");
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        var timeout = TimeSpan.FromSeconds(30);
        var firstRead = Task.Run(() => Record.Exception(() =>
            CSharpSourceFileRootReader.ReadFile(_store, first, () =>
            {
                callbackEntered.SetResult();
                releaseCallback.Wait();
                throw failure;
            })));
        Task<FocusedEvidenceParsedRootCache.ParsedFile>? copyRead = null;
        try
        {
            await WaitFor(callbackEntered.Task, timeout, "the parse callback to enter");
            copyRead = Task.Run(() => ReadFile(copy));
            await WaitFor(copyRead, timeout, "the copy read to finish while the callback is blocked");
            var file = await copyRead;
            Assert.Equal(copy, file.Root.SyntaxTree.FilePath);
        }
        finally
        {
            releaseCallback.Set();
            await WaitFor(firstRead, timeout, "the callback-owning read to finish during cleanup");
            if (copyRead is not null)
                await WaitFor(copyRead, timeout, "the copy read to finish during cleanup");
        }

        Assert.Same(failure, await firstRead);
        Assert.Equal(0, _parses);
        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public void EvictedContent_RepublishedAtAnotherPath_ReplacesLiveWrapper()
    {
        var (original, copy) = WriteIdenticalPair();
        var store = new FocusedEvidenceParsedRootCache.Store(2);
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);
        var old = CSharpSourceFileRootReader.ReadFile(store, original);

        // Rewriting one path evicts content while the original path entry stays indexed.
        File.WriteAllText(copy, "class FirstReplacement {}");
        CSharpSourceFileRootReader.ReadFile(store, copy);
        File.WriteAllText(copy, "class SecondLongerReplacement {}");
        CSharpSourceFileRootReader.ReadFile(store, copy);

        File.Copy(original, copy, overwrite: true);
        var republished = CSharpSourceFileRootReader.ReadFile(store, copy);
        var current = CSharpSourceFileRootReader.ReadFile(store, original);

        Assert.Equal(1, parses.CountFor(original));
        Assert.Equal(3, parses.CountFor(copy));
        Assert.NotSame(old.Root, current.Root);
        Assert.True(republished.Names.Equals(current.Names));
        Assert.Equal(original, current.Root.SyntaxTree.FilePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EarlyEof_ReturnsAvailableTextWithoutCaching(bool hasInitialMetadata)
    {
        const string availableSource = "class Available {}";
        const string originalSource = availableSource + " class Truncated {}";
        var path = Path.Combine(_root, "Truncated.cs");
        if (hasInitialMetadata) File.WriteAllText(path, originalSource);

        var opens = 0;
        var store = new FocusedEvidenceParsedRootCache.Store(2, sourcePath =>
        {
            Assert.Equal(path, sourcePath);
            if (++opens > 1) return File.OpenRead(sourcePath);
            File.WriteAllText(sourcePath, availableSource);
            return new EarlyEofStream(Encoding.UTF8.GetBytes(availableSource), Encoding.UTF8.GetByteCount(originalSource));
        });

        var first = CSharpSourceFileRootReader.ReadFile(store, path, () => _parses++);
        Assert.Equal(availableSource, first.Root.ToFullString());
        Assert.Equal(path, first.Root.SyntaxTree.FilePath);
        Assert.Equal(0, store.Count);

        var retry = CSharpSourceFileRootReader.ReadFile(store, path, () => _parses++);
        Assert.Equal(availableSource, retry.Root.ToFullString());
        Assert.Equal(2, opens);
        Assert.Equal(2, _parses);
        Assert.Equal(1, store.Count);
    }

    private sealed class EarlyEofStream(byte[] bytes, long reportedLength) : MemoryStream(bytes)
    {
        public override long Length => reportedLength;
    }

    private static async Task WaitFor(Task task, TimeSpan timeout, string outcome)
    {
        try { await task.WaitAsync(timeout); }
        catch (TimeoutException ex) { throw new TimeoutException($"Timed out waiting for {outcome}.", ex); }
    }

    private FocusedEvidenceParsedRootCache.ParsedFile ReadFile(string path) =>
        CSharpSourceFileRootReader.ReadFile(_store, path, () => _parses++);

    private (string First, string Copy) WriteIdenticalPair()
    {
        var first = Path.Combine(_root, "First.cs");
        var copy = Path.Combine(_root, "Copy.cs");
        File.WriteAllText(first, "class Shared {}");
        File.Copy(first, copy);
        return (first, copy);
    }

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
