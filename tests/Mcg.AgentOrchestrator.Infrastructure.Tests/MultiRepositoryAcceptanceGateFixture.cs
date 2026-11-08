using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

internal sealed class MultiRepositoryAcceptanceGateFixture : IDisposable
{
    internal GoalRepositoriesTestFixture Repositories { get; } = new();
    internal Goal Goal { get; }
    internal string HomeWorktree { get; }
    internal string SecondaryWorktree { get; }
    internal string SecondaryManifest { get; }
    internal List<(string Key, string Directory)> Calls { get; } = [];
    internal bool FailSecondary { get; set; }
    internal bool FailHome { get; set; }
    internal Action<string>? OnCheck { get; set; }
    internal List<string> RetryMessages { get; } = [];
    internal int LandingCalls { get; private set; }
    internal IGoalAcceptanceVerifier Verifier { get; }
    internal MultiRepositoryAcceptanceGate Gate { get; }

    internal MultiRepositoryAcceptanceGateFixture()
    {
        foreach (var repository in new[] { Repositories.Home, Repositories.Crosswalk })
        {
            File.WriteAllText(Path.Combine(repository.CheckoutPath, ".gitignore"), ".orchestrator/\n");
            repository.Git("add", ".gitignore");
            repository.Git("commit", "-m", "Ignore case-owned runtime state");
        }
        var config = Path.Combine(Repositories.Home.CheckoutPath, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), Manifest("home check", "--info"));
        Repositories.Home.Git("add", "config/acceptance-manifest.json");
        Repositories.Home.Git("commit", "-m", "Seed home manifest");
        var projectHome = MultiRepositoryProjectVerifier.ProjectHome(Repositories.Registry, "crosswalk");
        Directory.CreateDirectory(projectHome);
        SecondaryManifest = Path.Combine(projectHome, "acceptance-manifest.json");
        File.WriteAllText(SecondaryManifest, Manifest("secondary check", "--version"));
        Goal = Repositories.CreateGoal();
        HomeWorktree = Repositories.CreateHomeWorktree(Goal);
        Assert.True(GoalRepositoryDispatchProvisioning.TryProvision(Goal, Goal.Tasks.Single(), out var failure), failure);
        SecondaryWorktree = Repositories.Crosswalk.WorktreePath(Goal.Id, "crosswalk");
        Assert.False(File.Exists(Path.Combine(SecondaryWorktree, "config", "acceptance-manifest.json")));
        ConductorDriverTests.PassVerification(Repositories.Kernel, Goal, Goal.Tasks.Single());
        Verifier = new GoalAcceptanceVerifier(new GoalAcceptanceVerifierTestOverrides
        {
            BuildStorageRootForTests = new DotnetBuildStorageRoot(Path.Combine(Repositories.Home.Root, "dotnet"))
        }, (arguments, directory, _, _) =>
        {
            Assert.Equal("dotnet", arguments[0]);
            var key = arguments.Contains("--info") ? "default" : "crosswalk";
            Assert.Contains(key == "default" ? "--info" : "--version", arguments);
            Calls.Add((key, directory));
            OnCheck?.Invoke(key);
            var failed = key == "default" ? FailHome : FailSecondary;
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(failed ? 1 : 0,
                failed ? "fixture check failed" : "fixture check passed"));
        });
        Gate = new(Repositories.Workspace, Repositories.Registry, Verifier);
    }

    internal ConductorDriver Driver()
    {
        var kernel = Repositories.Kernel;
        var driver = new ConductorDriver(_ => GoalLifecycleFacts.None, () => 0, _ => HomeWorktree,
            _ => DispatchStartOutcome.Started(), null, null, _ => RunHome(), null,
            (goal, task, message) => { RetryMessages.Add(message); return kernel.RetryTaskAutomatically(goal, task, message, RetryCause.CriterionEvidenceOwnerMismatch); },
            null, kernel.RecordCriterionRetryFeedback, null,
            _ => throw new InvalidOperationException("Multi-repository acceptance must not rebase a single branch."),
            (_, _) => { LandingCalls++; throw new InvalidOperationException("Multi-repository goals must not land."); },
            null, _ => { }, _ => throw new InvalidOperationException("Multi-repository goals must not clean up."),
            (_, _, _) => { }, _ => null,
            recordAcceptanceFailure: (goal, checks, branch, main) => kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main),
            clearAcceptanceFailure: goal => kernel.ClearAcceptanceFailure(goal.Id),
            parallelAcceptanceAttemptCoordinator: new(Path.Combine(Repositories.Home.Root, "attempts"), "main", runInline: true));
        ConductorGateAttention.Configure(driver, Repositories.Workspace, Verifier, _ => null, Repositories.Registry);
        return driver;
    }

    internal CliExecutionContext Context() => new(Repositories.Kernel, Repositories.Workspace,
        new InMemoryModelProviderRegistry([]), Repositories.Agents, Repositories.Profiles, null)
        { ProjectRegistry = Repositories.Registry, AcceptanceVerifier = Verifier };

    internal RepositoryCandidateTuple Tuple()
    {
        var candidate = GoalRepositoryCandidateIdentity.Compute(Goal, Repositories.Workspace, Repositories.Registry);
        Assert.True(RepositoryCandidateTuple.TryParse(candidate.Identity?.RepositoryTupleCanonical, out var tuple), candidate.Failure);
        return tuple!;
    }

    internal AcceptanceVerificationSummary RunHome()
    {
        var result = AcceptanceExecutionRunner.RunAttempt(Verifier, HomeWorktree, Goal.Id, [], null, null,
            CancellationToken.None, new AcceptanceRunExecutionOptions(IntegrationBranch: "main",
                ProjectHomeDirectory: Repositories.Workspace.ProjectHomeDirectoryOrNull));
        var failed = result.Checks?.Where(check => !check.Passed).ToArray() ?? [];
        return AcceptanceVerificationSummary.FromVerification(result, failed, failed.Select(check => check.Name).ToArray(),
            null, null, result.TestResultPaths, null, null);
    }

    internal static string Manifest(string name, string argument) => JsonSerializer.Serialize(new
    {
        version = 1, engine = new { enforceStructuralCoverage = false },
        checks = new[] { new { name, type = "command", command = "dotnet", arguments = new[] { argument } } },
        forbiddenChangedPathGlobs = Array.Empty<string>()
    });

    public void Dispose() => Repositories.Dispose();
}
