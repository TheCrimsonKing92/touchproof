using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// xUnit class-fixture lifetime; recipes and every copy have independent writable files.
public sealed class WorktreeSetupTemplate : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, string> recipes = new(StringComparer.Ordinal);
    private readonly string root = Directory.CreateTempSubdirectory("worktree-setup-recipes-").FullName;

    internal bool TryAddManifest(string repository, string contents, Action<string> handBuild)
    {
        var git = Path.Combine(repository, ".git");
        if (!Directory.Exists(git) || Directory.Exists(Path.Combine(git, "worktrees")) ||
            !GitPathQuery.TryQuery(repository, ["--git-dir"], _ => null, out _) ||
            File.Exists(Path.Combine(repository, "config", "acceptance-manifest.json"))) return false;
        // Exact input bytes are the admission guard. Different refs, config, index or logs get
        // a different recipe, so a mutated repository can never receive a stale setup overlay.
        var key = Fingerprint(git) + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)));
        string source;
        lock (sync)
        {
            if (!recipes.TryGetValue(key, out source!))
            {
                source = Path.Combine(root, Guid.NewGuid().ToString("n"));
                CopyFiles(repository, source, excludeStores: true);
                handBuild(source);
                recipes.Add(key, source);
            }
            CopyFiles(Path.Combine(source, ".git"), git);
            var manifest = Path.Combine(repository, "config", "acceptance-manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            File.Copy(Path.Combine(source, "config", "acceptance-manifest.json"), manifest);
        }
        MergeTrainRecheckFixtureTemplate.RefreshIndex(repository);
        return true;
    }

    internal void CopyTerminalSeed(string repository)
    {
        const string key = "terminal-seed";
        lock (sync)
        {
            if (!recipes.TryGetValue(key, out var source))
            {
                source = Path.Combine(root, key);
                Directory.CreateDirectory(source);
                HandBuildTerminalSeed(source);
                recipes.Add(key, source);
            }
            CopyFiles(source, repository);
        }
        if (!OperatingSystem.IsWindows()) MergeTrainRecheckFixtureTemplate.RefreshIndex(repository);
    }

    internal void CopyBareOrigin(string repository)
    {
        lock (sync)
        {
            const string key = "bare-origin";
            if (!recipes.TryGetValue(key, out var source))
            {
                source = Directory.CreateDirectory(Path.Combine(root, key)).FullName;
                var result = GitCli.Run(source, "init", "--bare");
                Assert.True(result.Succeeded, result.Error);
                recipes.Add(key, source);
            }
            CopyFiles(source, repository);
        }
    }

    internal void AddLandingCandidate(string repository, GoalId goal)
    {
        var git = Path.Combine(repository, ".git");
        var key = "landing-candidate:" + Fingerprint(git);
        lock (sync)
        {
            if (!recipes.TryGetValue(key, out var source))
            {
                source = Path.Combine(root, Guid.NewGuid().ToString("n"));
                CopyFiles(repository, source, excludeStores: true);
                LandingExecutorTests.AddGoalBranchCommit(source,
                    GoalWorktrees.BranchName(GoalBranchScenarioTemplate.TemplateGoalId), "src/delivery.txt", "goal work");
                recipes.Add(key, source);
            }
            var oldBranch = GoalWorktrees.BranchName(GoalBranchScenarioTemplate.TemplateGoalId);
            var newBranch = GoalWorktrees.BranchName(goal);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(source, ".git"), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(repository, relative.Replace(oldBranch.Replace('/', Path.DirectorySeparatorChar),
                    newBranch.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                if (relative.StartsWith(".git" + Path.DirectorySeparatorChar + "objects" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) || Path.GetFileName(file) == "index") File.Copy(file, target, overwrite: true);
                else File.WriteAllText(target, File.ReadAllText(file).Replace(oldBranch, newBranch, StringComparison.Ordinal));
            }
        }
        // The caller's seed files have their own filesystem identity. Refresh that index;
        // GoalWorktrees.Ensure below the fixture boundary still creates the real worktree.
        MergeTrainRecheckFixtureTemplate.RefreshIndex(repository);
    }

    internal static void HandBuildTerminalSeed(string repository)
    {
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@example.com");
        Git("config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(repository, "README.md"), "seed");
        Git("add", "-A");
        Git("commit", "-m", "Seed");
        void Git(params string[] args)
        {
            var result = GitCli.RunWithEnvironment(repository, new Dictionary<string, string>
            {
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
                ["GIT_AUTHOR_DATE"] = "2026-01-01T00:00:00Z", ["GIT_COMMITTER_DATE"] = "2026-01-01T00:00:00Z"
            }, args);
            Assert.True(result.Succeeded, result.Error);
        }
    }

    private static string Fingerprint(string git)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(git, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(git, file));
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            var bytes = File.ReadAllBytes(file);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void CopyFiles(string source, string target, bool excludeStores = false)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (excludeStores && name is ".orchestrator" or ".orchestrator-worktrees") continue;
            var destination = Path.Combine(target, name);
            if (Directory.Exists(entry)) CopyFiles(entry, destination);
            else
            {
                // Git objects copied into an existing repository may already be read-only.
                // Clear only the destination's bit; File.Copy preserves the recipe's attributes.
                if (File.Exists(destination))
                    File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                File.Copy(entry, destination, overwrite: true);
                File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(entry));
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(entry));
            }
        }
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
