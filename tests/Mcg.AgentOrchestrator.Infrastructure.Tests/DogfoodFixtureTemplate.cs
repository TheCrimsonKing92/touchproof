using System.Threading;
using Mcg.AgentOrchestrator.Core;

// Only the immutable repository history is process-owned. Each fixture owns a byte copy
// and creates its kernel, goal, stores and evidence after copying.
internal static class DogfoodFixtureTemplate
{
    private static readonly Lazy<PreparedRepository> Template = new(
        Builder.Build, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string TemplateDirectory => Template.Value.Repo;

    internal static PreparedRepository Copy()
    {
        var source = Template.Value;
        var repo = Path.Combine(Builder.ProcessRoot, Guid.NewGuid().ToString("n"), "repo");
        try
        {
            Directory.CreateDirectory(repo);
            foreach (var directory in Directory.EnumerateDirectories(source.Repo, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(repo, Path.GetRelativePath(source.Repo, directory)));
            foreach (var file in Directory.EnumerateFiles(source.Repo, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(repo, Path.GetRelativePath(source.Repo, file)));
            return source with { Repo = repo };
        }
        catch
        {
            Builder.Remove(repo);
            throw;
        }
    }

    internal sealed record PreparedRepository(string Repo, string Main, string FirstCandidate,
        GoalId SecondGoalId, string SecondCandidate, string Promoted, string Tree);

    private sealed class Builder : AcceptanceCohortWorkflowTests
    {
        internal static string ProcessRoot => SeedRepositoryProcessRootPath;
        internal static void Remove(string repo) => DeleteDirectory(repo);

        internal static PreparedRepository Build()
        {
            // This container stays under the existing process-root janitor's ownership.
            var repo = CreateAcceptanceCohortRepository();
            try
            {
                var main = RunGitOutput(repo, "rev-parse", "main").Trim();
                var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
                var second = CreateCandidate(repo, "22222222222222222222222222222222", "src/Second.cs", "second");
                RunGit(repo, "merge", "--no-ff", "-m", "Combine first member", first.Revision);
                RunGit(repo, "merge", "--no-ff", "-m", "Combine second member", second.Revision);
                var promoted = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
                var tree = RunGitOutput(repo, "rev-parse", "HEAD^{tree}").Trim();
                return new(repo, main, first.Revision, second.GoalId, second.Revision, promoted, tree);
            }
            catch (Exception exception)
            {
                Remove(repo);
                throw new InvalidOperationException("Dogfood fixture template could not be prepared.", exception);
            }
        }
    }
}
