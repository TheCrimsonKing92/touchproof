using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

internal sealed class PromotionFactBackfillFixture : IDisposable
{
    internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
    internal OrchestratorWorkspace Workspace { get; }
    internal SqliteOrchestratorStateRepository Repository { get; }
    internal AgentOrchestratorKernel Kernel { get; } = new();
    internal string Prior { get; }
    internal string Candidate { get; }
    internal string Promoted { get; }
    internal string Unreachable { get; }
    internal static DateTimeOffset At => new(2026, 10, 15, 14, 30, 0, TimeSpan.FromHours(-5));

    internal PromotionFactBackfillFixture()
    {
        Workspace = OrchestratorWorkspace.ForDirectory(Root) with { IntegrationBranch = "trunk" };
        Git("init", "-b", Workspace.IntegrationBranch);
        Git("config", "user.email", "tests@example.invalid");
        Git("config", "user.name", "Tests");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.hooksPath", Path.Combine(Root, "no-hooks"));
        File.WriteAllText(Path.Combine(Root, "initial.txt"), "initial");
        Git("add", "initial.txt");
        Git("commit", "-m", "initial");
        Git("checkout", "-b", "candidate");
        File.WriteAllText(Path.Combine(Root, "candidate.txt"), "candidate");
        Git("add", "candidate.txt");
        Git("commit", "-m", "candidate");
        Candidate = Git("rev-parse", "HEAD");
        Git("checkout", Workspace.IntegrationBranch);
        File.WriteAllText(Path.Combine(Root, "trunk.txt"), "trunk");
        Git("add", "trunk.txt");
        Git("commit", "-m", "advance trunk");
        Prior = Git("rev-parse", "HEAD");
        Git("merge", "--no-ff", "candidate", "-m", "promote candidate");
        Promoted = Git("rev-parse", "HEAD");
        Git("checkout", "-b", "unmerged");
        Git("commit", "--allow-empty", "-m", "off trunk");
        Unreachable = Git("rev-parse", "HEAD");
        Git("checkout", Workspace.IntegrationBranch);
        Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
    }

    internal Goal AddGoal() => Kernel.CreateGoal("Historical landing");

    internal async Task Save()
    {
        var snapshot = Kernel.ExportSnapshot();
        await Repository.SaveGoalSnapshotsAsync(snapshot.Goals.Select(g => g with { Status = GoalStatus.Completed }).ToArray());
    }

    internal GoalLifecycleEventWriter Writer(DateTimeOffset? at = null) =>
        new(Workspace.GoalLifecycleEventsDirectory, new FixedClock(at ?? At), integrationBranch: Workspace.IntegrationBranch);

    internal void Land(Goal goal, string? main = null, DateTimeOffset? at = null) =>
        Writer(at).AppendGoalLandedFromMergeEvidence(goal.Id, "goal/" + goal.Id.Value, Candidate, main ?? Promoted);

    internal PromotionFactBackfillPlanner.Plan Plan() => PromotionFactBackfillPlanner.Build(
        SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath).ListGoalMetadataAsync().GetAwaiter().GetResult(),
        new HashSet<string>(), Workspace.GoalLifecycleEventsDirectory, Root, Workspace.IntegrationBranch);

    internal (int Exit, JsonDocument Json, string Error) Run(bool apply = false)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = CliPromotionFactBackfillCommand.Run(apply ? ["promotion-facts-backfill", "--apply", "--json"]
            : ["promotion-facts-backfill", "--json"], Workspace.SqliteStatePath, Workspace.GoalLifecycleEventsDirectory,
            Root, Workspace.IntegrationBranch, output, error);
        return (exit, JsonDocument.Parse(output.ToString()), error.ToString());
    }

    internal string[] Rows() => Query("SELECT rowid, goal_id, candidate_revision, prior_main_revision, promoted_main_revision, promoted_at, provenance FROM goal_promotion_facts ORDER BY goal_id");

    internal string[] Query(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Workspace.SqliteStatePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString()).ToArray()));
        return rows.ToArray();
    }

    internal void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Workspace.SqliteStatePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal string Git(params string[] args)
    {
        var result = GitCli.Run(Root, args);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Error}");
        return result.Output.Trim();
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);

    private sealed class FixedClock(DateTimeOffset at) : IClock
    {
        public DateTimeOffset UtcNow => at;
    }
}
