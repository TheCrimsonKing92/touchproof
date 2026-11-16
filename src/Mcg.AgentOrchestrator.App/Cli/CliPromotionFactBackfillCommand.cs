using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliPromotionFactBackfillCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("promotion-facts-backfill", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        Run(args, workspace.SqliteStatePath, workspace.GoalLifecycleEventsDirectory, workspace.ExecutionDirectory,
            workspace.IntegrationBranch, Console.Out, Console.Error);

    internal static int Run(IReadOnlyList<string> args, string statePath, string eventsDirectory,
        string executionDirectory, string integrationBranch, TextWriter output, TextWriter error,
        Func<string, string[], GitCli.GitResult>? runGit = null)
    {
        try
        {
            if (args.Skip(1).Any(arg => arg is not ("--apply" or "--json")))
                throw new ArgumentException(CliCommandHelp.PromotionFactsBackfillUsage);
            var apply = args.Contains("--apply");
            var json = args.Contains("--json");
            if (!File.Exists(statePath)) throw new FileNotFoundException("State database is missing.", statePath);
            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(statePath);
            var goals = repository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            var stored = new HashSet<string>(StringComparer.Ordinal);
            bool hasTable;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = statePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'goal_promotion_facts'";
                hasTable = command.ExecuteScalar() is not null;
                if (hasTable)
                {
                    command.CommandText = "SELECT goal_id FROM goal_promotion_facts";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) stored.Add(reader.GetString(0));
                }
            }
            var plan = PromotionFactBackfillPlanner.Build(goals, stored, eventsDirectory, executionDirectory, integrationBranch, runGit);
            var written = 0;
            var failures = new List<string>();
            if (apply)
            {
                if (!hasTable) throw new InvalidOperationException("Missing goal_promotion_facts table; apply requires a migrated state database.");
                var writer = new SqliteOrchestratorStateRepository(statePath);
                foreach (var group in plan.PlannedGoals.GroupBy(f =>
                             (f.PriorMainRevision, f.PromotedMainRevision, f.PromotedAt, f.Provenance)))
                {
                    try { writer.RecordPromotionFacts(group.ToArray()); written += group.Count(); }
                    catch (Exception ex) when (ex is InvalidOperationException or SqliteException or IOException)
                    {
                        failures.Add(group.Key.PromotedMainRevision);
                        error.WriteLine($"Error: promotion {group.Key.PromotedMainRevision}: {ex.Message}");
                    }
                }
            }
            if (!json)
            {
                foreach (var fact in plan.PlannedGoals) output.WriteLine($"planned {fact.GoalId} {fact.PromotedMainRevision} {fact.PromotedAt:O} {fact.Provenance}");
                foreach (var id in plan.AlreadyRecordedGoals) output.WriteLine($"already-recorded {id}");
                foreach (var item in plan.UnknownGoals) output.WriteLine($"unknown {item.GoalId} {item.Reason}");
                foreach (var item in plan.ConflictGoals) output.WriteLine($"conflict {item.GoalId} {string.Join(",", item.MainRevisions)}");
            }
            output.WriteLine(JsonSerializer.Serialize(new
            {
                planned = plan.PlannedGoals.Count, written, alreadyRecorded = plan.AlreadyRecordedGoals.Count,
                unknown = plan.UnknownGoals.Count, conflicts = plan.ConflictGoals.Count,
                malformedLines = plan.MalformedLines, applied = apply,
                plannedGoals = plan.PlannedGoals.Select(f => new { goalId = f.GoalId, f.CandidateRevision,
                    f.PriorMainRevision, f.PromotedMainRevision, f.PromotedAt, f.Provenance }),
                alreadyRecordedGoals = plan.AlreadyRecordedGoals,
                unknownGoals = plan.UnknownGoals.Select(g => new { goalId = g.GoalId, reason = g.Reason }),
                conflictGoals = plan.ConflictGoals.Select(g => new { goalId = g.GoalId, mainRevisions = g.MainRevisions }),
                failedPromotions = failures
            }));
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
