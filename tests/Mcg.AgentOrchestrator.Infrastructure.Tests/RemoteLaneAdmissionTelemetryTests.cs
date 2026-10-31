using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneAdmissionTelemetryTests
{
    [Fact]
    public async Task SlotOnlyRunPersistsNullBudgetAndRemainsReadable()
    {
        using var fixture = new RemoteLaneCostTestFixture(costBudget: null);
        Assert.NotNull(await fixture.Claim("A"));
        fixture.ReturnSuccessfulResult();
        Assert.NotNull((await fixture.Run("A")).Accepted);

        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger));
        var admission = Assert.IsType<RemoteLaneAdmissionRecord>(row.Admission);
        Assert.Null(admission.CostBudget);
        Assert.Equal(2.0, admission.ReservedCost);
        Assert.Equal(1, admission.Concurrency);
        Assert.Equal(1, row.Version);
        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(fixture.Ledger)));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("admission").GetProperty("cost_budget").ValueKind);
    }

    [Fact]
    public async Task SuccessfulRunPersistsFrozenAdmissionAndReleasesItsOwnCost()
    {
        using var fixture = new RemoteLaneCostTestFixture([
            RemoteLaneCostTestFixture.Sample("A", 100), RemoteLaneCostTestFixture.Sample("B", 190),
            RemoteLaneCostTestFixture.Sample("C", 190)]);
        Assert.NotNull(await fixture.Claim("A")); Assert.NotNull(await fixture.Claim("B"));
        fixture.ReturnSuccessfulResult();
        Assert.NotNull((await fixture.Run("B")).Accepted);
        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger));
        Assert.Equal("attempt", row.GateAttemptId);
        Assert.Equal("executor", row.ExecutorId);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        var admission = Assert.IsType<RemoteLaneAdmissionRecord>(row.Admission);
        Assert.Equal("executor", admission.ExecutorId);
        Assert.Equal(2, admission.Concurrency);
        Assert.Equal(new[] { RemoteLaneCostTestFixture.Lane("A").Name }, admission.ConcurrentLanes);
        Assert.Equal(1.9, admission.ReservedCost); Assert.Equal(1, admission.CostSamples);
        Assert.Equal("heavy", admission.CostClass);
        Assert.Equal(4, admission.CostBudget); Assert.Equal(2.9, admission.TotalReservedCost);
        Assert.Equal(1, row.Version);
        Assert.NotNull(await fixture.Claim("C"));
        Assert.Null(await fixture.Claim("Unsampled"));
        Assert.Equal(2, admission.Concurrency); // Snapshot does not track later reservations.
    }

    [Fact]
    public void OldRowsStayReadableAndMatchingHistorySkipsMissingAdmission()
    {
        using var fixture = new RemoteLaneCostTestFixture();
        var sample = RemoteLaneCostTestFixture.Sample("A");
        RemoteExecutorHealthLedger.Append(fixture.Ledger, sample);
        using (var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(fixture.Ledger))))
            Assert.False(json.RootElement.TryGetProperty("admission", out _));
        var old = Assert.Single(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger));
        Assert.Null(old.Admission); Assert.Equal(1, old.Version);
        var overall = RemoteLaneOfferHistoryReader.Read(fixture.Ledger);
        Assert.Equal(600, overall[(sample.Lane, sample.Expected.Filter)].MedianSeconds);
        var history = RemoteLaneAdmissionHistory.Read(fixture.Ledger);
        Assert.Equal((2.0, 0), RemoteLaneCostModel.Build(history.Records).Estimate("unknown", "executor"));
        Assert.Equal(((double?)null, 0), history.MatchingDuration(sample.Lane, sample.Expected.Filter, "executor", 1));
        File.AppendAllText(fixture.Ledger, "{malformed\n");
        Assert.Single(RemoteLaneAdmissionHistory.Read(fixture.Ledger).Records);
    }
}
