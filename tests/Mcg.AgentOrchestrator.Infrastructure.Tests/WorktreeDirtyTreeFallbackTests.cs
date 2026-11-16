using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each row owns its repository/configuration and never mutates process environment.
public sealed class WorktreeDirtyTreeFallbackTests
{
    [Theory]
    [InlineData("filter-gitattributes")]
    [InlineData("filter-info-attributes")]
    [InlineData("filter-global-attributes")]
    [InlineData("filter-unchanged-subtree")]
    [InlineData("crlf-text-attribute")]
    [InlineData("crlf-eol-attribute")]
    [InlineData("crlf-crlf-attribute")]
    [InlineData("crlf-global-autocrlf")]
    [InlineData("safecrlf-global")]
    [InlineData("symlink-entry")]
    [InlineData("submodule-entry")]
    [InlineData("sparse-checkout")]
    [InlineData("skip-worktree")]
    [InlineData("assume-unchanged")]
    [InlineData("unmerged-record")]
    [InlineData("ident")]
    [InlineData("working-tree-encoding")]
    [InlineData("attribute-macro")]
    [InlineData("attr-tree-global")]
    [InlineData("ignored-force-added")]
    [InlineData("ignored-rename-staged")]
    public void TryCompute_FallbackTrigger_EqualsOriginalThroughFourStepPath(string trigger)
    {
        var config = trigger.StartsWith("filter-", StringComparison.Ordinal)
            ? "[filter \"demo\"]\nclean = cat\nsmudge = cat\n"
            : trigger == "crlf-global-autocrlf" ? "[core]\nautocrlf = true\n"
            : trigger == "safecrlf-global" ? "[core]\nsafecrlf = true\n"
            : trigger == "attr-tree-global" ? "[attr]\ntree = HEAD\n" : null;
        using var fixture = new WorktreeTreeDigestFixture(config is null ? null : "");
        var tracked = Path.Combine(fixture.Repo, "tracked.txt");
        // Establish that this repository takes the new path before introducing the trigger.
        // This control fails on the original implementation (four launches for the same delta).
        File.WriteAllText(tracked, "baseline dirty\n");
        var baseline = fixture.OriginalDigest();
        Assert.Equal(baseline, fixture.Compute());
        Assert.InRange(fixture.Launches.Count, 1, 2);
        Assert.All(fixture.Launches.Skip(1), launch => Assert.Equal("ls-tree", launch.Arguments[0]));
        fixture.Git("checkout", "--", "tracked.txt");
        if (config is not null) File.WriteAllText(fixture.ReadEnvironment("GIT_CONFIG_GLOBAL")!, config);
        switch (trigger)
        {
            case "ignored-force-added":
            case "ignored-rename-staged":
                Directory.CreateDirectory(Path.Combine(fixture.Repo, "ignored"));
                if (trigger == "ignored-force-added")
                {
                    File.WriteAllText(Path.Combine(fixture.Repo, "ignored", "indexed.txt"), "forced addition\n");
                    fixture.Git("add", "-f", "ignored/indexed.txt");
                }
                else fixture.Git("mv", "tracked.txt", "ignored/indexed.txt");
                var ignoredStatus = fixture.Git("status", "--porcelain=v2", "-z", "--untracked-files=all");
                Assert.StartsWith(trigger == "ignored-force-added" ? "1 A." : "2 R.", ignoredStatus);
                Assert.Contains("ignored/indexed.txt", ignoredStatus);
                Assert.Equal("ignored/indexed.txt", fixture.Git("check-ignore", "--no-index", "ignored/indexed.txt").Trim());
                Assert.Empty(fixture.Git("ls-tree", "HEAD", "ignored/indexed.txt"));
                break;
            case "filter-gitattributes":
                File.WriteAllText(Path.Combine(fixture.Repo, ".gitattributes"), "*.txt filter=demo\n");
                CommitAttributes(fixture);
                break;
            case "filter-info-attributes":
                Directory.CreateDirectory(Path.Combine(fixture.Repo, ".git", "info"));
                File.WriteAllText(Path.Combine(fixture.Repo, ".git", "info", "attributes"), "*.txt filter=demo\n");
                break;
            case "filter-global-attributes":
                Directory.CreateDirectory(Path.Combine(fixture.Root, "git"));
                File.WriteAllText(Path.Combine(fixture.Root, "git", "attributes"), "*.txt filter=demo\n");
                break;
            case "filter-unchanged-subtree":
                var subtree = Directory.CreateDirectory(Path.Combine(fixture.Repo, "filtered")).FullName;
                File.WriteAllText(Path.Combine(subtree, ".gitattributes"), "*.txt filter=demo\n");
                File.WriteAllText(Path.Combine(subtree, "unchanged.txt"), "unchanged\n");
                fixture.Git("add", "-A");
                fixture.Git("commit", "-m", "filtered subtree");
                break;
            case "crlf-text-attribute":
            case "crlf-eol-attribute":
            case "crlf-crlf-attribute":
                var attribute = trigger == "crlf-text-attribute" ? "text" : trigger == "crlf-eol-attribute" ? "eol=lf" : "crlf";
                File.WriteAllText(Path.Combine(fixture.Repo, ".gitattributes"), "*.txt " + attribute + "\n");
                CommitAttributes(fixture);
                break;
            case "symlink-entry":
            case "submodule-entry":
                var oid = trigger == "symlink-entry" ? fixture.Git("hash-object", "-w", "tracked.txt").Trim()
                    : fixture.Git("rev-parse", "HEAD").Trim();
                var mode = trigger == "symlink-entry" ? "120000" : "160000";
                fixture.Git("update-index", "--add", "--cacheinfo", mode + "," + oid + ",special");
                fixture.Git("commit", "-m", "special entry");
                Assert.Contains(mode, fixture.Git("ls-tree", "HEAD", "special"));
                break;
            case "sparse-checkout":
                Directory.CreateDirectory(Path.Combine(fixture.Repo, ".git", "info"));
                File.WriteAllText(Path.Combine(fixture.Repo, ".git", "info", "sparse-checkout"), "/*\n");
                File.AppendAllText(Path.Combine(fixture.Repo, ".git", "config"), "\n[core]\nsparseCheckout = true\n");
                break;
            case "skip-worktree":
                fixture.Git("update-index", "--skip-worktree", "tracked.txt");
                Assert.StartsWith("S ", fixture.Git("ls-files", "-v", "tracked.txt"));
                break;
            case "assume-unchanged":
                fixture.Git("update-index", "--assume-unchanged", "tracked.txt");
                Assert.StartsWith("h ", fixture.Git("ls-files", "-v", "tracked.txt"));
                break;
            case "unmerged-record":
                fixture.Git("checkout", "-b", "other");
                File.WriteAllText(tracked, "other branch\n");
                fixture.Git("add", "-A");
                fixture.Git("commit", "-m", "other edit");
                fixture.Git("checkout", "fixture-main");
                File.WriteAllText(tracked, "main branch\n");
                fixture.Git("add", "-A");
                fixture.Git("commit", "-m", "main edit");
                fixture.GitFails("merge", "other");
                Assert.StartsWith("u ", fixture.Git("status", "--porcelain=v2", "-z"));
                break;
            case "ident":
            case "working-tree-encoding":
            case "attribute-macro":
                var assignment = trigger == "ident" ? "*.txt ident\n" : trigger == "working-tree-encoding"
                    ? "*.txt working-tree-encoding=UTF-8\n" : "[attr]custom text\n*.txt custom\n";
                File.WriteAllText(Path.Combine(fixture.Repo, ".gitattributes"), assignment);
                CommitAttributes(fixture);
                break;
        }
        if (trigger is not ("unmerged-record" or "ignored-rename-staged"))
            File.WriteAllText(tracked, trigger.StartsWith("crlf-", StringComparison.Ordinal) ? "modified\r\n" : "modified\n");
        // Skip/assume bits can hide the modified tracked file entirely; force a dirty status too.
        if (trigger is "skip-worktree" or "assume-unchanged" or "crlf-global-autocrlf")
            File.WriteAllText(Path.Combine(fixture.Repo, "untracked"), trigger == "crlf-global-autocrlf" ? "new\r\n" : "new\n");
        Assert.NotEmpty(fixture.Git("status", "--porcelain=v2", "-z", "--untracked-files=all"));
        var expected = fixture.OriginalDigest();
        Assert.Equal(expected, fixture.Compute());
        var launches = fixture.Launches.Select(launch => launch.Arguments[0]).ToArray();
        Assert.Equal(["read-tree", "add", "write-tree"], launches[^3..]);
        Assert.Equal(trigger == "sparse-checkout" ? "rev-parse" : "status", launches[0]);
        Assert.Equal(trigger == "sparse-checkout" ? 0 : 1, launches.Count(launch => launch == "status"));
        Assert.InRange(launches.Length, 4, 5); // A HEAD-only trigger may be discovered by ls-tree.
    }

    [Theory]
    [InlineData("failed-launch")]
    [InlineData("timed-out-launch")]
    [InlineData("malformed-listing")]
    [InlineData("wrong-tree-id")]
    [InlineData("wrong-oid-length")]
    [InlineData("unknown-status")]
    [InlineData("unreadable-file")]
    public void TryCompute_UnsafeInput_DeclinesBeforeReturningDigest(string shape)
    {
        using var fixture = new WorktreeTreeDigestFixture();
        File.WriteAllText(Path.Combine(fixture.Repo, "tracked.txt"), "modified\n");
        var status = fixture.Git("status", "--porcelain=v2", "-z", "--untracked-files=all");
        if (shape == "unknown-status") status = "x unsupported\0";
        // Unique tree prevents any other row from satisfying this row's runner through the cache.
        File.WriteAllText(Path.Combine(fixture.Repo, "unique"), Guid.NewGuid().ToString("N"));
        fixture.Git("add", "unique");
        fixture.Git("commit", "-m", "unique tree");
        Assert.True(GitRefFileResolver.TryResolve(fixture.Repo, "HEAD^{tree}", out var head, fixture.ReadEnvironment));
        var listing = fixture.Git("ls-tree", "-r", "-t", "-z", "--full-tree", head!);
        var calls = 0;
        WorktreeTreeDigestGitStep Run(string root, Dictionary<string, string>? env, string[] args)
        {
            Assert.Equal("ls-tree", args[0]);
            calls++;
            return shape switch
            {
                "failed-launch" => new(1, "", "injected failure", false),
                "timed-out-launch" => new(-1, "", "injected timeout", true),
                "malformed-listing" => new(0, "malformed\0", "", false),
                "wrong-tree-id" => new(0, listing.Replace(
                    listing.Split('\t')[0].Split(' ')[2], new string('0', head!.Length), StringComparison.Ordinal), "", false),
                "wrong-oid-length" => new(0, listing.Replace(
                    listing.Split('\t')[0].Split(' ')[2], new string('0', 64), StringComparison.Ordinal), "", false),
                _ => new(0, listing, "", false)
            };
        }
        using var locked = shape == "unreadable-file"
            ? new FileStream(Path.Combine(fixture.Repo, "tracked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        Assert.False(WorktreeDirtyTreeHasher.TryCompute(fixture.Repo, status, fixture.ReadEnvironment,
            Run, (_, _) => head, out var result));
        Assert.Empty(result);
        Assert.Equal(shape == "unknown-status" ? 0 : 1, calls);
    }

    private static void CommitAttributes(WorktreeTreeDigestFixture fixture)
    {
        fixture.Git("add", ".gitattributes");
        fixture.Git("commit", "-m", "attribute source");
    }
}
