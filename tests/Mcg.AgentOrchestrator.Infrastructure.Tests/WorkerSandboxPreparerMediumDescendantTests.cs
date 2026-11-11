using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its filesystem root and in-memory labels; no native ACL writes.
public sealed class WorkerSandboxPreparerMediumDescendantTests
{
    [Theory]
    [InlineData("medium", false)]
    [InlineData("medium", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("native-error", false)]
    [InlineData("native-error", true)]
    [InlineData("unlabeled", false)]
    [InlineData("unlabeled", true)]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("io-error", false)]
    [InlineData("io-error", true)]
    [InlineData("access-denied", false)]
    [InlineData("access-denied", true)]
    [InlineData("budget", false)]
    [InlineData("budget", true)]
    public void Prepare_FailedLabelWithUnprovenDescendants_RecoversWithoutReceipt(
        string failure, bool hadReceipt)
    {
        using var tree = new TestTree();
        var project = tree.Directory("src", "Project");
        var preparer = new WorkerSandboxPreparer(tree.Labels,
            maxDescendantEntries: failure == "budget" ? 0 : 2048);
        if (hadReceipt)
            Assert.False(preparer.Prepare(tree.Root, tree.Sandbox).RequiresRecovery);
        else
            tree.Labels.SetIntegrity(tree.Root, "(OI)(CI)L", recursive: true);
        tree.Labels.QueryOverride = path => path != project ? null : failure switch
        {
            "medium" => new(true, false, true, Medium: true),
            "missing" => new(false, false, false),
            "native-error" => new(true, true, true, NativeQueryError: 5),
            "unlabeled" => new(true, false, false, Unlabeled: true),
            "unknown" => new(true, false, false),
            "io-error" => throw new IOException("Injected label read failure"),
            "access-denied" => throw new UnauthorizedAccessException("Injected label read denial"),
            "budget" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        tree.Labels.FailLowWrites = true;
        tree.Labels.SetCalls.Clear();
        var expected = failure switch
        {
            "medium" or "unlabeled" => WorktreeDescendantIntegrityScanOutcome.MediumDescendant,
            "budget" => WorktreeDescendantIntegrityScanOutcome.BudgetExceeded,
            _ => WorktreeDescendantIntegrityScanOutcome.Incomplete
        };
        Assert.Equal(expected, WorktreeDescendantIntegrityScan.Scan(
            tree.Root, tree.Labels, failure == "budget" ? 0 : 2048).Outcome);

        var result = preparer.Prepare(tree.Root, tree.Sandbox);

        Assert.True(result.RequiresRecovery);
        Assert.False(result.PrepReceiptHit);
        var action = Assert.IsType<WorkerSandboxPrepRecoverableAction>(result.RecoveryAction);
        Assert.Equal(tree.Root, action.FailedRoot);
        Assert.Equal(tree.Root, action.Worktree);
        Assert.Equal(tree.Sandbox, action.SandboxRoot);
        Assert.True(action.RequiresRecursiveRemediation);
        Assert.Contains($"Descendant integrity: {expected};", action.Reason);
        Assert.False(System.IO.File.Exists(Path.Combine(tree.Root, WorkerSandboxPreparer.ReceiptFileName)));
        Assert.Equal(hadReceipt, System.IO.File.Exists(Path.Combine(tree.Root, WorkerSandboxPreparer.MarkerFileName)));
        Assert.Equal((tree.Root, WorkerSandboxPreparer.LowInheritableLevel, true),
            Assert.Single(tree.Labels.SetCalls));
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).RequiresRecovery);
        Assert.False(System.IO.File.Exists(Path.Combine(tree.Root, WorkerSandboxPreparer.ReceiptFileName)));
    }

    [Fact]
    public void Prepare_FailedRepair_InvalidatesReceiptEvenIfDescendantsLaterBecomeLow()
    {
        using var tree = new TestTree();
        var project = tree.Directory("project");
        var preparer = new WorkerSandboxPreparer(tree.Labels);
        preparer.Prepare(tree.Root, tree.Sandbox);
        tree.Labels.SetIntegrity(project, "(OI)(CI)M", recursive: false);
        tree.Labels.FailLowWrites = true;
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).RequiresRecovery);
        tree.Labels.FailLowWrites = false;
        tree.Labels.SetIntegrity(project, "(OI)(CI)L", recursive: false);
        tree.Labels.FailLowWrites = true;
        tree.Labels.SetCalls.Clear();

        var result = preparer.Prepare(tree.Root, tree.Sandbox);

        Assert.False(result.RequiresRecovery);
        Assert.False(result.PrepReceiptHit);
        Assert.Equal((tree.Root, WorkerSandboxPreparer.LowInheritableLevel, true),
            Assert.Single(tree.Labels.SetCalls));
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).PrepReceiptHit);
    }

    [Fact]
    public void Prepare_FailedLabelWithPreparedDescendants_PublishesReusableReceipt()
    {
        using var tree = new TestTree();
        tree.Directory("src", "Project");
        var file = tree.File("src", "Project", "source.cs");
        var git = tree.Directory(".git", "objects");
        var scratch = tree.Directory(".mcg-sandbox", "private");
        tree.Labels.SetIntegrity(tree.Root, "(OI)(CI)L", recursive: true);
        foreach (var path in new[] { file, git, scratch })
            tree.Labels.SetIntegrity(path, "(OI)(CI)M", recursive: true);
        tree.Labels.FailLowWrites = true;
        tree.Labels.SetCalls.Clear();
        tree.Labels.Queries.Clear();
        var preparer = new WorkerSandboxPreparer(tree.Labels);

        var result = preparer.Prepare(tree.Root, tree.Sandbox);

        Assert.False(result.RequiresRecovery);
        Assert.False(result.PrepReceiptHit);
        Assert.False(result.WorktreeRecursiveRelabel);
        Assert.False(result.SandboxRecursiveRelabel);
        Assert.True(System.IO.File.Exists(Path.Combine(tree.Root, WorkerSandboxPreparer.ReceiptFileName)));
        Assert.True(System.IO.File.Exists(Path.Combine(tree.Sandbox, WorkerSandboxPreparer.ReceiptFileName)));
        foreach (var path in new[] { file, git, scratch })
        {
            Assert.DoesNotContain(path, tree.Labels.Queries);
            Assert.True(tree.Labels.Query(path).Medium);
        }
        Assert.Equal(new[] { (tree.Root, WorkerSandboxPreparer.LowInheritableLevel, true),
            (tree.Sandbox, WorkerSandboxPreparer.LowInheritableLevel, false) }, tree.Labels.SetCalls);
        tree.Labels.SetCalls.Clear();
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).PrepReceiptHit);
        Assert.Empty(tree.Labels.SetCalls);
    }

    [Fact]
    public void ApplySandbox_FailedRecursiveRepair_HoldsWritableLaunch()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires Windows sandbox preparation.");
        using var tree = new TestTree();
        var project = tree.Directory("src", "Project");
        tree.Labels.SetIntegrity(tree.Root, "(OI)(CI)L", recursive: true);
        tree.Labels.SetIntegrity(project, "(OI)(CI)M", recursive: false);
        tree.Labels.FailLowWrites = true;
        tree.Labels.SetCalls.Clear();
        var phases = new List<string>();
        var start = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        start.Environment.Clear();
        start.ArgumentList.Add("Write-Output worker-started");
        var parameters = new DispatchProcessHost.DispatchRunParameters(
            "Write-Output worker-started", tree.Root, Path.Combine(tree.Root, "out.log"),
            Path.Combine(tree.Root, "err.log"), Path.Combine(tree.Root, "exit.txt"), null,
            false, SandboxLowIntegrity: true, SandboxWorktreeWritable: true);

        var error = Assert.Throws<InvalidOperationException>(() =>
            DispatchProcessHost.ApplyWorkerSandbox(start, parameters,
                new WorkerSandboxPreparer(tree.Labels), (phase, _, _) => phases.Add(phase),
                protectWorkspaceBoundary: _ => Assert.Fail("Protection reached after failed preparation."),
                protectGitMetadata: _ => Assert.Fail("Protection reached after failed preparation."),
                providerEnvironmentReader: _ => null, sandboxFileSystem: new NoGitFileSystem()));

        Assert.Contains("Descendant integrity: MediumDescendant;", error.Message);
        Assert.Equal(new[] { "prepare-roots" }, phases);
        Assert.Equal("Write-Output worker-started", Assert.Single(start.ArgumentList));
        Assert.False(System.IO.File.Exists(Path.Combine(tree.Root, WorkerSandboxPreparer.ReceiptFileName)));
        Assert.False(System.IO.File.Exists(Path.Combine(tree.Sandbox, DispatchProcessHost.LowIntegritySetupArtifactName)));
        Assert.Equal((tree.Root, WorkerSandboxPreparer.LowInheritableLevel, true),
            Assert.Single(tree.Labels.SetCalls));
    }

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
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Prepare_DirectoriesAtBudgetBoundary_ReuseOnlyWithinBudget(
        int budget, bool receiptHit)
    {
        using var tree = new TestTree();
        tree.Directory("src", "project", "nested");
        tree.Directory(".git", "objects");
        tree.File("source.txt");
        var preparer = new WorkerSandboxPreparer(tree.Labels, maxDescendantEntries: budget);
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).WorktreeRecursiveRelabel);
        // Only src, project and nested consume the budget; files and exclusions do not.
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
    public void Prepare_FileHeavyLowTree_ReusesReceiptWithoutRelabel()
    {
        using var tree = new TestTree();
        var project = tree.Directory("project");
        for (var index = 0; index < 4097; index++)
            tree.File("project", $"source-{index}.txt");
        Assert.Equal(4097, System.IO.Directory.EnumerateFiles(project).Count());
        var preparer = new WorkerSandboxPreparer(tree.Labels);
        Assert.True(preparer.Prepare(tree.Root, tree.Sandbox).WorktreeRecursiveRelabel);
        tree.Labels.SetCalls.Clear();
        tree.Labels.Queries.Clear();

        var result = preparer.Prepare(tree.Root, tree.Sandbox);

        Assert.True(result.PrepReceiptHit);
        Assert.False(result.WorktreeRecursiveRelabel);
        Assert.False(result.SandboxRecursiveRelabel);
        Assert.Empty(tree.Labels.SetCalls);
        Assert.Contains(project, tree.Labels.Queries);
        Assert.All(tree.Labels.Queries, path => Assert.True(System.IO.Directory.Exists(path), path));
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

    private sealed class NoGitFileSystem : IWorkerSandboxFileSystem
    {
        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => false;
        public string ReadAllText(string path) => throw new InvalidOperationException("No Git metadata in fixture.");
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
        internal bool FailLowWrites { get; set; }

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
            if (FailLowWrites && level.EndsWith('L')) return false;
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
