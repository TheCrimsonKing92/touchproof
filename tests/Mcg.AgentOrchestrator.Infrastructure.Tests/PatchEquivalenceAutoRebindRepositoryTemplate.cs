using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static PatchEquivalenceIntegrationMergeTestsAutoRebind;

public sealed class PatchEquivalenceAutoRebindRepositoryTemplate : IDisposable
{
    private readonly Dictionary<(string Merge, int Ceiling), Fixture> recipes = [];
    private readonly object sync = new();

    internal Fixture ReadOnly(string secondMerge = "writer", int boundCeiling = 20)
    {
        lock (sync)
        {
            var key = (secondMerge, boundCeiling);
            if (!recipes.TryGetValue(key, out var fixture))
            {
                fixture = PrepareRepository(secondMerge, boundCeiling);
                recipes.Add(key, fixture);
            }
            return fixture;
        }
    }

    internal Fixture Copy(string secondMerge = "writer", int boundCeiling = 20)
    {
        var source = ReadOnly(secondMerge, boundCeiling);
        var repo = Path.Combine(Path.GetDirectoryName(source.Repository)!, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(repo);
            DotnetBuildLandingFixtureLocks.RegisterCurrentLandingTestFixtureRoot(repo);
            foreach (var directory in Directory.EnumerateDirectories(source.Repository, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(repo, Path.GetRelativePath(source.Repository, directory)));
            foreach (var file in Directory.EnumerateFiles(source.Repository, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(repo, Path.GetRelativePath(source.Repository, file));
                File.Copy(file, target);
                if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(file));
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            }
            var worktree = MergeTrainRecheckFixtureTemplate.RepairWorktreeLinks(source.Repository, repo,
                "Auto-rebind fixture").Single();
            var kernel = AgentOrchestratorKernel.FromSnapshot(source.Kernel.ExportSnapshot());
            return new Fixture(repo, worktree, kernel, kernel.GetGoal(source.Goal.Id),
                source.Bound, source.Main, source.Candidate, source.Walked.ToArray());
        }
        catch
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var fixture in recipes.Values) fixture.Dispose();
    }
}
