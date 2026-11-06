using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// A recipe owns immutable history. Callers own the entire writable repository and stores.
internal static class GoalBranchScenarioTemplate
{
    internal enum BaseKind { GoalWorktreeDefault, GoalWorktreeMain, CliAcceptance }
    private sealed record Recipe(string Path, string Content, string Message, BaseKind Kind);
    private sealed record Prepared(string Root, Dictionary<string, byte[]> BaseGit);
    internal static readonly GoalId TemplateGoalId = new("b7a9c301000000000000000000000000");
    private static readonly ConcurrentDictionary<Recipe, Lazy<Prepared>> Templates = new();

    private static Prepared Template(Recipe recipe) => Templates.GetOrAdd(recipe,
        key => new Lazy<Prepared>(() => Build(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    internal static string TemplateDirectory(BaseKind kind, string path, string content, string message) =>
        Template(new(path, content, message, kind)).Root;

    internal static string Materialize(string root, BaseKind kind, GoalId goalId,
        string path, string content, string message) =>
        TryMaterialize(root, kind, goalId, path, content, message) ??
        throw new InvalidOperationException("Goal-branch scenario requires a pristine seeded repository.");

    internal static string? TryMaterialize(string root, BaseKind kind, GoalId goalId,
        string path, string content, string message)
    {
        if (Directory.Exists(Path.Combine(root, GoalWorktrees.DirectoryName))) return null;
        var source = Template(new(path, content, message, kind));
        var gitRoot = Path.Combine(root, ".git");
        if (!Directory.Exists(gitRoot)) return null;
        var current = Snapshot(gitRoot);
        if (current.Count != source.BaseGit.Count || source.BaseGit.Any(pair =>
            !current.TryGetValue(pair.Key, out var bytes) || !bytes.AsSpan().SequenceEqual(pair.Value)))
            return null;

        var oldPrefix = GoalWorktrees.BranchName(TemplateGoalId)["goal/".Length..];
        var newPrefix = GoalWorktrees.BranchName(goalId)["goal/".Length..];
        string Map(string relative) => string.Join(Path.DirectorySeparatorChar,
            relative.Split(Path.DirectorySeparatorChar).Select(part => part == oldPrefix ? newPrefix : part));

        // Overlay only .git and the new worktree; state.db and caller files stay at their original root.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source.Root, ".git"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source.Root, file);
            var bytes = File.ReadAllBytes(file);
            if (!relative.StartsWith(".git" + Path.DirectorySeparatorChar + "objects" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) && Path.GetFileName(file) != "index")
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                bytes = System.Text.Encoding.UTF8.GetBytes(text.Replace(
                    GoalWorktrees.BranchName(TemplateGoalId), GoalWorktrees.BranchName(goalId), StringComparison.Ordinal));
            }
            WriteCopy(file, Path.Combine(root, Map(relative)), bytes);
        }
        var oldWorktree = GoalWorktrees.WorktreePath(source.Root, TemplateGoalId);
        var worktree = GoalWorktrees.WorktreePath(root, goalId);
        foreach (var directory in Directory.EnumerateDirectories(oldWorktree, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(worktree, Path.GetRelativePath(oldWorktree, directory)));
        foreach (var file in Directory.EnumerateFiles(oldWorktree, "*", SearchOption.AllDirectories))
            WriteCopy(file, Path.Combine(worktree, Path.GetRelativePath(oldWorktree, file)), File.ReadAllBytes(file));

        MergeTrainRecheckFixtureTemplate.RepairWorktreeLinks(source.Root, root, "Goal-branch scenario template", Map);
        MergeTrainRecheckFixtureTemplate.RefreshIndex(worktree);
        MergeTrainRecheckFixtureTemplate.RefreshIndex(root);
        return worktree;
    }

    private static void WriteCopy(string source, string target, byte[] bytes)
    {
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        File.SetAttributes(target, File.GetAttributes(source));
        File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(source));
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
    }

    private static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(root, file), File.ReadAllBytes, StringComparer.Ordinal);

    private static Prepared Build(Recipe recipe)
    {
        var root = Builder.CreateBase(recipe.Kind);
        try
        {
            var baseline = Snapshot(Path.Combine(root, ".git"));
            Builder.HandBuild(root, TemplateGoalId, recipe.Path, recipe.Content, recipe.Message);
            var branchRef = Path.Combine(root, ".git", "refs", "heads", "goal",
                GoalWorktrees.BranchName(TemplateGoalId)["goal/".Length..]);
            if (!File.Exists(branchRef) || Directory.Exists(Path.Combine(root, ".git", "reftable")))
                throw new InvalidOperationException("Goal-branch scenario requires loose goal refs and file-based worktree metadata.");
            return new(root, baseline);
        }
        catch (Exception exception)
        {
            Builder.Remove(root);
            throw new InvalidOperationException($"Goal-branch recipe '{recipe}' could not be prepared.", exception);
        }
    }

    private sealed class Builder : GoalWorktreeTestBase
    {
        internal static string CreateBase(BaseKind kind)
        {
            if (kind != BaseKind.CliAcceptance)
                return CreateReducedAcceptanceCohortRepository(kind == BaseKind.GoalWorktreeMain);
            // The existing process-root janitor owns recipe lifetime for both base kinds.
            var root = Path.Combine(EnsureSeedRepositoryProcessRoot(), Guid.NewGuid().ToString("n"), "repo");
            Directory.CreateDirectory(root);
            CliAcceptanceGitRepositoryTemplate.CopyTo(root);
            return root;
        }

        internal static void HandBuild(string root, GoalId goalId, string path, string content, string message)
        {
            var worktree = GoalWorktrees.Ensure(root, goalId);
            var file = Path.Combine(worktree, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", message);
        }

        internal static void Remove(string root) => DeleteDirectory(root);
    }
}
