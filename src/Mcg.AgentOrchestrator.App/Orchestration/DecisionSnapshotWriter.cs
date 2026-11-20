using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DecisionSnapshotWriter
{
    internal static async Task WriteAsync(string stateDirectory, string outputPath, DateTimeOffset asOf,
        Func<GoalSnapshot, Goal>? restoreGoal = null)
    {
        stateDirectory = DecisionSnapshotPaths.ResolveDirectory(stateDirectory);
        outputPath = DecisionSnapshotPaths.ResolveOutput(stateDirectory, outputPath);
        asOf = asOf.ToUniversalTime();

        var dbPath = Path.Combine(stateDirectory, "state.db");
        var repository = SqliteOrchestratorStateRepository.OpenReadOnlyOfflineCopy(dbPath);
        var stored = await repository.ListStatusGoalIdsAsync();
        if (stored.Any(row => string.IsNullOrWhiteSpace(row.Id)) ||
            stored.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != stored.Count)
        {
            throw new InvalidDataException("The state copy has missing or duplicate goal identities.");
        }

        var ids = stored.Select(row => row.Id).Order(StringComparer.Ordinal).ToArray();
        var policy = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(stateDirectory));
        var verifiedTransition = policy.GetTransitionDecision(GoalLifecycleState.Verified);
        var context = new DecisionSnapshotContext(asOf, verifiedTransition.ToString(), DecisionSnapshotEvaluator.SlotIndex,
            typeof(DecisionSnapshotWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            ReadCopyHash(stateDirectory));

        var evaluator = new DecisionSnapshotEvaluator(stateDirectory, repository, verifiedTransition, asOf);
        var outcomes = new Dictionary<string, DecisionSnapshotGoal>(StringComparer.Ordinal);
        var goals = new Dictionary<string, Goal>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            try
            {
                if (id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    id.Contains('\\') || id.Contains('/') || id.Contains(':'))
                    throw new InvalidDataException("Stored goal identity is not a single filename component.");

                var snapshot = await repository.LoadGoalAsync(new GoalId(id)) ??
                    throw new InvalidDataException("The enumerated goal could not be loaded.");
                var goal = restoreGoal is null ? Goal.FromSnapshot(snapshot) : restoreGoal(snapshot);
                if (!string.Equals(goal.Id.Value, id, StringComparison.Ordinal))
                    throw new InvalidDataException("The hydrated goal has a different identity from the stored row.");
                goals.Add(id, goal);
            }
            catch (Exception ex)
            {
                outcomes.Add(id, CaptureException(id, ex));
            }
        }

        foreach (var (id, goal) in goals)
        {
            try
            {
                outcomes.Add(id, new(id, Decision: evaluator.Evaluate(goal, goals.Values)));
            }
            catch (Exception ex)
            {
                outcomes.Add(id, CaptureException(id, ex));
            }
        }

        var artifact = new DecisionSnapshot(DecisionSnapshot.CurrentSchemaVersion, context,
            ids.Select(id => outcomes[id]).ToArray());
        await PublishAsync(outputPath, artifact);
    }

    private static DecisionSnapshotGoal CaptureException(string id, Exception ex) =>
        new(id, Exception: new(ex.GetType().FullName ?? ex.GetType().Name, ex.Message));

    private static string ReadCopyHash(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "state.db", "state.db-wal", "collaboration-items.db", "collaboration-items.db-wal" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            using var file = File.OpenRead(path);
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
            hash.AppendData(SHA256.HashData(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task PublishAsync(string outputPath, DecisionSnapshot artifact)
    {
        var staging = Path.Combine(Path.GetDirectoryName(outputPath)!, $".decision-snapshot-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                await JsonSerializer.SerializeAsync(stream, artifact, DecisionSnapshot.JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, outputPath, overwrite: true);
            created = false;
        }
        finally
        {
            if (created) File.Delete(staging);
        }
    }
}
