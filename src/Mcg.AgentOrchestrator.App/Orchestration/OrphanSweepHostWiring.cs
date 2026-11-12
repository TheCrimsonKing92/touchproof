using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OrphanSweepHostWiring
{
    internal static GoalWorktreeCleanupHooks Create(OrchestratorWorkspace workspace,
        GoalWorktreeCleanupOptions options, bool isConductLoop,
        TextWriter? diagnosticWriter = null, Func<IReadOnlyList<GoalSummary>>? readMetadata = null,
        OrphanWorktreeSweepOutcomeLedger? ledger = null)
    {
        var outcomes = ledger ?? OrphanWorktreeSweepOutcomeLedger.Process;
        var diagnostics = diagnosticWriter ?? Console.Error;
        return GoalWorktreeCleanupHooks.ForConfiguration(options, workspace.OrchestratorDirectory) with
        {
            ReadStoredGoalMetadata = readMetadata ?? (() =>
            {
                var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
                return repository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            }),
            OrphanSweepOutcomeSink = outcome =>
            {
                if (!isConductLoop && outcome.CallerRole?.StartsWith("cli-startup:", StringComparison.Ordinal) == true)
                {
                    if (outcomes.Observe(outcome)) CliStartupSweepOutcomeLog.Append(workspace.LogDirectory, outcome);
                }
                else OrphanWorktreeSweepOutcome.Write(outcome, diagnostics, outcomes);
            }
        };
    }
}
