using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalScopeCollisionAdvisory
{
    internal static void Print(
        CliExecutionContext context,
        string rawObjective,
        string? intakeItemId = null,
        string? heading = null,
        IReadOnlyDictionary<string, GoalScopeLifecycleObservation>? lifecycleObservations = null,
        GoalRepositoryDeclaration? repositories = null)
    {
        lifecycleObservations ??= GoalMonitoringSubscriptionCommand.ReadScopeCollisionLifecycleObservations(
            context.Workspace,
            GoalScopeCollisionAdvisor.SelectComparisonCandidates(context.Kernel.Goals, intakeItemId));
        var report = GoalScopeCollisionAdvisor.Build(
            [rawObjective],
            context.Kernel.Goals,
            intakeItemId,
            lifecycleObservations, context.Workspace.RootDirectory, repositories);
        foreach (var warning in report.IntakeWarnings)
            Console.Error.WriteLine(warning);
        GoalScopeCollisionReportTextView.Print(
            report,
            intakeItemId,
            heading);
    }
}
