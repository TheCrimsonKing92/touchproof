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
        bool refreshIndex;
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
            var sourceManifest = Path.Combine(source, "config", "acceptance-manifest.json");
            File.Copy(sourceManifest, manifest);
            if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(manifest, File.GetCreationTimeUtc(sourceManifest));
            File.SetLastWriteTimeUtc(manifest, File.GetLastWriteTimeUtc(sourceManifest));
            refreshIndex = !OperatingSystem.IsWindows() || !WorktreeTimestampsMatch(source, repository);
        }
        // Only matching Windows copies can reuse the recipe's index stat data.
        // Unix ctime cannot be copied; other inputs also retain the Git refresh.
        if (refreshIndex) MergeTrainRecheckFixtureTemplate.RefreshIndex(repository);
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
                InfrastructureTestSupport.RunGitWithCommitPostcondition(source, ["init", "--bare"]);
                recipes.Add(key, source);
            }
            CopyFiles(source, repository);
        }
    }

    internal void CopyPushedOrigin(string repository, string origin)
    {
        var git = Path.Combine(repository, ".git");
        var configPath = Path.Combine(git, "config");
        var config = File.ReadAllText(configPath);
        // Only the fresh remote setup used by the drift fixture is shareable. Keep Git
        // authoritative for pre-existing tracking state or an unreadable main ref.
        var main = string.Empty;
        var eligible = !File.Exists(Path.Combine(git, "packed-refs")) &&
            !Directory.Exists(Path.Combine(git, "refs", "remotes")) &&
            !Directory.Exists(Path.Combine(git, "logs", "refs", "remotes")) &&
            FixtureRefFileReader.TryRead(repository, "refs/heads/main", out main);
        var key = "pushed-origin:" + main + ":" +
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(config)));
        lock (sync)
        {
            if (eligible && recipes.TryGetValue(key, out var source))
            {
                CopyFiles(Path.Combine(source, "origin"), origin);
                File.WriteAllText(configPath, config + RemoteSection(origin));
                CopyFiles(Path.Combine(source, "refs"), Path.Combine(git, "refs", "remotes"));
                var logs = Path.Combine(source, "logs");
                if (Directory.Exists(logs)) CopyFiles(logs, Path.Combine(git, "logs", "refs", "remotes"));
                return;
            }

            CopyBareOrigin(origin);
            LandingExecutorTests.ReadGit(repository, "remote", "add", "origin", origin);
            LandingExecutorTests.ReadGit(repository, "push", "origin", "main");
            var tracking = Path.Combine(git, "refs", "remotes", "origin", "main");
            // Cache only the bytes actually produced by a real push, and only when the
            // config transformation round-trips exactly (including Git's URL escaping).
            if (!eligible || File.ReadAllText(configPath) != config + RemoteSection(origin) ||
                !File.Exists(tracking) || File.ReadAllText(tracking).Trim() != main ||
                !FixtureRefFileReader.TryRead(origin, "refs/heads/main", out var remoteMain) || remoteMain != main) return;

            source = Path.Combine(root, Guid.NewGuid().ToString("n"));
            CopyFiles(origin, Path.Combine(source, "origin"));
            CopyFiles(Path.Combine(git, "refs", "remotes"), Path.Combine(source, "refs"));
            var remoteLogs = Path.Combine(git, "logs", "refs", "remotes");
            if (Directory.Exists(remoteLogs)) CopyFiles(remoteLogs, Path.Combine(source, "logs"));
            recipes.Add(key, source);
        }
    }

    private static string RemoteSection(string origin)
    {
        var escaped = origin.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal).Replace("\b", "\\b", StringComparison.Ordinal);
        if (origin.Contains('#') || origin.Contains(';') || char.IsWhiteSpace(origin[0]) || char.IsWhiteSpace(origin[^1]))
            escaped = "\"" + escaped + "\"";
        return "[remote \"origin\"]\n\turl = " + escaped + "\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n";
    }

    internal void AddLandingCandidate(string repository, GoalId goal)
    {
        var git = Path.Combine(repository, ".git");
        var key = "landing-candidate:" + Fingerprint(git);
        bool refreshIndex;
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
            refreshIndex = !OperatingSystem.IsWindows() || !WorktreeTimestampsMatch(source, repository);
        }
        // GoalWorktrees.Ensure below this boundary still creates the real worktree.
        if (refreshIndex) MergeTrainRecheckFixtureTemplate.RefreshIndex(repository);
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

    private static bool WorktreeTimestampsMatch(string source, string target)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (name is ".git" or ".orchestrator" or ".orchestrator-worktrees") continue;
            var destination = Path.Combine(target, name);
            if (Directory.Exists(entry))
            {
                if (!WorktreeTimestampsMatch(entry, destination)) return false;
            }
            else
            {
                var expected = new FileInfo(entry);
                var actual = new FileInfo(destination);
                if (!actual.Exists || actual.Length != expected.Length ||
                    actual.CreationTimeUtc != expected.CreationTimeUtc ||
                    actual.LastWriteTimeUtc != expected.LastWriteTimeUtc) return false;
            }
        }
        return true;
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
