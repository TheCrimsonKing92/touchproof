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
                File.Copy(file, Path.Combine(repo, Path.GetRelativePath(source.Repo, file)));

            foreach (var worktree in MergeTrainRecheckFixtureTemplate.RepairWorktreeLinks(
                source.Repo, repo, $"Candidate template '{key}'"))
                MergeTrainRecheckFixtureTemplate.RefreshIndex(worktree);
            // Copied stat data must be refreshed before read-tree -m -u.
            MergeTrainRecheckFixtureTemplate.RefreshIndex(repo);
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
            return new PreparedRepository(repo, ids, revisions);
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
        internal static string Revision(string repo, string revision) => RunGitOutput(repo, "rev-parse", revision).Trim();
        internal static void Remove(string repo) => DeleteDirectory(repo);
    }
}
