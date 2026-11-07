using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using static GitWorkspaceLaunchFixture;

// Isolated repositories, per-test census and thread-local start observer; no ambient mutation.
public sealed class GitMetadataCommonDirectoryProtectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplySandbox_LinkedWorktree_QueryAvoidsProtectionPhaseGit(bool useQuery)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new GitWorkspaceLaunchFixture();
        var linked = fixture.AddLinkedWorktree(fixture.CreateRepository());
        var expected = GitWorkspaceLaunchFixture.CommonDirectory(linked);
        var sandboxRoot = Path.Combine(linked, ".mcg-sandbox");
        var labeler = new SandboxLabeler(linked, sandboxRoot);
        var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = linked };
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = "";
        startInfo.ArgumentList.Add("Write-Output ok");
        var parameters = new DispatchProcessHost.DispatchRunParameters(
            "Write-Output ok", linked, Path.Combine(fixture.Root, "out.log"), Path.Combine(fixture.Root, "err.log"),
            Path.Combine(fixture.Root, "exit.txt"), null, false,
            SandboxLowIntegrity: true, SandboxWorktreeWritable: true);
        var events = new List<string>();
        var reasons = new List<string>();
        using var launches = new GitWorkspaceLaunchFixture.Recording();
        var observer = GitCli.StartInfoObserver;
        GitCli.StartInfoObserver = info => { events.Add("git-launch"); observer?.Invoke(info); };
        try
        {
            DispatchProcessHost.ApplyWorkerSandbox(startInfo, parameters, new WorkerSandboxPreparer(labeler),
                (phase, _, _) => events.Add(phase), protectWorkspaceBoundary: _ => { },
                providerEnvironmentReader: _ => null, gitMetadataLabeler: labeler,
                sandboxFileSystem: new SandboxFileSystem(fixture.Root),
                commonDirectoryQuery: useQuery ? GitPathQuery.QueryCommonDirectory : null,
                reportGitMetadataFallback: reasons.Add);
        }
        finally { GitCli.StartInfoObserver = observer; }
        launches.Close(null);

        var before = events.IndexOf(WorkerSandboxPreparer.ProtectWorkspaceBoundaryPhase);
        var after = events.IndexOf(WorkerSandboxPreparer.ProtectGitMetadataPhase);
        Assert.True(before >= 0 && after > before, "Protection phase boundaries must be observed.");
        Assert.Equal(useQuery ? 0 : 1, events.Skip(before + 1).Take(after - before - 1)
            .Count(item => item == "git-launch"));
        Assert.Equal(useQuery ? 0 : 1, reasons.Count);
        Assert.Contains((Path.Combine(linked, ".git"), "M", false), labeler.SetCalls);
        Assert.Contains(labeler.SetCalls, call => GitWorkspaceLaunchFixture.Normalize(call.Path) ==
            GitWorkspaceLaunchFixture.Normalize(expected) && call.Level == "(OI)(CI)M" && !call.Recursive);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("linked")]
    [InlineData("separate")]
    [InlineData("edited-gitdir")]
    public void ProvenLayout_ProtectsGitOracleWithoutLaunchingGit(string layout)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var checkout = repo;
        if (layout is "linked" or "edited-gitdir")
        {
            checkout = fixture.AddLinkedWorktree(repo);
            Assert.False(Path.IsPathRooted(File.ReadAllText(Path.Combine(AdminDirectory(checkout), "commondir")).Trim()));
            Assert.NotNull(GitPathQuery.QueryCommonDirectory(checkout, null).Output);
            if (layout == "edited-gitdir")
            {
                var other = fixture.CreateRepository("other");
                // Truncate Git's existing hidden file rather than replacing it on Windows.
                using (var writer = new StreamWriter(File.Open(Path.Combine(checkout, ".git"), FileMode.Truncate, FileAccess.Write)))
                    writer.Write($"gitdir: {Path.Combine(other, ".git")}\n");
            }
        }
        else if (layout == "separate")
            Git(repo, "init", "--separate-git-dir=" + Path.Combine(fixture.Root, "metadata"));

        var expected = CommonDirectory(checkout);
        var labeler = new Labeler();
        var reasons = new List<string>();
        using var launches = new Recording();
        DispatchProcessHost.ProtectGitMetadata(checkout, labeler,
            commonDirectoryQuery: GitPathQuery.QueryCommonDirectory, reportFallback: reasons.Add);
        launches.Close();
        Assert.Empty(reasons);
        Assert.Equal(ExpectedProtection(checkout, expected), labeler.Calls);
    }

    [Theory]
    [InlineData("missing-gitdir")]
    [InlineData("missing-commondir")]
    [InlineData("non-git-commondir")]
    [InlineData("commondir-without-head")]
    [InlineData("GIT_DIR")]
    [InlineData("GIT_COMMON_DIR")]
    [InlineData("GIT_WORK_TREE")]
    [InlineData("empty-GIT_DIR")]
    [InlineData("lowercase-GIT_DIR")]
    public void UnprovenLayout_UsesOneGitFallbackAndProtectsItsAnswer(string scenario)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var linked = fixture.AddLinkedWorktree(fixture.CreateRepository());
        var originalCommon = CommonDirectory(linked);
        var other = fixture.CreateRepository("other");
        var environment = new Dictionary<string, string>();
        if (scenario == "missing-gitdir")
        {
            // Truncate Git's existing hidden file rather than replacing it on Windows.
            using (var writer = new StreamWriter(File.Open(Path.Combine(linked, ".git"), FileMode.Truncate, FileAccess.Write)))
                writer.Write($"gitdir: {Path.Combine(fixture.Root, "missing")}\n");
        }
        else if (scenario == "commondir-without-head")
            File.Delete(Path.Combine(originalCommon, "HEAD"));
        else if (scenario is "missing-commondir" or "non-git-commondir")
        {
            var target = Path.Combine(fixture.Root, "invalid-common");
            if (scenario == "non-git-commondir")
            {
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(Path.Combine(target, "objects"));
                Directory.CreateDirectory(Path.Combine(target, "refs"));
                // A readable config alone does not prove that git recognizes the directory.
                File.Copy(Path.Combine(other, ".git", "config"), Path.Combine(target, "config"));
            }
            File.WriteAllText(Path.Combine(AdminDirectory(linked), "commondir"),
                Path.GetRelativePath(AdminDirectory(linked), target) + "\n");
        }
        else if (scenario == "empty-GIT_DIR") environment["GIT_DIR"] = "";
        else if (scenario == "lowercase-GIT_DIR")
        {
            if (!OperatingSystem.IsWindows()) return;
            environment["git_dir"] = Path.Combine(other, ".git");
        }
        else environment[scenario] = scenario == "GIT_WORK_TREE" ? other : Path.Combine(other, ".git");

        var oracle = GitCli.RunWithEnvironment(linked, environment, "rev-parse", "--git-common-dir");
        var expected = oracle.Succeeded && !string.IsNullOrWhiteSpace(oracle.Output)
            ? Path.GetFullPath(oracle.Output.Trim(), linked) : null;
        if (scenario is "GIT_DIR" or "GIT_COMMON_DIR" or "lowercase-GIT_DIR")
        {
            Assert.NotNull(expected);
            Assert.NotEqual(Normalize(originalCommon), Normalize(expected));
        }
        var labeler = new Labeler();
        var reasons = new List<string>();
        using var launches = new Recording();
        DispatchProcessHost.ProtectGitMetadata(linked, labeler, gitEnvironment: environment,
            commonDirectoryQuery: GitPathQuery.QueryCommonDirectory, reportFallback: reasons.Add);
        launches.Close("rev-parse");
        Assert.Equal(["rev-parse", "--git-common-dir"], Assert.Single(launches.Arguments));
        var reason = Assert.Single(reasons);
        if (environment.Count > 0) Assert.StartsWith("git-environment-override:", reason);
        else Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(ExpectedProtection(linked, expected), labeler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookupFailure_StillUsesGitWhenFallbackDiagnosticThrows(bool queryThrows)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var linked = fixture.AddLinkedWorktree(fixture.CreateRepository());
        var expected = CommonDirectory(linked);
        var labeler = new Labeler();
        var reasons = new List<string>();
        using var launches = new Recording();
        DispatchProcessHost.ProtectGitMetadata(linked, labeler,
            commonDirectoryQuery: (_, _) => queryThrows ? throw new IOException("lookup failed")
                : (fixture.Root, "unproven-test-layout"),
            reportFallback: reason => { reasons.Add(reason); throw new IOException("diagnostic failed"); });
        launches.Close("rev-parse");
        Assert.Equal(queryThrows ? "in-process-query-exception:IOException" : "unproven-test-layout",
            Assert.Single(reasons));
        Assert.Equal(ExpectedProtection(linked, expected), labeler.Calls);
    }

    private sealed class SandboxLabeler(string worktree, string sandboxRoot) : IWorkerIntegrityLabeler
    {
        internal List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];
        public IntegrityLabelState Query(string path) => new(Exists: true,
            Low: path == worktree || path == sandboxRoot, Inheritable: true, Medium: true);
        public bool SetIntegrity(string path, string level, bool recursive)
        {
            SetCalls.Add((path, level, recursive));
            return true;
        }
    }

    // Keep the preparation boundary inside this test's repository root.
    private sealed class SandboxFileSystem(string root) : IWorkerSandboxFileSystem
    {
        private bool IsOwned(string path) => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        public bool FileExists(string path) => IsOwned(path) && File.Exists(path);
        public bool DirectoryExists(string path) => IsOwned(path) && Directory.Exists(path);
        public string ReadAllText(string path) => IsOwned(path)
            ? File.ReadAllText(path) : throw new InvalidOperationException("Read outside fixture root.");
    }

    private static List<(string Path, string Level, bool Recursive)> ExpectedProtection(
        string checkout, string? common)
    {
        var calls = new List<(string, string, bool)>();
        if (File.Exists(Path.Combine(checkout, ".git")))
            calls.Add((Normalize(Path.Combine(checkout, ".git")), "M", false));
        if (common is not null && Directory.Exists(common))
            calls.Add((Normalize(common), "(OI)(CI)M", false));
        return calls;
    }

    [Fact]
    public void LinkedAndPrimaryCheckout_ProtectWithoutGitLaunch()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var linked = fixture.AddLinkedWorktree(repo);
        foreach (var checkout in new[] { linked, repo })
        {
            var expected = CommonDirectory(checkout); // Real git oracle outside the window.
            var labeler = new Labeler();
            using var launches = new Recording();
            DispatchProcessHost.ProtectGitMetadata(checkout, labeler, GitRevisionReader.ResolveCommonDirectoryOrNull);
            launches.Close();
            var common = (Normalize(expected), "(OI)(CI)M", false);
            Assert.Equal(checkout == linked
                ? [(Normalize(Path.Combine(linked, ".git")), "M", false), common]
                : new[] { common }, labeler.Calls);
        }
    }

    [Fact]
    public void Prepare_LinkedWorkspace_ResolvesCommonDirectoryWithoutGitLaunch()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var linked = fixture.AddLinkedWorktree(repo, "t-caller-layout");
        var expected = CommonDirectory(linked);
        Assert.Equal(Normalize(expected), Normalize(Assert.IsType<string>(
            GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null))));
        var labeler = new Labeler();
        using var scope = AcceptanceWorkspaceIntegrityPreparer.PushIntegrityLabelerForTests(labeler);
        using var launches = new Recording();
        AcceptanceWorkspaceIntegrityPreparer.Prepare(linked);
        launches.Close();
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains((Normalize(Path.Combine(linked, ".git")), "M", false), labeler.Calls);
            Assert.Contains((Normalize(expected), "(OI)(CI)M", false), labeler.Calls);
        }
        TestContext.Current.TestOutputHelper!.WriteLine("Caller-layout resolver decided 1/1; Prepare git launches=0.");
    }

    [Fact]
    public void InjectedGitDirectory_FallsBackToOneRevParse()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var linked = fixture.AddLinkedWorktree(repo);
        var other = fixture.CreateRepository("other");
        var environment = new Dictionary<string, string> { ["GIT_DIR"] = Path.Combine(other, ".git") };
        var expected = CommonDirectory(linked, environment);
        Assert.NotEqual(Normalize(CommonDirectory(linked)), Normalize(expected));
        Assert.Null(GitRevisionReader.ResolveCommonDirectoryOrNull(linked, environment));
        var labeler = new Labeler();
        using var launches = new Recording();
        DispatchProcessHost.ProtectGitMetadata(linked, labeler,
            GitRevisionReader.ResolveCommonDirectoryOrNull, environment);
        launches.Close("rev-parse");
        Assert.Equal(["rev-parse", "--git-common-dir"], Assert.Single(launches.Arguments));
        Assert.Equal([(Normalize(Path.Combine(linked, ".git")), "M", false),
            (Normalize(expected), "(OI)(CI)M", false)], labeler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LabelingFailure_PreservesDiagnosticAndDoesNotFallback(bool gitFile)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var linked = fixture.AddLinkedWorktree(fixture.CreateRepository());
        var common = Assert.IsType<string>(GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null));
        var target = gitFile ? Path.Combine(linked, ".git") : common;
        var labeler = new Labeler(target);
        using var launches = new Recording();
        var failure = Assert.Throws<InvalidOperationException>(() => DispatchProcessHost.ProtectGitMetadata(
            linked, labeler, GitRevisionReader.ResolveCommonDirectoryOrNull));
        launches.Close();
        Assert.Equal(gitFile ? $"Failed to protect linked worktree git file '{target}'."
            : $"Failed to protect git common dir '{target}'.", failure.Message);
        Assert.Equal(gitFile ? 1 : 2, labeler.Calls.Count);
    }
}
