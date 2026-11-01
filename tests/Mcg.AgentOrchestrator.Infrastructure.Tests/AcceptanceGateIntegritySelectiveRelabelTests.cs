using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its tree and labeler; no process-wide state is changed.
public sealed class AcceptanceGateIntegritySelectiveRelabelTests
{
    [Fact]
    public void Normalize_NestedExplicitLow_ChangesOnlyTheLowDirectory()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        using var fixture = new AcceptanceGateIntegrityTestSupport();
        Assert.True(fixture.Labels.SetIntegrity(fixture.Worktree, "(OI)(CI)M", recursive: false));
        var parent = Directory.CreateDirectory(Path.Combine(fixture.Worktree, "a")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(parent, "b")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(fixture.Worktree, "sibling")).FullName;
        for (var index = 0; index < 200; index++)
            File.WriteAllText(Path.Combine(index % 2 == 0 ? fixture.Worktree : sibling, $"file-{index}.txt"), "unlabeled output");
        // These files have no explicit label: they were created under the Medium root.
        // Keep b empty so its relabel cannot mask an explicitly Low child in the assertion.
        Assert.True(fixture.Labels.SetIntegrity(nested, "(OI)(CI)L", recursive: false));
        Assert.True(fixture.Labels.Query(nested).Low); // The discriminating precondition.
        Assert.True(fixture.Labels.Query(fixture.Worktree).Medium);
        var labeler = new RecordingNativeLabeler();

        AcceptanceGateIntegrityNormalizer.Normalize(fixture.Worktree, labeler);

        Assert.Equal((nested, "(OI)(CI)M", false), Assert.Single(labeler.Calls));
        AssertPreparedTree(fixture);
        Assert.True(fixture.Labels.Query(nested).Inheritable);
        var laterFile = Path.Combine(nested, "later.txt");
        File.WriteAllText(laterFile, "gate output");
        Assert.True(fixture.Labels.Query(laterFile).Medium);
    }

    [Fact]
    public void Normalize_LowRootAndExplicitFile_UsesNativeWritesAndReadback()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        using var fixture = new AcceptanceGateIntegrityTestSupport();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Worktree, "output", "nested")).FullName;
        var file = Path.Combine(nested, "existing.txt");
        File.WriteAllText(file, "worker output");
        Assert.True(fixture.Labels.SetIntegrity(fixture.Worktree, "(OI)(CI)L", recursive: true));
        Assert.True(fixture.Labels.SetIntegrity(file, "L", recursive: false));
        Assert.True(fixture.Labels.Query(file).Low);
        var labeler = new RecordingNativeLabeler();

        AcceptanceGateIntegrityNormalizer.Normalize(fixture.Worktree, labeler);

        Assert.Contains(labeler.Calls, call => call.Path == fixture.Worktree);
        Assert.Contains(labeler.Calls, call => call.Path == file && call.Level == "M");
        Assert.All(labeler.Calls, call => Assert.False(call.Recursive));
        AssertPreparedTree(fixture);
        // Clearing the file's explicit Low label must preserve directory inheritance
        // so a later worker can restore Low without explicitly relabeling that file.
        Assert.True(fixture.Labels.SetIntegrity(nested, "(OI)(CI)L", recursive: false));
        Assert.True(fixture.Labels.Query(file).Low);
    }

    [Fact]
    public async Task Normalize_NativeJunction_LeavesOutsideTargetUntouched()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        using var fixture = new AcceptanceGateIntegrityTestSupport();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside")).FullName;
        var outsideFile = Path.Combine(outside, "untouched.txt");
        File.WriteAllText(outsideFile, "outside Low output");
        Assert.True(fixture.Labels.SetIntegrity(outside, "(OI)(CI)L", recursive: true));
        Assert.True(fixture.Labels.SetIntegrity(fixture.Worktree, "(OI)(CI)L", recursive: false));
        var link = Path.Combine(fixture.Worktree, "link");
        try
        {
            var created = await WorkerProcessRunner.RunBufferedAsync(new(
                $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{outside.Replace("'", "''")}' | Out-Null",
                fixture.Root, TimeSpan.FromSeconds(30)), CancellationToken.None);
            Assert.False(created.TimedOut);
            Assert.Equal(0, created.ExitCode);
            Assert.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
            Assert.True(fixture.Labels.Query(outsideFile).Low);
            var labeler = new RecordingNativeLabeler();

            AcceptanceGateIntegrityNormalizer.Normalize(fixture.Worktree, labeler);

            Assert.Equal(fixture.Worktree, Assert.Single(labeler.Calls).Path);
            Assert.DoesNotContain(link, labeler.Queries);
            Assert.True(fixture.Labels.Query(outside).Low);
            Assert.True(fixture.Labels.Query(outsideFile).Low);
        }
        finally
        {
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
        }
    }

    [Fact]
    public void NeedsRelabel_UnsafeOrUncertainState_RequiresAWrite()
    {
        var states = new[]
        {
            new IntegrityLabelState(Exists: false, Low: false, Inheritable: false),
            new IntegrityLabelState(Exists: true, Low: true, Inheritable: true),
            new IntegrityLabelState(Exists: true, Low: false, Inheritable: false),
            new IntegrityLabelState(Exists: true, Low: false, Inheritable: true, Medium: true, NativeQueryError: 5)
        };

        Assert.All(states, state =>
        {
            Assert.True(AcceptanceGateIntegritySelection.NeedsRelabel(state, directory: true));
            Assert.True(AcceptanceGateIntegritySelection.NeedsRelabel(state, directory: false));
        });
        var unlabeled = new IntegrityLabelState(Exists: true, Low: false, Inheritable: false, Unlabeled: true);
        Assert.False(AcceptanceGateIntegritySelection.NeedsRelabel(unlabeled, directory: false));
        Assert.True(AcceptanceGateIntegritySelection.NeedsRelabel(unlabeled, directory: true));
        var medium = new IntegrityLabelState(Exists: true, Low: false, Inheritable: false, Medium: true);
        Assert.False(AcceptanceGateIntegritySelection.NeedsRelabel(medium, directory: false));
        Assert.True(AcceptanceGateIntegritySelection.NeedsRelabel(medium, directory: true));
        Assert.False(AcceptanceGateIntegritySelection.NeedsRelabel(medium with { Inheritable = true }, directory: true));
    }

    private static void AssertPreparedTree(AcceptanceGateIntegrityTestSupport fixture)
    {
        var paths = Directory.EnumerateFileSystemEntries(fixture.Worktree, "*", SearchOption.AllDirectories)
            .Prepend(fixture.Worktree).ToArray();
        Assert.NotEmpty(paths);
        Assert.All(paths, path =>
        {
            var state = fixture.Labels.Query(path);
            Assert.True(state.Exists, path);
            Assert.Null(state.NativeQueryError);
            Assert.False(state.Low, path);
            Assert.True(state.Medium || (!Directory.Exists(path) && state.Unlabeled), path);
            if (Directory.Exists(path)) Assert.True(state.Inheritable, path);
        });
    }

    private sealed class RecordingNativeLabeler : IAcceptanceGateSelectiveIntegrityLabeler
    {
        // Query uses the real native API. Any regression to icacls during normalization
        // fails at the process seam, rather than merely checking an implementation name.
        private readonly AcceptanceGateIntegrityLabeler inner = new(new IcaclsIntegrityLabeler(
            _ => throw new InvalidOperationException("Normalization must not start icacls.")));
        internal List<(string Path, string Level, bool Recursive)> Calls { get; } = [];
        internal List<string> Queries { get; } = [];

        public IntegrityLabelState Query(string path)
        {
            Queries.Add(path);
            return inner.Query(path);
        }

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            Calls.Add((path, level, recursive));
            return inner.SetIntegrity(path, level, recursive);
        }
    }
}
