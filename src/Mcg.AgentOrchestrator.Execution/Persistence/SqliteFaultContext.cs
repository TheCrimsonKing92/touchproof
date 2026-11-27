using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record SqliteFaultContext(string Database, string Operation)
{
    private const string DataKey = "Mcg.AgentOrchestrator.SqliteFaultContext";

    // A false filter records the diagnostic before disposal and lets the original fault propagate.
    internal static bool Attach(SqliteException exception, SqliteConnection connection, string operation)
    {
        try
        {
            if (!exception.Data.Contains(DataKey))
            {
                var database = connection.DataSource;
                if (string.IsNullOrWhiteSpace(database))
                    database = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource;
                if (!string.IsNullOrWhiteSpace(database))
                    exception.Data[DataKey] = new SqliteFaultContext(database, operation);
            }
        }
        catch
        {
            // A diagnostic failure must leave the SQLite exception intact.
        }
        return false;
    }

    internal static SqliteFaultContext? Find(Exception exception)
    {
        if (exception is SqliteException && exception.Data[DataKey] is SqliteFaultContext context)
            return context;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Select(Find).FirstOrDefault(found => found is not null);
        return exception.InnerException is { } inner ? Find(inner) : null;
    }
}
