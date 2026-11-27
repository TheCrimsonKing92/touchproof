using Mcg.AgentOrchestrator.App.Cli;
using Microsoft.Data.Sqlite;

internal static class ShadowStartWalProbe
{
    internal static int Run(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        var state = Path.Combine(root, ".orchestrator");
        var rootIndex = Array.IndexOf(args, "--repository-root");
        var stateIndex = Array.IndexOf(args, "--expected-state-directory");
        if (!args.Contains("--no-dispatch") || !args.Contains("--disposable-fixture") ||
            rootIndex < 0 || args[rootIndex + 1] != root ||
            stateIndex < 0 || args[stateIndex + 1] != state)
            return 3;

        ShadowStartSidecarTracking.InstallForCandidate(state);
        var path = Path.Combine(state, "state.db");
        OpenAndWriteWal(path).Dispose();
        if (File.Exists(path + "-wal") || File.Exists(path + "-shm"))
            throw new IOException("The first SQLite connection did not remove its sidecars.");

        using var reopened = OpenAndWriteWal(path);
        Console.WriteLine("wal-ready");
        if (args[0] != "no-marker") File.WriteAllText(args[0], "started");
        // The test releases the run by cancelling or closing the job.
        using var hold = new ManualResetEventSlim();
        hold.Wait();
        return 4;
    }

    private static SqliteConnection OpenAndWriteWal(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var write = connection.CreateCommand();
            write.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO grouped_attempts VALUES ('wal-fixture', NULL);";
            write.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
