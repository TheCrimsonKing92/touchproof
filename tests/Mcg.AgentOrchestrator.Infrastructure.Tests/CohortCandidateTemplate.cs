using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// One xUnit class fixture owns immutable recipes; every caller owns a writable byte copy.
// Preparation is lazy so it runs inside the first test's capacity lease and duration.
public sealed class CohortCandidateTemplate : IDisposable
{
    private readonly Dictionary<string, Lazy<PreparedRepository>> recipes = new(StringComparer.Ordinal);
    private readonly object sync = new();

    internal PreparedRepository Copy(string key, int count, Action<string, GoalId[]> prepare)
    {
        PreparedRepository source;
        lock (sync)
        {
            if (!recipes.TryGetValue(key, out var recipe))
            {
                recipe = new Lazy<PreparedRepository>(() => Build(key, count, prepare));
                recipes.Add(key, recipe);
            }
            source = recipe.Value;
        }

        var repo = Path.Combine(Builder.ProcessRoot, Guid.NewGuid().ToString("n"), "repo");
        try
        {
            Directory.CreateDirectory(repo);
            foreach (var directory in Directory.EnumerateDirectories(source.Repo, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(repo, Path.GetRelativePath(source.Repo, directory)));
            foreach (var file in Directory.EnumerateFiles(source.Repo, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(repo, Path.GetRelativePath(source.Repo, file));
                File.Copy(file, target);
                // Git for Windows records creation time as ctime. Preserve both timestamps,
                // as the existing recheck template does, so candidate indexes stay valid.
                if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(file));
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            }

            // Prepared index bytes describe the source timestamps, which Windows copies preserve.
            // The original recipe indexes remain untouched even when preparation left them stale.
            foreach (var (path, contents) in source.CopyIndexes)
                File.WriteAllBytes(Path.Combine(repo, path), contents);

            foreach (var worktree in MergeTrainRecheckFixtureTemplate.RepairWorktreeLinks(
                source.Repo, repo, $"Candidate template '{key}'"))
                // Unix ctime cannot be copied. Keep the authoritative refresh on those hosts.
                if (!OperatingSystem.IsWindows()) MergeTrainRecheckFixtureTemplate.RefreshIndex(worktree);
            // Link repair touches only untracked .git metadata. Preserved Windows file
            // timestamps also keep the root index valid; Unix ctime still needs a refresh.
            if (!OperatingSystem.IsWindows()) MergeTrainRecheckFixtureTemplate.RefreshIndex(repo);
            return new PreparedRepository(repo, source.GoalIds.ToArray(), source.Revisions.ToArray());
        }
        catch
        {
            Builder.Remove(repo);
            throw;
        }
    }

    internal string TemplatePath(string key) => recipes[key].Value.Repo;

    private static PreparedRepository Build(string key, int count, Action<string, GoalId[]> prepare)
    {
        var repo = Builder.CreateRepository();
        try
        {
            // IDs belong to this fixture recipe; separate class fixtures cannot collide.
            var ids = Enumerable.Range(0, count).Select(_ => GoalId.New()).ToArray();
            prepare(repo, ids);
            var revisions = ids.Select(id => Builder.Revision(repo, GoalWorktrees.BranchName(id))).ToArray();
            var indexes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                PrepareIndex(repo, Path.Combine(repo, ".git", "index"));
                foreach (var entry in Directory.EnumerateDirectories(Path.Combine(repo, ".git", "worktrees")))
                {
                    var gitFile = File.ReadAllText(Path.Combine(entry, "gitdir")).Trim();
                    PrepareIndex(Path.GetDirectoryName(gitFile)!, Path.Combine(entry, "index"));
                }
            }
            return new PreparedRepository(repo, ids, revisions) { CopyIndexes = indexes };

            void PrepareIndex(string worktree, string index)
            {
                // Refresh against the real files through a separate index, never mutating the recipe.
                var temporary = index + ".copy-" + Guid.NewGuid().ToString("n");
                try
                {
                    File.Copy(index, temporary);
                    // RunGitProbe deliberately strips repository selectors from commandEnvironment.
                    // Supply the complete allowlisted environment so this index, rather than the
                    // immutable recipe's real index, receives the refresh.
                    var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
                        if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
                    environment["GIT_INDEX_FILE"] = temporary;
                    environment["GIT_CONFIG_NOSYSTEM"] = "1";
                    environment["GIT_CONFIG_GLOBAL"] = "NUL";
                    environment["GIT_TERMINAL_PROMPT"] = "0";
                    environment["GCM_INTERACTIVE"] = "Never";
                    environment["GIT_OPTIONAL_LOCKS"] = "0";
                    environment["LC_ALL"] = "C";
                    environment["LANG"] = "C";
                    var result = InfrastructureTestSupport.RunGitProbe(worktree,
                        ["update-index", "--refresh"],
                        verificationEnvironment: environment);
                    InfrastructureTestSupport.RequireCompleteGitOutput(result);
                    if (!result.Succeeded)
                        throw new InvalidOperationException($"Candidate index preparation failed: {result.StandardError}");
                    indexes.Add(Path.GetRelativePath(repo, index), File.ReadAllBytes(temporary));
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
        }
        catch (Exception exception)
        {
            Builder.Remove(repo);
            throw new InvalidOperationException($"Candidate template '{key}' could not be prepared.", exception);
        }
    }

    public void Dispose()
    {
        foreach (var recipe in recipes.Values)
            if (recipe.IsValueCreated) Builder.Remove(recipe.Value.Repo);
    }

    internal sealed record PreparedRepository(string Repo, GoalId[] GoalIds, string[] Revisions)
    {
        internal IReadOnlyDictionary<string, byte[]> CopyIndexes { get; init; } = new Dictionary<string, byte[]>();

        internal Goal CreateCompletedGoal(AgentOrchestratorKernel kernel, int index, string objective)
        {
            var goal = kernel.CreateGoal(GoalIds[index], objective,
                [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id,
                ManualVerificationRecorder.Create(true, "Passed.", Repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);
            return goal;
        }
    }

    private sealed class Builder : AcceptanceCohortWorkflowTests
    {
        internal static string ProcessRoot => SeedRepositoryProcessRootPath;
        internal static string CreateRepository() => CreateReducedAcceptanceCohortRepository();
        internal static string Revision(string repo, string revision) => FixtureRefFileReader.Read(repo, revision);
        internal static void Remove(string repo) => DeleteDirectory(repo);
    }
}
