using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: unique artifact roots; holder identity and liveness are injected.
public sealed class GoalBranchCommitAcceptancePreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapacityQuery_ReservedOrReleasedHolder_ReadsWithoutChangingAnyArtifact(bool released)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(GoalBranchCommitAcceptancePreflightTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var generation = new SpawnProcessIdentity(1234, DateTimeOffset.UnixEpoch, Path.Combine(root, "holder.exe"));
            var parallel = new ConductorParallelAcceptanceAttempt("attempt", "goal", "goal", 0, "candidate", "main",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1234, ConductorParallelAcceptanceAttemptOutcome.Running,
                Path.Combine(root, "out"), Path.Combine(root, "err"), Path.Combine(root, "exit"),
                Path.Combine(root, "heartbeat"), Path.Combine(root, "result"), Path.Combine(root, "parallel.attempt.json"))
                { HolderContractVersion = 1 };
            var grouped = new ConductorGroupedGateAttempt("group", "cohort", [], "main", "tree", "manifest", "identity",
                DateTimeOffset.UnixEpoch, 1234, 1234, Path.Combine(root, "group.attempt.json"), Path.Combine(root, "group.result"),
                Path.Combine(root, "group.exit"), Path.Combine(root, "group.out"), Path.Combine(root, "group.err"), root, "{}")
                { HolderContractVersion = 1 };
            File.WriteAllText(parallel.MetadataPath, JsonSerializer.Serialize(parallel));
            File.WriteAllText(grouped.MetadataPath, JsonSerializer.Serialize(grouped));
            WriteLease(AcceptanceHolderBinding.For(parallel));
            WriteLease(AcceptanceHolderBinding.For(grouped));
            var before = Snapshot();
            var soloCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, "integration",
                isProcessAlive: _ => throw new InvalidOperationException("Read-only lease query must not probe processes."),
                generationIdentity: generation);
            var groupedCoordinator = new ConductorGroupedGateAttemptCoordinator(root,
                isProcessAlive: _ => throw new InvalidOperationException("Read-only lease query must not probe processes."),
                generationIdentity: generation);

            Assert.Equal(!released, soloCoordinator.ReservesCapacityReadOnly(parallel));
            Assert.Equal(!released, groupedCoordinator.ReservesCapacityReadOnly(grouped));
            Assert.Equal(before, Snapshot());

            void WriteLease(AcceptanceHolderBinding binding) => File.WriteAllText(binding.LeasePath,
                JsonSerializer.Serialize(new AcceptanceHolderLeaseRecord(binding, binding.JobName, generation,
                    released ? generation : null, released ? "Released" : "Reserved", released
                        ? [new AcceptanceHolderTransition("release", generation, DateTimeOffset.UnixEpoch, 0, "settled")]
                        : [])));

            string[] Snapshot() => Directory.GetFiles(root).Order(StringComparer.Ordinal)
                .Select(path => path + ":" + File.ReadAllText(path)).ToArray();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
