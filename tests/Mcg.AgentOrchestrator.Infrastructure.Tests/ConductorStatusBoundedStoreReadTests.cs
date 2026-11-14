using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace/databases, call-local observer and injected time/probes.
public sealed class ConductorStatusBoundedStoreReadTests
{
    // Cold sparse dependencies account for five ~35K snapshots plus ~20K visible data/metadata.
    // 1M leaves >4x headroom, yet is below 25% of the 6.5M irrelevant Completed payload.
    private const long MaximumMaterializedCharacters = 1_000_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_RequestsOnlyVisibleGoalsAndBoundsAllMaterialization(bool storedEvidence)
    {
        using var fixture = new ConductorStatusSeededFixture();
        await fixture.Seed();
        using (var connection = ConductorStatusSeededFixture.Open(fixture.Workspace.SqliteStatePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*), min(length(snapshot_json)) FROM goals WHERE status = 'Completed'";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(200, reader.GetInt64(0));
            Assert.True(reader.GetInt64(1) >= ConductorStatusSeededFixture.PayloadCharacters);
        }
        if (storedEvidence)
            Assert.Equal(5, (await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath)
                .ReadProviderHoldSeedEvidenceAsync(ConductorStatusSeededFixture.EvidenceIds))!.Count);

        var observations = new List<GoalSnapshotReadObservation>();
        var output = fixture.Status(observer: observations.Add);

        Assert.Contains("Non-terminal goals: 5", output);
        Assert.Contains("Provider usage holds: 1", output);
        Assert.Equal(ConductorStatusSeededFixture.NonTerminal,
            observations.Where(row => row.Operation == "requested").Select(row => row.GoalId).Order(StringComparer.Ordinal));
        Assert.InRange(Assert.Single(observations.Where(row => row.Operation == "metadata")).MaterializedCharacters, 1, 9_000);
        Assert.InRange(Assert.Single(observations.Where(row => row.Operation == "model-fit-history")).MaterializedCharacters, 1, 10_000);
        Assert.Single(observations.Where(row => row.Operation == "human-input"));
        Assert.Single(observations.Where(row => row.Operation == "latest-landing"));
        var evidenceOperation = storedEvidence ? "provider-evidence-stored" : "provider-evidence-decoded";
        Assert.Equal(ConductorStatusSeededFixture.EvidenceIds.Select(id => id.Value).Order(StringComparer.Ordinal),
            observations.Where(row => row.Operation == evidenceOperation).Select(row => row.GoalId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(observations, row => row.Operation == (storedEvidence ? "provider-evidence-decoded" : "provider-evidence-stored"));
        Assert.All(observations, row => Assert.True(row.MaterializedCharacters >= 0));
        Assert.InRange(observations.Sum(row => row.MaterializedCharacters), 1, MaximumMaterializedCharacters);
    }
}
