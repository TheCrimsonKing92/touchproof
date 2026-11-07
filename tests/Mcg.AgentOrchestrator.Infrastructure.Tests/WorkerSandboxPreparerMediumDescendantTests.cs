using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its filesystem root and in-memory labels; no native ACL writes.
public sealed class WorkerSandboxPreparerMediumDescendantTests
{
    [Fact]
    public void Prepare_NormalizedDescendantsUnderLowAncestors_RecursivelyRestoresLow()
    {
        using var tree = new TestTree();
        var tests = tree.Directory("tests");
        var source = tree.Directory("src");
        var testProject = tree.Directory("tests", "Project.Tests");
        var sourceProject = tree.Directory("src", "Project");
        var testFile = tree.File("tests", "Project.Tests", "Regression.cs");
        var sourceFile = tree.File("src", "Project", "Program.cs");
        var preparer = new WorkerSandboxPreparer(tree.Labels);
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).WorktreeRecursiveRelabel);
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).PrepReceiptHit);

        AcceptanceGateIntegrityNormalizer.Normalize(tree.Root, tree.Labels);
        foreach (var path in new[] { tree.Root, tests, source })
            Assert.True(tree.Labels.SetIntegrity(path, "(OI)(CI)L", recursive: false));

        Assert.True(tree.Labels.Query(tree.Root).Low);
        Assert.True(tree.Labels.Query(tree.Root).Inheritable);
        foreach (var path in new[] { testProject, sourceProject, testFile, sourceFile })
        {
            Assert.True(tree.Labels.Query(path).Medium, path);
            Assert.False(tree.Labels.Query(path).Low, path);
        }
        Assert.True(System.IO.File.Exists(Path.Combine(tree.Root, WorkerSandboxPreparer.ReceiptFileName)));
        tree.Labels.SetCalls.Clear();

        var result = preparer.Prepare(tree.Root, tree.Sandbox);

        Assert.False(result.PrepReceiptHit);
        Assert.True(result.WorktreeRecursiveRelabel);
        Assert.Null(result.RecoveryAction);
        var repair = Assert.Single(tree.Labels.SetCalls);
        Assert.Equal((tree.Root, WorkerSandboxPreparer.LowInheritableLevel, true), repair);
        foreach (var path in new[] { testProject, sourceProject, testFile, sourceFile })
        {
            Assert.True(tree.Labels.Query(path).Low, path);
            Assert.False(tree.Labels.Query(path).Medium, path);
        }
        tree.Labels.SetCalls.Clear();
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).PrepReceiptHit);
        Assert.Empty(tree.Labels.SetCalls);
    }

    [Fact]
    public void Prepare_FullyLowTree_ReusesReceiptWithDirectoryQueriesOnly()
    {
        using var tree = new TestTree();
        var project = tree.Directory("src", "Project");
        var file = tree.File("src", "Project", "Program.cs");
        var preparer = new WorkerSandboxPreparer(tree.Labels);
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).WorktreeRecursiveRelabel);
        tree.Labels.SetCalls.Clear();
        tree.Labels.Queries.Clear();

        var result = preparer.Prepare(tree.Root, tree.Sandbox);

        Assert.True(result.PrepReceiptHit);
        Assert.False(result.WorktreeRecursiveRelabel);
        Assert.False(result.SandboxRecursiveRelabel);
        Assert.Empty(tree.Labels.SetCalls);
        Assert.Contains(Path.Combine(tree.Root, "src"), tree.Labels.Queries);
        Assert.Contains(project, tree.Labels.Queries);
        Assert.DoesNotContain(file, tree.Labels.Queries);
        Assert.All(tree.Labels.Queries, path => Assert.True(System.IO.Directory.Exists(path), path));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void Prepare_FileAndExcludedEntriesAtBudgetBoundary_ReuseOnlyWithinBudget(
        int budget, bool receiptHit)
    {
        using var tree = new TestTree();
        tree.File("source.txt");
        var preparer = new WorkerSandboxPreparer(tree.Labels, maxDescendantEntries: budget);
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).WorktreeRecursiveRelabel);
        // Exactly four entries: source.txt, marker, receipt and the excluded sandbox directory.
        Assert.Equal(4, System.IO.Directory.EnumerateFileSystemEntries(tree.Root).Count());
        Assert.True(tree.Labels.Query(tree.Root).Low);
        tree.Labels.SetCalls.Clear();

        var result = preparer.Prepare(tree.Root, tree.Sandbox);

        Assert.Equal(receiptHit, result.PrepReceiptHit);
        Assert.Equal(!receiptHit, result.WorktreeRecursiveRelabel);
        if (receiptHit)
            Assert.Empty(tree.Labels.SetCalls);
        else
            Assert.Equal((tree.Root, WorkerSandboxPreparer.LowInheritableLevel, true),
                Assert.Single(tree.Labels.SetCalls));
    }

    [Fact]
    public void Prepare_TopLevelPrivateAndGitDirectories_SkipsTheirMediumDescendants()
    {
        using var tree = new TestTree();
        var git = tree.Directory(".git");
        var gitChild = tree.Directory(".git", "objects");
        var scratchChild = tree.Directory(".mcg-sandbox", "private");
        var preparer = new WorkerSandboxPreparer(tree.Labels);
        preparer.Prepare(tree.Root, tree.Sandbox);
        tree.Labels.SetIntegrity(git, "(OI)(CI)M", recursive: true);
        tree.Labels.SetIntegrity(scratchChild, "(OI)(CI)M", recursive: true);
        tree.Labels.SetCalls.Clear();
        tree.Labels.Queries.Clear();

        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).PrepReceiptHit);

        Assert.Empty(tree.Labels.SetCalls);
        Assert.DoesNotContain(git, tree.Labels.Queries);
        Assert.DoesNotContain(gitChild, tree.Labels.Queries);
        Assert.DoesNotContain(scratchChild, tree.Labels.Queries);
        Assert.True(tree.Labels.Query(gitChild).Medium);
        Assert.True(tree.Labels.Query(scratchChild).Medium);
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".mcg-sandbox")]
    public void Prepare_ExclusionNameBelowTopLevel_RepairsMediumDirectory(string name)
    {
        using var tree = new TestTree();
        var nested = tree.Directory("project", name);
        var preparer = new WorkerSandboxPreparer(tree.Labels);
        preparer.Prepare(tree.Root, tree.Sandbox);
        tree.Labels.SetIntegrity(nested, "(OI)(CI)M", recursive: false);
        tree.Labels.SetCalls.Clear();

        var result = preparer.Prepare(tree.Root, tree.Sandbox);

        Assert.False(result.PrepReceiptHit);
        Assert.True(result.WorktreeRecursiveRelabel);
        Assert.True(tree.Labels.Query(nested).Low);
        Assert.Equal((tree.Root, WorkerSandboxPreparer.LowInheritableLevel, true),
            Assert.Single(tree.Labels.SetCalls));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("native-error")]
    [InlineData("unlabeled")]
    [InlineData("unknown")]
    [InlineData("io-error")]
    [InlineData("access-denied")]
    public void Prepare_UnverifiableDirectory_RejectsReceiptAndRepairs(string failure)
    {
        using var tree = new TestTree();
        var project = tree.Directory("project");
        var preparer = new WorkerSandboxPreparer(tree.Labels);
        preparer.Prepare(tree.Root, tree.Sandbox);
        tree.Labels.QueryOverride = path => path != project ? null : failure switch
        {
            "missing" => new(false, false, false),
            "native-error" => new(true, true, true, NativeQueryError: 5),
            "unlabeled" => new(true, false, false, Unlabeled: true),
            "unknown" => new(true, false, false),
            "io-error" => throw new IOException("Injected label read failure"),
            "access-denied" => throw new UnauthorizedAccessException("Injected label read denial"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        tree.Labels.SetCalls.Clear();

        var result = preparer.Prepare(tree.Root, tree.Sandbox);

        Assert.False(result.PrepReceiptHit);
        Assert.True(result.WorktreeRecursiveRelabel);
        Assert.Equal((tree.Root, WorkerSandboxPreparer.LowInheritableLevel, true),
            Assert.Single(tree.Labels.SetCalls));
    }

    [Fact]
    public void Scan_MissingDirectory_ReturnsIncompleteWithoutMutation()
    {
        using var tree = new TestTree();

        var result = WorktreeDescendantIntegrityScan.Scan(
            Path.Combine(tree.Root, "missing"), tree.Labels);

        Assert.Equal(WorktreeDescendantIntegrityScanOutcome.Incomplete, result.Outcome);
        Assert.Empty(tree.Labels.Queries);
        Assert.Empty(tree.Labels.SetCalls);
    }

    [Fact]
    public void PrepareReadOnly_MediumWorktree_LabelsOnlySandboxRoot()
    {
        using var tree = new TestTree();
        var project = tree.Directory("project");
        var file = tree.File("project", "source.cs");
        tree.Labels.SetIntegrity(tree.Root, "(OI)(CI)M", recursive: true);
        tree.Labels.SetCalls.Clear();
        tree.Labels.Queries.Clear();
        var preparer = new WorkerSandboxPreparer(tree.Labels, maxDescendantEntries: 0);

        var result = preparer.PrepareSandboxRootOnly(tree.Root, tree.Sandbox);

        Assert.False(result.WorktreeRecursiveRelabel);
        Assert.Equal((tree.Sandbox, WorkerSandboxPreparer.LowInheritableLevel, false),
            Assert.Single(tree.Labels.SetCalls));
        Assert.True(preparer.PrepareSandboxRootOnly(tree.Root, tree.Sandbox).PrepReceiptHit);
        Assert.All(tree.Labels.Queries, path => Assert.Equal(tree.Sandbox, path));
        foreach (var path in new[] { tree.Root, project, file })
            Assert.True(tree.Labels.Query(path).Medium, path);
    }

    private sealed class TestTree : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "mcg-medium-descendants-" + Guid.NewGuid().ToString("N"));
        internal string Sandbox => Path.Combine(Root, ".mcg-sandbox");
        internal InMemoryLabeler Labels { get; } = new();

        internal TestTree() => System.IO.Directory.CreateDirectory(Sandbox);

        internal string Directory(params string[] parts) =>
            System.IO.Directory.CreateDirectory(Path.Combine([Root, .. parts])).FullName;

        internal string File(params string[] parts)
        {
            var path = Path.Combine([Root, .. parts]);
            System.IO.File.WriteAllText(path, "fixture");
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }

    private sealed class InMemoryLabeler : IWorkerIntegrityLabeler
    {
        private readonly Dictionary<string, IntegrityLabelState> labels = new(StringComparer.OrdinalIgnoreCase);
        internal List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];
        internal List<string> Queries { get; } = [];
        internal Func<string, IntegrityLabelState?>? QueryOverride { get; set; }

        public IntegrityLabelState Query(string path)
        {
            Queries.Add(path);
            if (QueryOverride?.Invoke(path) is { } overridden)
                return overridden;
            if (!System.IO.Directory.Exists(path) && !System.IO.File.Exists(path))
                return new(false, false, false);
            if (labels.TryGetValue(path, out var own))
                return own;
            for (var parent = System.IO.Directory.GetParent(path); parent is not null; parent = parent.Parent)
                if (labels.TryGetValue(parent.FullName, out var inherited) && inherited.Inheritable)
                    return inherited;
            return new(true, false, true, Medium: true);
        }

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            SetCalls.Add((path, level, recursive));
            if (recursive)
            {
                var prefix = path + Path.DirectorySeparatorChar;
                foreach (var descendant in labels.Keys.Where(
                             key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                    labels.Remove(descendant);
            }
            labels[path] = new(true, level.EndsWith('L'), level.Contains("(OI)(CI)"),
                Medium: level.EndsWith('M'));
            return true;
        }
    }
}
