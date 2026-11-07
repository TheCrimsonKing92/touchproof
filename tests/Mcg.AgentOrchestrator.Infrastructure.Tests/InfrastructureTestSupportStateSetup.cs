using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;
using Microsoft.Data.Sqlite;

internal static partial class InfrastructureTestSupport
{
    // CLI and conductor fixtures own setup when they create a usable workspace.
    // Tests for missing setup deliberately use OrchestratorWorkspace.ForDirectory instead.
    public static OrchestratorWorkspace CreateSetUpWorkspace(string root)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        SetUpStateDatabase(workspace.SqliteStatePath);
        return workspace;
    }

    public static string SetUpStateDatabase(string statePath)
    {
        if (!StateDbMigrations.IsUpToDate(statePath))
            StateDbMigrations.EnsureUpToDate(statePath);
        return statePath;
    }

    public static void CreateHistoricalReconcileSweepSchema(string statePath, IReadOnlyList<string> statements)
    {
        SetUpStateDatabase(statePath);
        using var connection = new SqliteConnection($"Data Source={statePath};Pooling=False;Mode=ReadWrite");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM schema_migrations WHERE migration_number = 15;
            DROP TABLE reconcile_sweep_remediation;
            DROP TABLE reconcile_acceptance_leases;
            """;
        command.ExecuteNonQuery();
        foreach (var statement in statements)
        {
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}
