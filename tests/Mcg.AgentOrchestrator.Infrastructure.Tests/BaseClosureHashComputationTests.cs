using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: unique repositories and private memos; launch observation is thread-local.
public sealed class BaseClosureHashComputationTests : IDisposable
{
    private const int CandidateGitTimeoutMilliseconds = 10_000;
    private static readonly HashSet<string> CandidateRootInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "Directory.Packages.props",
        "global.json", "NuGet.config"
    };
    private readonly string root = Path.Combine(Path.GetTempPath(), "mcg-base-closure", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SecondComputationLaunchesNothingAndReturnsSameHash()
    {
        var commit = Seed();
        var memo = new GitBaseClosureHashMemo();
        string[] changed = ["src/A/File.cs", "notes.txt"];
        var first = Compute(commit, changed, memo);
        Assert.Equal(2, first.Launches.Count);
        var second = Compute(commit, changed.Reverse().ToArray(), memo);
        Assert.Empty(second.Launches);
        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(1, memo.Count);
        Assert.Equal(1, memo.Hits);
    }

    [Fact]
    public void DifferentCommitOrChangedListComputesAgain()
    {
        var commit = Seed();
        var memo = new GitBaseClosureHashMemo();
        Compute(commit, ["src/A/File.cs"], memo);
        Assert.NotEmpty(Compute(commit, ["src/B/File.cs"], memo).Launches);
        Write("src/E/File.cs", "// new base\n");
        var nextCommit = Commit();
        Assert.NotEqual(commit, nextCommit);
        Assert.NotEmpty(Compute(nextCommit, ["src/A/File.cs"], memo).Launches);
        Assert.Equal(3, memo.Count);
        Assert.Equal(0, memo.Hits);
    }

    [Fact]
    public void ColdBatchUsesTwoLaunchesAndMatchesPerFileReference()
    {
        var commit = Seed();
        string[] changed = ["src/A/File.cs", "notes.txt"];
        Assert.True(ReferenceHash(root, commit, changed, out var reference, out var failure), failure);
        var result = Compute(commit, changed, new GitBaseClosureHashMemo());
        Assert.InRange(result.Launches.Count, 1, 3);
        Assert.Equal(new[] { "ls-tree", "cat-file" }, result.Launches.Select(args => args[0]));
        Assert.Single(result.Launches, args => args.SequenceEqual(new[] { "cat-file", "--batch" }));
        Assert.DoesNotContain(result.Launches, args => args[0] == "show");
        Assert.Equal(reference, result.Hash);

        // E is the fifth project and lies outside A's chain/branch closure.
        Write("src/E/File.cs", "// excluded project changed\n");
        Assert.Equal(result.Hash, Compute(Commit(), changed, new GitBaseClosureHashMemo()).Hash);
        Write("src/D/File.cs", "// transitive dependency changed\n");
        Assert.NotEqual(result.Hash, Compute(Commit(), changed, new GitBaseClosureHashMemo()).Hash);
    }

    [Fact]
    public void FailedBlobReadIsNotStoredAndRecoveryComputesThenCaches()
    {
        var commit = Seed();
        var memo = new GitBaseClosureHashMemo();
        var blob = Git("rev-parse", commit + ":src/D/D.csproj");
        var objectFile = Path.Combine(root, ".git", "objects", blob[..2], blob[2..]);
        Assert.True(File.Exists(objectFile), "Fixture must provide a loose project blob to remove.");
        var held = objectFile + ".held";
        File.Move(objectFile, held);
        try
        {
            var launches = Capture(() =>
            {
                Assert.False(GoalWorktrees.TryHashBaseClosure(root, commit, ["src/A/File.cs"], memo,
                    out _, out var failure));
                Assert.Equal("project-reference-unavailable", failure);
            });
            Assert.Contains(launches, args => args[0] == "cat-file");
            Assert.Equal(0, memo.Count);
        }
        finally { File.Move(held, objectFile); }
        var recovered = Compute(commit, ["src/A/File.cs"], memo);
        Assert.NotEmpty(recovered.Launches);
        Assert.Equal(1, memo.Count);
        var cached = Compute(commit, ["src/A/File.cs"], memo);
        Assert.Empty(cached.Launches);
        Assert.Equal(recovered.Hash, cached.Hash);
    }

    [Fact]
    public void FailedTreeReadIsNotStored()
    {
        Directory.CreateDirectory(root);
        var memo = new GitBaseClosureHashMemo();
        Assert.False(GoalWorktrees.TryHashBaseClosure(root, new string('a', 40), [], memo,
            out _, out var failure));
        Assert.Equal("base-tree-unavailable", failure);
        Assert.Equal(0, memo.Count);
    }

    [Fact]
    public void MalformedXmlThrowsWithoutStoring()
    {
        Seed();
        Write("src/A/A.csproj", "<Project>");
        var commit = Commit();
        var memo = new GitBaseClosureHashMemo();
        Assert.Throws<System.Xml.XmlException>(() =>
            GoalWorktrees.TryHashBaseClosure(root, commit, ["src/A/File.cs"], memo, out _, out _));
        Assert.Equal(0, memo.Count);
    }

    [Theory]
    [InlineData("$(Unknown)/Other.csproj", "project-reference-unresolved")]
    [InlineData("../Missing/Missing.csproj", "project-reference-missing")]
    [InlineData("../b/b.csproj", "project-reference-unavailable")]
    public void TraversalFailuresAreNotStored(string include, string expectedFailure)
    {
        Seed();
        Write("src/A/A.csproj", $"<Project><ProjectReference Include=\"{include}\" /></Project>");
        var memo = new GitBaseClosureHashMemo();
        Assert.False(GoalWorktrees.TryHashBaseClosure(root, Commit(), ["src/A/File.cs"], memo,
            out _, out var failure));
        Assert.Equal(expectedFailure, failure);
        Assert.Equal(0, memo.Count);
    }

    [Fact]
    public void SymbolicAndAbbreviatedRevisionsComputeWithoutMemoizing()
    {
        var commit = Seed();
        var expected = Compute(commit, ["src/A/File.cs"], new GitBaseClosureHashMemo()).Hash;
        foreach (var revision in new[] { "HEAD", commit[..12] })
        {
            var memo = new GitBaseClosureHashMemo();
            for (var call = 0; call < 2; call++)
            {
                var result = Compute(revision, ["src/A/File.cs"], memo);
                Assert.NotEmpty(result.Launches);
                Assert.Equal(expected, result.Hash);
                Assert.Equal(0, memo.Count);
            }
        }
    }

    [Fact]
    public void SuccessfulEmptyClosureIsMemoizedWithoutStartingReader()
    {
        Initialize();
        var commit = Commit();
        var memo = new GitBaseClosureHashMemo();
        var result = Compute(commit, [], memo);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(), result.Hash);
        Assert.Single(result.Launches);
        Assert.Equal("ls-tree", result.Launches[0][0]);
        Assert.Empty(Compute(commit, [], memo).Launches);
        Assert.Equal(1, memo.Count);
    }

    private (string Hash, List<string[]> Launches) Compute(string commit, string[] changed, GitBaseClosureHashMemo memo)
    {
        var hash = string.Empty;
        var launches = Capture(() =>
            Assert.True(GoalWorktrees.TryHashBaseClosure(root, commit, changed, memo, out hash, out var failure), failure));
        return (hash, launches);
    }

    private static List<string[]> Capture(Action action)
    {
        var launches = new List<string[]>();
        var previous = GitCli.StartInfoObserver;
        GitCli.StartInfoObserver = info => launches.Add(info.ArgumentList.Skip(GitCli.HardeningArguments.Count).ToArray());
        try { action(); }
        finally { GitCli.StartInfoObserver = previous; }
        return launches;
    }

    private void Initialize()
    {
        Directory.CreateDirectory(root);
        Git("init", "--initial-branch=fixture-main");
        Git("config", "user.name", "Base closure tests");
        Git("config", "user.email", "base-closure@example.test");
        Git("config", "commit.gpgSign", "false");
        Git("config", "core.hooksPath", Path.Combine(root, "no-hooks"));
        Git("config", "core.autocrlf", "false");
        Git("config", "gc.auto", "0");
    }

    private string Seed()
    {
        Initialize();
        Write("src/A/A.csproj", "<Project><ItemGroup><ProjectReference Include=\"../B/B.csproj\" />" +
            "<ProjectReference Include=\"..\\C\\C.csproj\" /></ItemGroup></Project>\n");
        Write("src/B/B.csproj", "<Project><ProjectReference Include=\"../D/D.csproj\" /></Project>\n", new UTF8Encoding(true));
        Write("src/C/C.csproj", "<Project>\r\n<ProjectReference Include=\"../D/D.csproj\" />\r\n</Project>\r\n");
        Write("src/D/D.csproj", "<Project><!-- unicode: \u03bb --></Project>\n", Encoding.Unicode);
        Write("src/E/E.csproj", "<Project />\n");
        foreach (var name in new[] { "A", "B", "C", "D", "E" }) Write($"src/{name}/File.cs", $"// {name}\n");
        Write("Directory.Build.props", "<Project />\n");
        Write("Fixture.sln", "fixture solution\n");
        Write("notes.txt", "individual file\n");
        return Commit();
    }

    private void Write(string relative, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
    }

    private string Commit()
    {
        Git("add", "-A");
        Git("commit", "--allow-empty", "-m", "fixture");
        return Git("rev-parse", "HEAD");
    }

    private string Git(params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, args);
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);

    // Verbatim pre-change algorithm, apart from its name, to detect retrieval/decoding drift.
    private static bool ReferenceHash(
        string worktreePath, string baseSha, IReadOnlyList<string> changedFiles,
        out string hash, out string failureReason)
    {
        hash = string.Empty;
        failureReason = string.Empty;
        var tree = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds, "ls-tree", "-r", "--full-tree", baseSha);
        if (!IsUsable(tree))
            return Fail("base-tree-unavailable", out failureReason);
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var metadata = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length == 3 && metadata[1] == "blob")
                blobs[line[(tab + 1)..].TrimEnd('\r')] = metadata[2];
        }
        var projects = blobs.Keys.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        var individualFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var changed in changedFiles)
        {
            var owner = projects.Where(project => changed.Equals(project, StringComparison.OrdinalIgnoreCase) ||
                    changed.StartsWith(project[..(project.LastIndexOf('/') + 1)], StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(project => project.Length).FirstOrDefault();
            if (owner is null) individualFiles.Add(changed);
            else pending.Enqueue(owner);
        }
        while (pending.TryDequeue(out var project))
        {
            if (!visited.Add(project)) continue;
            var source = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds, "show", $"{baseSha}:{project}");
            if (!IsUsable(source))
                return Fail("project-reference-unavailable", out failureReason);
            var document = XDocument.Parse(source.Output);
            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                if (include.Contains("$(", StringComparison.Ordinal) || include.Contains('*'))
                    return Fail("project-reference-unresolved", out failureReason);
                var absolute = Path.GetFullPath(Path.Combine(worktreePath,
                    project[..project.LastIndexOf('/')].Replace('/', Path.DirectorySeparatorChar),
                    include.Replace('\\', Path.DirectorySeparatorChar)));
                var relative = Path.GetRelativePath(worktreePath, absolute).Replace('\\', '/');
                if (!projects.Contains(relative))
                    return Fail("project-reference-missing", out failureReason);
                pending.Enqueue(relative);
            }
        }
        var lines = blobs.Where(pair => CandidateRootInputs.Contains(pair.Key) ||
                pair.Key.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                individualFiles.Contains(pair.Key) ||
                visited.Any(project => pair.Key.StartsWith(project[..(project.LastIndexOf('/') + 1)], StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}\0{pair.Value}\n");
        hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
        return true;
    }

    private static bool IsUsable(GitCli.GitResult result) =>
        result.Succeeded && !result.DrainTimedOut && result.ProcessStarted;

    private static bool Fail(string reason, out string failureReason)
    {
        failureReason = reason;
        return false;
    }
}
