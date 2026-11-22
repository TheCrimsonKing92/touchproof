using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: unique temporary roots, scoped parse counts, and no resets of the shared cache.
public sealed class FocusedEvidenceParsedRootCacheTests
{
    [Fact]
    public void DifferentClassLookups_UnchangedProject_ParseEachFileOnce()
    {
        using var tree = new SourceTree();
        var alpha = tree.Write("Alpha.cs", "class Alpha {}");
        var beta = tree.Write("Beta.cs", "class Beta {}");
        var empty = tree.Write("Empty.cs", "enum Ignored { Value }");
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);

        Assert.Equal(new[] { alpha }, tree.Find("Alpha"));
        Assert.Equal(new[] { beta }, tree.Find("Beta"));
        var root = FocusedEvidenceRequestResolver.ParseCSharpRoot(alpha);
        Assert.Same(root, FocusedEvidenceRequestResolver.ParseCSharpRoot(alpha));
        var alias = Path.Combine(Path.GetDirectoryName(alpha)!, ".", Path.GetFileName(alpha));
        Assert.Equal(alias, FocusedEvidenceRequestResolver.ParseCSharpRoot(alias).SyntaxTree.FilePath);
        Assert.Equal(3, parses.Total);
        foreach (var path in new[] { alpha, beta, empty }) Assert.Equal(1, parses.CountFor(path));
    }

    [Fact]
    public void ContentAndSizeChange_ReparsesOnlyChangedFileAndRefreshesNames()
    {
        using var tree = new SourceTree();
        var alpha = tree.Write("Alpha.cs", "class Alpha {}");
        var beta = tree.Write("Beta.cs", "class Beta {}");
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        Assert.Equal(new[] { alpha }, tree.Find("Alpha"));
        var oldTimestamp = File.GetLastWriteTimeUtc(beta);
        var oldLength = new FileInfo(beta).Length;

        tree.Write("Beta.cs", "class GammaWithLongerName {}");
        File.SetLastWriteTimeUtc(beta, oldTimestamp);
        Assert.Equal(oldTimestamp, File.GetLastWriteTimeUtc(beta));
        Assert.NotEqual(oldLength, new FileInfo(beta).Length);

        Assert.Equal(new[] { beta }, tree.Find("GammaWithLongerName"));
        Assert.Empty(tree.Find("Beta"));
        Assert.Contains("GammaWithLongerName", FocusedEvidenceRequestResolver.ParseCSharpRoot(beta).ToFullString());
        Assert.Equal(1, parses.CountFor(alpha));
        Assert.Equal(2, parses.CountFor(beta));
        Assert.Equal(3, parses.Total);
    }

    [Fact]
    public void TimestampOnlyChange_ReparsesOnlyChangedFileAndKeepsNames()
    {
        using var tree = new SourceTree();
        var alpha = tree.Write("Alpha.cs", "class Alpha {}");
        var beta = tree.Write("Beta.cs", "class Beta {}");
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        Assert.Equal(new[] { alpha }, tree.Find("Alpha"));
        var original = FocusedEvidenceRequestResolver.ParseCSharpRoot(beta);
        var length = new FileInfo(beta).Length;
        var oldTimestamp = File.GetLastWriteTimeUtc(beta);

        File.SetLastWriteTimeUtc(beta, oldTimestamp.AddMinutes(1));
        Assert.NotEqual(oldTimestamp, File.GetLastWriteTimeUtc(beta));
        Assert.Equal(length, new FileInfo(beta).Length);
        Assert.Equal(new[] { beta }, tree.Find("Beta"));
        var refreshed = FocusedEvidenceRequestResolver.ParseCSharpRoot(beta);
        Assert.NotSame(original, refreshed);
        Assert.Equal(original.ToFullString(), refreshed.ToFullString());
        Assert.Equal(1, parses.CountFor(alpha));
        Assert.Equal(1, parses.CountFor(beta));
        Assert.Equal(2, parses.Total);
    }

    [Fact]
    public void LockedColdFile_PreservesReadExceptionAndRetriesAfterRelease()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None is a Windows read-failure fixture.
        using var tree = new SourceTree();
        var path = tree.Write("Locked.cs", "class Locked {}");
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var expected = Assert.Throws<IOException>(() => File.ReadAllText(path));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var actual = Assert.Throws<IOException>(() => FocusedEvidenceRequestResolver.ParseCSharpRoot(path));
                Assert.Equal(expected.Message, actual.Message);
            }
            Assert.Equal(0, parses.Total);
        }

        Assert.Equal(new[] { path }, tree.Find("Locked"));
        FocusedEvidenceRequestResolver.ParseCSharpRoot(path);
        Assert.Equal(1, parses.CountFor(path));
    }

    [Fact]
    public void DeletedResidentFile_PreservesReadExceptionAndReloadsAfterRecreation()
    {
        using var tree = new SourceTree();
        var path = tree.Write("Deleted.cs", "class Before {}");
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        FocusedEvidenceRequestResolver.ParseCSharpRoot(path);
        File.Delete(path);
        var expected = Assert.Throws<FileNotFoundException>(() => File.ReadAllText(path));
        var actual = Assert.Throws<FileNotFoundException>(() => FocusedEvidenceRequestResolver.ParseCSharpRoot(path));
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(1, parses.Total);

        tree.Write("Deleted.cs", "class RecreatedWithNewName {}");
        Assert.Equal(new[] { path }, tree.Find("RecreatedWithNewName"));
        Assert.Empty(tree.Find("Before"));
        Assert.Equal(2, parses.CountFor(path));
    }

    [Fact]
    public void LockedResidentFile_UnchangedFingerprint_ReturnsCachedRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var tree = new SourceTree();
        var path = tree.Write("Resident.cs", "class Resident {}");
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        var root = FocusedEvidenceRequestResolver.ParseCSharpRoot(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => File.ReadAllText(path));
            Assert.Same(root, FocusedEvidenceRequestResolver.ParseCSharpRoot(path));
        }
        Assert.Equal(1, parses.Total);
    }

    [Fact]
    public void ClassLookups_ColdAndWarm_MatchFreshDeclarationsInSourceOrder()
    {
        using var tree = new SourceTree();
        tree.Write("Nested.cs", "namespace Outer.Space { class Host { class Inner {} } partial class Shared {} }");
        tree.Write("Scoped.cs", "namespace Flat; sealed class FileScoped {} record struct Point; partial class Shared {}");
        tree.Write("Blocks.cs", "namespace A { namespace B { class Target {} } }");
        tree.Write("Generic.cs", "class Box<T> {} class SpaceHost {} interface Contract {} struct Value {}");
        tree.Write("Escaped.cs", @"class @Keyword {} class \u0048idden {}");
        tree.Write("Ignored.cs", "enum Ignored { Value } delegate void Callback();");
        tree.Write("bin/Generated.cs", "class Host {} class Generated {}");
        tree.Write("obj/Generated.cs", "class Host {} class Generated {}");
        var files = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(tree.Root, SourceTree.Project).ToArray();
        Assert.Equal(6, files.Length);
        string[] names = ["Host", " Host ", "Outer.Space.Host", "Space.Host", "pace.Host", "Inner", "Host.Inner",
            "Space.Host.Inner", "Outer.Space.Host.Inner", "FileScoped", "Flat.FileScoped", "Point", "Flat.Point",
            "Target", "B.Target", "A.B.Target", "Box", "Box<T>", "SpaceHost", "Shared", "Contract", "Value",
            "Keyword", "Hidden", "Ignored", "Callback", "Generated", "Missing", "host"];
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);

        foreach (var name in names)
        {
            var expected = files.Where(path => FreshRoot(path).DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Any(declaration => FocusedEvidenceRequestResolver.FocusedEvidenceTypeMatches(declaration, name))).ToArray();
            Assert.Equal(expected, tree.Find(name));
            Assert.Equal(expected, tree.Find(name));
        }
        Assert.Equal(2, tree.Find("Shared").Count);
        Assert.Single(tree.Find("Space.Host"));
        Assert.Single(tree.Find("Box"));
        Assert.Empty(tree.Find("pace.Host"));
        Assert.Empty(tree.Find("Ignored"));
        Assert.Equal(files.Length, parses.Total);
        foreach (var path in files) Assert.Equal(1, parses.CountFor(path));
    }

    [Fact]
    public void Store_AtCapacity_EvictsLeastRecentlyUsedFile()
    {
        using var tree = new SourceTree();
        var alpha = tree.Write("Alpha.cs", "class Alpha {}");
        var beta = tree.Write("Beta.cs", "class Beta {}");
        var gamma = tree.Write("Gamma.cs", "class Gamma {}");
        var store = new FocusedEvidenceParsedRootCache.Store(2);
        var loads = new Dictionary<string, int>();
        CompilationUnitSyntax Load(string path)
        {
            loads[path] = loads.GetValueOrDefault(path) + 1;
            return FreshRoot(path);
        }

        var first = store.Get(alpha, Load);
        store.Get(beta, Load);
        Assert.Same(first, store.Get(alpha, Load));
        store.Get(gamma, Load);
        Assert.Equal(2, store.Count);
        Assert.Same(first, store.Get(alpha, Load));
        store.Get(beta, Load);
        Assert.Equal(2, store.Count);
        Assert.Equal(1, loads[alpha]);
        Assert.Equal(2, loads[beta]);
        Assert.Equal(1, loads[gamma]);
    }

    [Fact]
    public void Store_FailedLoad_RethrowsOriginalExceptionAndRetries()
    {
        using var tree = new SourceTree();
        var path = tree.Write("Retry.cs", "class Retry {}");
        var store = new FocusedEvidenceParsedRootCache.Store(2);
        var failure = new IOException("Source loader failed.");
        var calls = 0;
        CompilationUnitSyntax Load(string source)
        {
            if (++calls == 1) throw failure;
            return FreshRoot(source);
        }

        Assert.Same(failure, Assert.Throws<IOException>(() => store.Get(path, Load)));
        Assert.Equal(0, store.Count);
        var retried = store.Get(path, Load);
        Assert.Equal("Retry", Assert.Single(retried.Names).SimpleName);
        Assert.Same(retried, store.Get(path, Load));
        Assert.Equal(2, calls);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Store_MetadataChangesDuringLoad_ReturnsRootWithoutRetainingIt()
    {
        using var tree = new SourceTree();
        var path = tree.Write("Changing.cs", "class Before {}");
        var store = new FocusedEvidenceParsedRootCache.Store(2);
        var calls = 0;
        CompilationUnitSyntax Load(string source)
        {
            var root = FreshRoot(source);
            if (++calls == 1) tree.Write("Changing.cs", "class AfterWithLongerName {}");
            return root;
        }

        Assert.Contains("Before", store.Get(path, Load).Root.ToFullString());
        Assert.Equal(0, store.Count);
        var refreshed = store.Get(path, Load);
        Assert.Contains("AfterWithLongerName", refreshed.Root.ToFullString());
        Assert.Equal("AfterWithLongerName", Assert.Single(refreshed.Names).SimpleName);
        Assert.Same(refreshed, store.Get(path, Load));
        Assert.Equal(2, calls);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Store_BlockedLoad_AllowsUnrelatedLoadsAndSharesCompletedRoot()
    {
        using var tree = new SourceTree();
        var alpha = tree.Write("Alpha.cs", "class Alpha {}");
        var beta = tree.Write("Beta.cs", "class Beta {}");
        var store = new FocusedEvidenceParsedRootCache.Store(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var alphaLoads = 0;
        CompilationUnitSyntax Load(string path)
        {
            if (path == alpha)
            {
                Interlocked.Increment(ref alphaLoads);
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "The blocked source loader was not released.");
            }
            return FreshRoot(path);
        }

        var first = Task.Run(() => store.Get(alpha, Load));
        Task<FocusedEvidenceParsedRootCache.ParsedFile>? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "The first source loader did not enter.");
            var unrelated = await WaitFor(Task.Run(() => store.Get(beta, Load)), "unrelated source load");
            Assert.Equal("Beta", Assert.Single(unrelated.Names).SimpleName);
            second = Task.Run(() => store.Get(alpha, Load));
        }
        finally { release.Set(); }

        var loaded = await WaitFor(first, "first source load completion");
        Assert.NotNull(second);
        Assert.Same(loaded, await WaitFor(second, "second source load completion"));
        Assert.Equal(1, alphaLoads);
        Assert.Equal(2, store.Count);
    }

    private static async Task<T> WaitFor<T>(Task<T> task, string eventName)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new TimeoutException($"Did not observe {eventName}.", ex); }
    }

    private static CompilationUnitSyntax FreshRoot(string path) =>
        CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetCompilationUnitRoot();

    private sealed class SourceTree : IDisposable
    {
        private readonly string _marker = "\n// fixture " + Guid.NewGuid().ToString("N");
        internal const string Project = "tests/P/P.csproj";
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();

        internal string Write(string relative, string source)
        {
            var path = Path.Combine(Root, "tests", "P", relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source + _marker);
            return path;
        }

        internal IReadOnlyList<string> Find(string name) =>
            FocusedEvidenceRequestResolver.FindFocusedEvidenceClassFiles(Root, Project, name);

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
