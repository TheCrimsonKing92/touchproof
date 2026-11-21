using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

// Parallel-safe: every fact owns its repository; real-git selection is scoped to its async flow.
public sealed class FixtureRepositoryWriterTests
{
    private static readonly FixtureRepositoryWriter.Identity Identity =
        new("Fixture Tests", "fixtures@example.com", DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"));

    [Fact]
    public void Construction_NestedRootAndChild_WritesIndexWithoutLaunching()
    {
        WithRepository(root =>
        {
            using var launches = CountGitProbeLaunches();
            var writer = new FixtureRepositoryWriter(root);
            writer.Initialize("fixture");
            var first = writer.WriteSnapshot(new Dictionary<string, byte[]> { ["one.txt"] = "one\n"u8.ToArray() }, [], "root", Identity);
            var files = new Dictionary<string, byte[]>
            {
                ["one.txt"] = "one\n"u8.ToArray(), ["nested/deep/two.bin"] = [0, 1, 128, 255]
            };
            var child = writer.WriteSnapshot(files, [first], "child", Identity);
            writer.SelectTip("fixture", child);

            Assert.Equal(0, launches.Count);
            Assert.Equal("ref: refs/heads/fixture\n", File.ReadAllText(Path.Combine(root, ".git", "HEAD")));
            Assert.Equal(child + "\n", File.ReadAllText(Path.Combine(root, ".git", "refs", "heads", "fixture")));
            Assert.Equal(files["nested/deep/two.bin"], File.ReadAllBytes(Path.Combine(root, "nested", "deep", "two.bin")));
            var index = File.ReadAllBytes(Path.Combine(root, ".git", "index"));
            Assert.Equal("DIRC", Encoding.ASCII.GetString(index, 0, 4));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(4, 4)));
            Assert.True(writer.TryReadIndex(out var entries));
            Assert.Equal(files.Keys.Order(StringComparer.Ordinal), entries.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(writer.WriteBlob(files["nested/deep/two.bin"]), entries["nested/deep/two.bin"]);
            Assert.Contains("parent " + first + "\n", Encoding.UTF8.GetString(writer.ReadObject(child, "commit")));
        });
    }

    [Fact]
    public void Writer_Source_HasNoLaunchDependencies()
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(), "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "FixtureRepositoryWriter.cs");
        var source = File.ReadAllText(path);
        Assert.NotEmpty(source);
        Assert.False(Regex.IsMatch(source, @"\b(Process|ProcessStartInfo|GitCli|RunGitProbe|OwnedProcessGroup|HandleListProcessLauncher)\b"));
    }

    [Fact]
    public void WrittenHistory_RealGitReadback_IsCleanAndSupportsNextCommit()
    {
        WithRepository(root =>
        {
            var writer = new FixtureRepositoryWriter(root);
            writer.Initialize("fixture");
            File.AppendAllText(Path.Combine(root, ".git", "config"), "[user]\n\tname = Fixture Tests\n\temail = fixtures@example.com\n");
            var rootFiles = new Dictionary<string, byte[]> { ["root.txt"] = "root\n"u8.ToArray() };
            var first = writer.WriteSnapshot(rootFiles, [], "root", Identity);
            var secondFiles = new Dictionary<string, byte[]>(rootFiles) { ["dir/child.txt"] = "child\n"u8.ToArray() };
            var child = writer.WriteSnapshot(secondFiles, [first], "child", Identity);
            var sibling = writer.WriteSnapshot(rootFiles, [first], "sibling", Identity);
            var tipFiles = new Dictionary<string, byte[]>(secondFiles)
            {
                ["dir/deep/binary.bin"] = [0, 1, 127, 128, 255], ["empty.txt"] = []
            };
            var tip = writer.WriteSnapshot(tipFiles, [child, sibling], "merge", Identity);
            writer.SelectTip("fixture", tip);

            var fsck = RealGit(root, "fsck", "--strict");
            Assert.Equal("", fsck.StandardError);
            Assert.Equal(tip, RealGit(root, "rev-parse", "HEAD").StandardOutput.Trim());
            var commit = RealGit(root, "cat-file", "-p", tip).StandardOutput;
            Assert.StartsWith("tree ", commit);
            Assert.Contains($"parent {child}\nparent {sibling}\n", commit);
            Assert.Contains("author " + Identity.Header + "\ncommitter " + Identity.Header + "\n\nmerge\n", commit);
            var tree = commit.Split('\n')[0][5..];
            var treeBody = RealGit(root, "cat-file", "-p", tree).StandardOutput;
            Assert.Contains("040000 tree ", treeBody);
            Assert.Contains("\tdir\n", treeBody);
            Assert.Contains("100644 blob ", treeBody);
            var paths = RealGit(root, "ls-tree", "-r", "--name-only", "HEAD").StandardOutput.TrimEnd('\n').Split('\n');
            Assert.Equal(tipFiles.Keys.Order(StringComparer.Ordinal), paths);
            Assert.Equal("", RealGit(root, "status", "--porcelain").StandardOutput);
            foreach (var (path, bytes) in tipFiles)
                Assert.Equal(writer.WriteBlob(bytes), RealGit(root, "hash-object", path).StandardOutput.Trim());
            File.WriteAllText(Path.Combine(root, "next.txt"), "next\n");
            RealGit(root, "add", "next.txt");
            RealGit(root, "commit", "-m", "after writer");
            Assert.Equal(tip, RealGit(root, "rev-parse", "HEAD^").StandardOutput.Trim());
            Assert.Equal("", RealGit(root, "status", "--porcelain").StandardOutput);
        });
    }

    [Fact]
    public void LinkedWorktree_SelectTip_WritesSharedHistoryAndLeavesMainCheckoutAlone()
    {
        WithRepository(root =>
        {
            var main = Path.Combine(root, "main");
            var linked = Path.Combine(root, "linked");
            Directory.CreateDirectory(main);
            RealGit(main, "init", "-b", "main");
            RealGit(main, "config", "user.name", Identity.Name);
            RealGit(main, "config", "user.email", Identity.Email);
            File.WriteAllText(Path.Combine(main, "example.txt"), "main\n");
            RealGit(main, "add", "example.txt");
            RealGit(main, "commit", "-m", "Seed");
            RealGit(main, "worktree", "add", "-b", "linked", linked);

            var mainGit = Path.Combine(main, ".git");
            var mainHead = RealGit(main, "rev-parse", "HEAD").StandardOutput.Trim();
            var mainHeadBytes = File.ReadAllBytes(Path.Combine(mainGit, "HEAD"));
            var mainIndex = File.ReadAllBytes(Path.Combine(mainGit, "index"));
            var writer = new FixtureRepositoryWriter(linked);
            Assert.True(writer.IsLinkedWorktree);
            Assert.Equal(mainGit, writer.CommonDirectory);
            Assert.Equal(Path.GetFullPath(RealGit(linked, "rev-parse", "--absolute-git-dir").StandardOutput.Trim()), writer.GitDirectory);
            var tip = writer.WriteSnapshot(new Dictionary<string, byte[]> { ["example.txt"] = "linked\n"u8.ToArray() },
                [mainHead], "Linked snapshot", Identity);
            writer.SelectTip("linked", tip);

            Assert.Empty(RealGit(linked, "fsck", "--strict").StandardError);
            Assert.Equal(tip, RealGit(linked, "rev-parse", "HEAD").StandardOutput.Trim());
            Assert.Equal(tip, RealGit(main, "rev-parse", "refs/heads/linked").StandardOutput.Trim());
            Assert.Empty(RealGit(linked, "status", "--porcelain").StandardOutput);
            Assert.Equal(mainHead, RealGit(main, "rev-parse", "HEAD").StandardOutput.Trim());
            Assert.Equal(mainHeadBytes, File.ReadAllBytes(Path.Combine(mainGit, "HEAD")));
            Assert.Equal(mainIndex, File.ReadAllBytes(Path.Combine(mainGit, "index")));
        });
    }

    [Fact]
    public void IdenticalInputs_EmptyTreeAndOrderedParents_ProduceIdenticalIds()
    {
        WithRepository(firstRoot => WithRepository(secondRoot =>
        {
            string[] Build(string root)
            {
                var writer = new FixtureRepositoryWriter(root);
                writer.Initialize();
                var empty = writer.WriteSnapshot(new Dictionary<string, byte[]>(), [], "empty", Identity);
                var files = new Dictionary<string, byte[]> { ["dir/a.bin"] = [255, 0, 42], ["dir.txt"] = [] };
                var child = writer.WriteSnapshot(files, [empty], "binary", Identity);
                var merge = writer.WriteSnapshot(files, [empty, child], "ordered", Identity);
                var reversed = writer.WriteSnapshot(files, [child, empty], "ordered", Identity);
                writer.SelectTip("main", merge);
                Assert.NotEqual(merge, reversed);
                Assert.Equal("", RealGit(root, "fsck", "--strict").StandardError);
                return [empty, child, merge, reversed];
            }
            Assert.Equal(Build(firstRoot), Build(secondRoot));
        }));
    }

    private static WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult RealGit(string root, params string[] args)
    {
        using var real = InProcessGitFunnel.Observe(realOnly: true);
        var result = RunGitProbe(root, args);
        Assert.NotNull(result.ChildProcessId);
        Assert.True(result.Succeeded, result.ToString());
        Assert.Empty(real.Served);
        return result;
    }

    private static void WithRepository(Action<string> test)
    {
        var root = CreateTempDirectory();
        try { test(root); }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
