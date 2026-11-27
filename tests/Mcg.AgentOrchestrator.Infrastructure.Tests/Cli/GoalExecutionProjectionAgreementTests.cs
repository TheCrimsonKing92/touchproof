using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class GoalExecutionProjectionAgreementTests
{
    [Fact]
    public void Preflight_MissingWorkspace_ReportsWorkspaceMissingBlocker()
    {
        var (kernel, _, goal, agents) = ReadinessTestSeed.Create();
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var profiles = WorkerProfileCatalog.Default();
        var now = ReadinessTestSeed.Now;
        var resolutions = 0;
        string? ResolveWorktree(string _, GoalId __)
        {
            resolutions++;
            return null;
        }

        var report = GoalReadinessPreflight.Build(goal, agents, root, profiles, ResolveWorktree,
            kernel.ProviderHoldScope, now);
        var conductor = ConductorDriver.EvaluateConductorReadiness(kernel, goal, agents, profiles, now,
            executionDirectory: root, resolveWorktree: ResolveWorktree);

        Assert.Equal(2, resolutions);
        Assert.True(report.RequiresWorkspace);
        Assert.False(report.HasWorkspace);
        var finding = Assert.Single(report.Findings, item => item.Kind == "workspace-missing");
        Assert.Equal(GoalReadinessSeverity.Blocker, finding.Severity);
        Assert.False(finding.CanOverride);
        Assert.IsType<DispatchReadinessReady>(conductor);
    }

    [Fact]
    public void Preflight_ResolvedWorkspace_HasNoWorkspaceMissingFinding()
    {
        var (kernel, _, goal, agents) = ReadinessTestSeed.Create();
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var report = GoalReadinessPreflight.Build(goal, agents, directory.FullName, WorkerProfileCatalog.Default(),
                resolveWorktree: (_, _) => directory.FullName, providerHoldScope: kernel.ProviderHoldScope,
                now: ReadinessTestSeed.Now);

            Assert.True(report.RequiresWorkspace);
            Assert.True(report.HasWorkspace);
            Assert.DoesNotContain(report.Findings, item => item.Kind == "workspace-missing");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CrossGoalProviderHold_PreflightAndConductorReportSameHoldForGoalB()
    {
        var (kernel, source, target, agents) = ReadinessTestSeed.Create(crossGoalHold: true);
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var profiles = WorkerProfileCatalog.Default();
        var now = ReadinessTestSeed.Now;
        static string? ResolveWorktree(string _, GoalId __) => null;

        var projection = GoalExecutionProjection.Build(target, agents, root, profiles, kernel.ProviderHoldScope, now,
            ResolveWorktree, kernel);
        var report = GoalReadinessPreflight.Build(target, agents, root, profiles, ResolveWorktree,
            kernel.ProviderHoldScope, now);
        var conductor = ConductorDriver.EvaluateConductorReadiness(kernel, target, agents, profiles, now,
            executionDirectory: root, resolveWorktree: ResolveWorktree);

        var blocked = Assert.IsType<DispatchReadinessBlocked>(conductor);
        Assert.True(blocked.HasCandidates);
        Assert.NotNull(blocked.ProviderBudgetHold);
        Assert.Equal(source.Id, blocked.ProviderBudgetHold.SourceGoalId);
        Assert.Equal(projection.Verdict, conductor);
        Assert.Equal(target.Id, report.GoalId);
        var finding = Assert.Single(report.Findings, item => item.Kind == "provider-budget-exhausted");
        Assert.Equal(GoalReadinessSeverity.Blocker, finding.Severity);
        Assert.False(finding.CanOverride);
        Assert.Equal(blocked.Reason, finding.Message);
    }
}
