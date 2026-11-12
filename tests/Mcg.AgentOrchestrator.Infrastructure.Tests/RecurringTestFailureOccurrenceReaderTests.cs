using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static RecurringUnrelatedTestRuleTests;

// Parallel-safe: pure rows and a uniquely owned temporary index.
public sealed class RecurringTestFailureOccurrenceReaderTests
{
    [Fact(DisplayName = "Gate census and lane flakes retain attempt and lane evidence")]
    public void ReadsBothGateSources()
    {
        var rows = RecurringTestFailureOccurrenceReader.Read([Census("a")], [Flake("b")], Now);
        Assert.Equal(2, rows.Count);
        Assert.Equal(Key, rows[0].TestKey);
        Assert.Equal("attempt-b", rows[1].AttemptId);
        Assert.Equal("verifier lane", rows[1].Lane);
        Assert.Equal("tree", rows[1].CandidateSha);
    }

    [Fact(DisplayName = "Touched tests, expired rows and non-gate records cannot corroborate")]
    public void RejectsIneligibleRows()
    {
        var rows = RecurringTestFailureOccurrenceReader.Read(
            [Census("a") with { InsideChangedPaths = true }, Census("a"),
             Census("old") with { RecordedAt = Now.AddDays(-8) },
             Census("tester") with { Kind = "tester-evidence" }, Census("future") with { RecordedAt = Now.AddMinutes(1) }],
            [Flake("a"), Flake("old") with { RecordedAt = Now.AddDays(-8) }], Now);
        Assert.Empty(rows);
    }

    [Fact(DisplayName = "Malformed index rows do not hide later valid gate evidence")]
    public void ReadsHandwrittenIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "recurrence-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "index.jsonl");
            File.WriteAllLines(path, ["{invalid", JsonSerializer.Serialize(Census("a"), new JsonSerializerOptions(JsonSerializerDefaults.Web))]);
            Assert.Equal("a", Assert.Single(new RecurringTestFailureOccurrenceReader(path, null).Read(Now)).GoalId);
            Assert.Empty(new RecurringTestFailureOccurrenceReader(Path.Combine(root, "missing"), null).Read(Now));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AcceptanceFailingTestIndexRecord Census(string goal) => new(1, "gate-failure", goal,
        Now.AddHours(-1), CandidateSha: "sha", CheckName: "verifier lane", TestIdentity: Identity);
    private static AcceptanceLaneFlakeRow Flake(string goal) => new(goal, "attempt-" + goal, "verifier lane", "tree",
        "first", [], [Identity], "red", "rerun", [], [], "green", AcceptanceLaneRerunEvidence.Flake, Now.AddHours(-1));
}
