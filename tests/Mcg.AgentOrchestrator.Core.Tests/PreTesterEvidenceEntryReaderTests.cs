using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: local values and in-memory serialization only.
public sealed class PreTesterEvidenceEntryReaderTests
{
    [Theory]
    [InlineData("not an evidence marker")]
    [InlineData("finding-evidence pre-tester outcome=red; candidate_sha=other; selections=WrongTests")]
    [InlineData("finding-evidence pre-tester outcome=red; outcome=duplicate")]
    public void Read_TypedEntryIgnoresAlteredOrMalformedMessage(string message)
    {
        var entry = Entry();
        var evt = new ProgressEvent(GoalId.New(), TaskId.New(), ProgressKind.FindingEvidenceRunRecorded,
            message, DateTimeOffset.UnixEpoch, PreTesterEvidence: entry);

        Assert.Same(entry, PreTesterEvidenceEntryReader.Read(evt));
        Assert.Equal(entry.FailingTests, PreTesterFailureSignature.ReadRun(evt).FailingTests);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("abandoned")]
    [InlineData("red")]
    public void Read_LegacyEventUsesExistingParser(string outcome)
    {
        var message = PreTesterEvidenceIndexLines.FormatMarker(Entry() with { Outcome = outcome });
        var evt = new ProgressEvent(GoalId.New(), TaskId.New(), ProgressKind.FindingEvidenceRunRecorded,
            message, DateTimeOffset.UnixEpoch);

        var actual = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceEntryReader.Read(evt));
        Assert.Equal(PreTesterEvidenceIndexLines.Parse(message), actual);
        Assert.Null(actual.AbandonmentReason);
    }

    [Fact]
    public void Entry_JsonReloadRetainsValueEqualityAndHashCode()
    {
        var entry = Entry();
        var restored = JsonSerializer.Deserialize<PreTesterEvidenceEntry>(JsonSerializer.Serialize(entry));

        Assert.NotSame(entry, restored);
        Assert.Equal(entry, restored);
        Assert.Equal(entry.GetHashCode(), restored!.GetHashCode());
        Assert.NotEqual(entry, restored with { AbandonmentReason = "other" });
        Assert.NotEqual(entry, restored with { Selections = entry.Selections.Reverse().ToArray() });
    }

    private static PreTesterEvidenceEntry Entry() => new("abandoned", "candidate", "receipt",
        ["Core.Tests:FirstTests", "Core.Tests:SecondTests"], ["MissingTests"], "result.json",
        ["FirstTests.Fails"], "truncated-stored-message");
}
