using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: each test owns a unique temporary root and never resets the process-wide store.
public sealed class AcceptanceTestSourceIndexStoreTests
{
    [Fact]
    public void SecondDefaultRequestOverUnchangedTreeReadsNoSourcesAndMatches()
    {
        using var tree = new SourceTree();
        tree.Write("tests/P/First.cs", "partial class Shared {} class First {}");
        tree.Write("tests/P/Second.cs", "partial class Shared {}");
        tree.Write("tests/P/bin/Generated.cs", "class Shared {}");
        var first = new AcceptanceTestSourceResolver.Request(tree.Root);
        var paths = first.ResolveIdentity("N.Shared.Fact");
        Assert.Equal(new[] { "tests/P/First.cs", "tests/P/Second.cs" }, paths);
        Assert.Equal(2, first.ReadCount);

        var second = new AcceptanceTestSourceResolver.Request(tree.Root);
        Assert.Equal(paths, second.ResolveIdentity("N.Shared.Fact"));
        Assert.Equal(0, second.ReadCount);
        Assert.Equal(1, second.EnumerationCount);
    }

    [Fact]
    public void SharedStoreMatchesFreshInjectedBuildAfterAddChangeAndRemove()
    {
        using var tree = new SourceTree();
        tree.Write("tests/P/Alpha.cs", "class Alpha {} partial class Shared {} class Outer { class Nested {} }");
        tree.Write("tests/P/Beta.cs", "class Beta {} interface Contract {} record Record {} struct Value {}");
        tree.Write("tests/P/bin/Generated.cs", "class Alpha {} class Generated {}");
        tree.Write("tests/P/OBJ/Generated.cs", "class Alpha {} class Generated {}");
        tree.Write("tests/P/Escaped.cs", @"class \u0048idden {} class @Keyword {} class \u0053hared {} // Shared");
        string[] names = ["Alpha", "Beta", "Gamma", "Delta", "Shared", "Hidden", "Keyword",
            "Nested", "Contract", "Record", "Value", "Generated"];

        Compare();
        tree.Write("tests/P/Gamma.cs", "class Gamma {} partial class Shared {}");
        Assert.Equal(1, Compare().ReadCount);

        var beta = tree.PathFor("tests/P/Beta.cs");
        var originalWrite = File.GetLastWriteTimeUtc(beta);
        tree.Write("tests/P/Beta.cs", "class Delta { int changed; }");
        File.SetLastWriteTimeUtc(beta, originalWrite.AddMinutes(1));
        Assert.Equal(1, Compare().ReadCount);

        File.Delete(tree.PathFor("tests/P/Alpha.cs"));
        Assert.Equal(0, Compare().ReadCount);

        AcceptanceTestSourceResolver.Request Compare()
        {
            var shared = new AcceptanceTestSourceResolver.Request(tree.Root);
            var fresh = new AcceptanceTestSourceResolver.Request(tree.Root,
                enumerate: path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories),
                read: File.ReadAllText);
            foreach (var project in new string?[] { null, "tests/P/P.csproj" })
            foreach (var name in names)
                Assert.Equal(fresh.ResolveSourcePaths(project, "N." + name + ".Fact"),
                    shared.ResolveSourcePaths(project, "N." + name + ".Fact"));

            Assert.NotEmpty(shared.ResolveIdentity("N.Keyword.Fact"));
            Assert.Empty(shared.ResolveIdentity("N.Hidden.Fact"));
            Assert.Empty(shared.ResolveIdentity("N.Generated.Fact"));
            return shared;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshAfterCachedSyntaxUsesCurrentTypesForLaterRequests(bool warmStore)
    {
        using var tree = new SourceTree();
        const string relative = "tests/P/Test.cs";
        tree.Write(relative, "class A {}");
        if (warmStore)
            Assert.Equal(new[] { relative },
                new AcceptanceTestSourceResolver.Request(tree.Root).ResolveIdentity("N.A.Fact"));

        var request = new AcceptanceTestSourceResolver.Request(tree.Root);
        Assert.Equal("class A {}", request.ReadSyntax(relative)?.ToFullString());
        Assert.Equal(1, request.ReadCount);
        var lastWrite = File.GetLastWriteTimeUtc(tree.PathFor(relative));
        tree.Write(relative, "class Bee {}");
        File.SetLastWriteTimeUtc(tree.PathFor(relative), lastWrite.AddMinutes(1));

        var fresh = new AcceptanceTestSourceResolver.Request(tree.Root, read: File.ReadAllText);
        var expected = fresh.ResolveIdentity("N.Bee.Fact");
        Assert.Equal(new[] { relative }, expected);
        Assert.Equal(expected, request.ResolveIdentity("N.Bee.Fact"));
        Assert.Empty(request.ResolveIdentity("N.A.Fact"));
        Assert.Equal(expected, request.ResolveSourcePaths("tests/P/P.csproj", "N.Bee.Fact"));
        Assert.Equal(2, request.ReadCount);

        var later = new AcceptanceTestSourceResolver.Request(tree.Root);
        Assert.Equal(expected, later.ResolveIdentity("N.Bee.Fact"));
        Assert.Empty(later.ResolveIdentity("N.A.Fact"));
        Assert.Equal(0, later.ReadCount);
    }

    [Fact]
    public void RefreshAfterCachedReadFailureReadsNewlyAvailableSource()
    {
        using var tree = new SourceTree();
        const string relative = "tests/P/Test.cs";
        Directory.CreateDirectory(tree.PathFor("tests/P"));
        var request = new AcceptanceTestSourceResolver.Request(tree.Root);
        Assert.Null(request.ReadSyntax(relative));
        Assert.Equal(1, request.ReadCount);

        tree.Write(relative, "class Shared {}");
        Assert.Equal(new[] { relative }, request.ResolveIdentity("N.Shared.Fact"));
        Assert.Equal(2, request.ReadCount);

        var later = new AcceptanceTestSourceResolver.Request(tree.Root);
        Assert.Equal(new[] { relative }, later.ResolveIdentity("N.Shared.Fact"));
        Assert.Equal(0, later.ReadCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EitherInjectedFunctionBypassesWarmSharedStore(bool injectEnumeration)
    {
        using var tree = new SourceTree();
        tree.Write("tests/P/Test.cs", "class Shared {}");
        Assert.Single(new AcceptanceTestSourceResolver.Request(tree.Root).ResolveIdentity("N.Shared.Fact"));
        var calls = 0;
        Func<string, IEnumerable<string>>? enumerate = injectEnumeration ? path =>
        {
            calls++;
            return Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories);
        } : null;
        Func<string, string>? read = injectEnumeration ? null : path =>
        {
            calls++;
            return File.ReadAllText(path);
        };

        var request = new AcceptanceTestSourceResolver.Request(tree.Root, enumerate, read);
        Assert.Single(request.ResolveIdentity("N.Shared.Fact"));
        Assert.Equal(1, request.ReadCount);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EquivalentRootSpellingsReuseNamesRelativeToEachRequest()
    {
        using var tree = new SourceTree();
        tree.Write("tests/P/Test.cs", "class Shared {}");
        var original = new AcceptanceTestSourceResolver.Request(tree.Root);
        Assert.Equal(new[] { "tests/P/Test.cs" }, original.ResolveSourcePaths("tests/P/P.csproj", "N.Shared.Fact"));
        Assert.Equal(1, original.ReadCount);

        // Both requests search P, but their repository-relative answers have different prefixes.
        var other = new AcceptanceTestSourceResolver.Request(tree.PathFor("tests/./"));
        Assert.Equal(new[] { "P/Test.cs" }, other.ResolveSourcePaths("P/P.csproj", "N.Shared.Fact"));
        Assert.Equal(0, other.ReadCount);
    }

    [Fact]
    public void FailedEnumerationCanBeRetriedAfterRootAppears()
    {
        using var tree = new SourceTree();
        var root = tree.PathFor("tests");
        Assert.Null(AcceptanceTestSourceIndexStore.GetIndex(root, (_, _, _) =>
            throw new InvalidOperationException("Missing root must fail before reading")));

        tree.Write("tests/Test.cs", "class Shared {}");
        var request = new AcceptanceTestSourceResolver.Request(tree.Root);
        Assert.Equal(new[] { "tests/Test.cs" }, request.ResolveIdentity("N.Shared.Fact"));
        Assert.Equal(1, request.ReadCount);
    }

    [Fact]
    public void FailedRefreshKeepsPreviousSnapshotImmutableAndRetriesEveryNewFile()
    {
        using var tree = new SourceTree();
        tree.Write("tests/Original.cs", "class Original {}");
        var root = tree.PathFor("tests");
        var previous = AcceptanceTestSourceIndexStore.GetIndex(root,
            (path, _, _) => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot());
        Assert.NotNull(previous);
        Assert.Single(previous);
        tree.Write("tests/First.cs", "class First {}");
        tree.Write("tests/Second.cs", "class Second {}");
        var reads = 0;
        Assert.Null(AcceptanceTestSourceIndexStore.GetIndex(root, (path, _, _) =>
        {
            if (++reads == 2) throw new IOException("Refresh interrupted after one new file");
            return CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
        }));
        Assert.Equal(2, reads);
        Assert.Single(previous);
        Assert.True(previous.ContainsKey("Original"));

        var request = new AcceptanceTestSourceResolver.Request(tree.Root);
        Assert.Single(request.ResolveIdentity("N.First.Fact"));
        Assert.Single(request.ResolveIdentity("N.Second.Fact"));
        Assert.Equal(2, request.ReadCount);
        Assert.Single(previous);
    }

    [Fact]
    public void UnreadableSourceIsRetriedByLaterRequest()
    {
        using var tree = new SourceTree();
        tree.Write("tests/Test.cs", "class Shared {}");
        var skipped = 0;
        var index = AcceptanceTestSourceIndexStore.GetIndex(tree.PathFor("tests"), (_, _, _) =>
        {
            skipped++;
            return null;
        });
        Assert.NotNull(index);
        Assert.Empty(index);
        Assert.Equal(1, skipped);

        var request = new AcceptanceTestSourceResolver.Request(tree.Root);
        Assert.Single(request.ResolveIdentity("N.Shared.Fact"));
        Assert.Equal(1, request.ReadCount);
    }

    [Fact]
    public async Task ConcurrentRequestsReadEachSourceOnceAndReturnCompleteMatches()
    {
        using var tree = new SourceTree();
        tree.Write("tests/First.cs", "partial class Shared {}");
        tree.Write("tests/Second.cs", "partial class Shared {}");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            var request = new AcceptanceTestSourceResolver.Request(tree.Root);
            var paths = request.ResolveIdentity("N.Shared.Fact");
            return (paths, request.ReadCount);
        })).ToArray();
        start.SetResult();
        var results = await CompleteLookups();
        Assert.All(results, result => Assert.Equal(new[] { "tests/First.cs", "tests/Second.cs" }, result.paths));
        Assert.Equal(2, results.Sum(result => result.ReadCount));

        async Task<(IReadOnlyList<string> paths, int ReadCount)[]> CompleteLookups()
        {
            try { return await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (TimeoutException ex)
            {
                throw new TimeoutException("Concurrent source-index lookups did not finish", ex);
            }
        }
    }

    private sealed class SourceTree : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string PathFor(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        internal void Write(string relative, string source)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
