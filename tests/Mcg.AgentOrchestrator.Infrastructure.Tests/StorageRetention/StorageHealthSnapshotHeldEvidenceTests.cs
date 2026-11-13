using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel safe: private retention roots and an explicit empty MTP root isolate each sweep.
public sealed class StorageHealthSnapshotHeldEvidenceTests
{
    private const string Legacy = """
        {"totalBytes":7,"totalFiles":1,"overBudgetCount":0,"unregisteredCount":0,
         "largestOverBudgetClass":null,"measuredAtUtc":"2026-09-20T12:00:00Z"}
        """;

    [Fact]
    public void Publish_HeldRows_RoundTripWithStableWireNames()
    {
        using var fixture = new RetentionReclaimFixture();
        AcceptanceHeldEvidenceRow[] rows =
        [
            new("DeferredLive", "attempt-is-unreconciled", 2, 7, 1),
            new("Preserved", "final-attempt", 1, 11, 0)
        ];
        var decisions = new List<EvidenceRetentionDecision>();

        new StorageHealthSnapshotWriter().Publish(fixture.OrchestratorDirectory,
            RetentionReclaimFixture.Now, decisions, rows.Reverse().ToArray());

        Assert.Empty(decisions);
        var bytes = File.ReadAllBytes(SnapshotPath(fixture));
        var snapshot = StorageHealthSnapshot.Parse(bytes);
        Assert.NotNull(snapshot);
        Assert.Equal(rows, snapshot.HeldAcceptanceEvidence);
        using var document = JsonDocument.Parse(bytes);
        var held = document.RootElement.GetProperty("heldAcceptanceEvidence").EnumerateArray().ToArray();
        Assert.Equal(2, held.Length);
        Assert.Equal("DeferredLive", held[0].GetProperty("action").GetString());
        Assert.Equal("attempt-is-unreconciled", held[0].GetProperty("reason").GetString());
        Assert.Equal(2, held[0].GetProperty("decisionCount").GetInt32());
        Assert.Equal(7, held[0].GetProperty("bytes").GetInt64());
        Assert.Equal(1, held[0].GetProperty("unmeasured").GetInt32());
    }

    [Fact]
    public void LegacyDocument_ParsesWithEmptyRowsAndSerializesAnArray()
    {
        var snapshot = StorageHealthSnapshot.Parse(Encoding.UTF8.GetBytes(Legacy));
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.HeldAcceptanceEvidence);
        Assert.Empty(snapshot.HeldAcceptanceEvidence);
        var oldConstructor = new StorageHealthSnapshot(7, 1, 0, 0, null, RetentionReclaimFixture.Now);
        using var document = JsonDocument.Parse(oldConstructor.Serialize());
        Assert.Empty(document.RootElement.GetProperty("heldAcceptanceEvidence").EnumerateArray());
        Assert.NotNull(StorageHealthSnapshot.Parse(Encoding.UTF8.GetBytes(oldConstructor.Serialize())));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"action\":0,\"reason\":\"r\",\"decisionCount\":1,\"bytes\":0,\"unmeasured\":0}]")]
    [InlineData("[{\"action\":\"Deleted\",\"reason\":\"r\",\"decisionCount\":1,\"bytes\":0,\"unmeasured\":0}]")]
    [InlineData("[{\"action\":\"Bogus\",\"reason\":\"r\",\"decisionCount\":1,\"bytes\":0,\"unmeasured\":0}]")]
    [InlineData("[{\"action\":\"0\",\"reason\":\"r\",\"decisionCount\":1,\"bytes\":0,\"unmeasured\":0}]")]
    [InlineData("[{\"action\":\"Preserved\",\"reason\":\"r\",\"decisionCount\":1,\"bytes\":-1,\"unmeasured\":0}]")]
    [InlineData("[{\"action\":\"Preserved\",\"reason\":\"r\",\"decisionCount\":2147483648,\"bytes\":0,\"unmeasured\":0}]")]
    [InlineData("[{\"action\":\"Preserved\",\"reason\":\"r\",\"decisionCount\":1,\"bytes\":0}]")]
    public void PresentMalformedRows_RejectSnapshot(string held)
    {
        var json = Legacy.TrimEnd()[..^1] + ",\"heldAcceptanceEvidence\":" + held + "}";
        Assert.Null(StorageHealthSnapshot.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Sweep_NonterminalGoal_PublishesItsUnchangedHeldDecision()
    {
        using var fixture = new RetentionReclaimFixture();
        var goalDirectory = Path.Combine(fixture.AttemptRoot(), RetentionReclaimFixture.ActiveId);
        fixture.WriteAgedFile(Path.Combine(goalDirectory, "evidence.txt"), 30);
        var expectedBytes = new FileInfo(Path.Combine(goalDirectory, "evidence.txt")).Length;

        var result = fixture.Run(null, fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active));

        Assert.DoesNotContain(result.Decisions, row => row.Action == EvidenceRetentionAction.Failed ||
            row.Reason == "storage-health-snapshot-failed");
        var decision = Assert.Single(result.Decisions, row => row.Family == EvidenceArtifactFamily.AcceptanceGateAttempts);
        Assert.Equal(EvidenceRetentionAction.Preserved, decision.Action);
        Assert.Equal("non-terminal-evidence-unbounded-by-policy", decision.Reason);
        Assert.Equal(goalDirectory, decision.Path);
        var snapshot = StorageHealthSnapshot.Parse(File.ReadAllBytes(SnapshotPath(fixture)));
        Assert.NotNull(snapshot);
        Assert.Equal(new AcceptanceHeldEvidenceRow("Preserved", decision.Reason, 1, expectedBytes, 0),
            Assert.Single(snapshot.HeldAcceptanceEvidence!));
    }

    private static string SnapshotPath(RetentionReclaimFixture fixture) =>
        Path.Combine(fixture.OrchestratorDirectory, StorageHealthSnapshotWriter.FileName);
}
