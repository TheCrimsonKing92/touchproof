using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel safe: all filesystem observations are inside a private retention fixture.
public sealed class AcceptanceHeldEvidenceSummaryTests
{
    [Fact]
    public void MixedDecisions_GroupOnlyHeldPathsAndCountMissingPaths()
    {
        using var fixture = new RetentionReclaimFixture();
        string FileOfSize(string relative, int size)
        {
            var path = Path.Combine(fixture.AttemptRoot(), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }
        var nonterminal = Path.GetDirectoryName(FileOfSize("active/a", 7))!;
        FileOfSize("active/nested/b", 11);
        var live = Path.GetDirectoryName(FileOfSize("live/a", 13))!;
        var finalA = FileOfSize("final/a", 17);
        var finalB = FileOfSize("final/b", 19);
        FileOfSize("final/unheld-sibling", 1000);
        var failed = FileOfSize("failed/a", 23);
        var deleted = FileOfSize("deleted/a", 2000);
        var other = FileOfSize("other/a", 4000);
        EvidenceRetentionDecision Decision(EvidenceRetentionAction action, string path, string reason) =>
            new(EvidenceArtifactFamily.AcceptanceGateAttempts, action, path, null,
                EvidenceOwnerResolution.Unrecorded, reason);
        EvidenceRetentionDecision[] decisions =
        [
            Decision(EvidenceRetentionAction.Preserved, nonterminal, "non-terminal-evidence-unbounded-by-policy"),
            Decision(EvidenceRetentionAction.DeferredLive, live, "attempt-is-unreconciled"),
            Decision(EvidenceRetentionAction.Preserved, finalA, "final-attempt"),
            Decision(EvidenceRetentionAction.Preserved, finalB, "final-attempt"),
            Decision(EvidenceRetentionAction.Preserved, failed, "last-failing-attempt"),
            Decision(EvidenceRetentionAction.Preserved, Path.Combine(fixture.AttemptRoot(), "missing"), "last-failing-attempt"),
            Decision(EvidenceRetentionAction.Deleted, deleted, "final-attempt"),
            Decision(EvidenceRetentionAction.Preserved, other, "final-attempt") with
                { Family = EvidenceArtifactFamily.PreReviewEvidenceAttempts }
        ];

        var summary = AcceptanceHeldEvidenceSummary.FromDecisions(decisions);

        AcceptanceHeldEvidenceRow[] expected =
        [
            new("DeferredLive", "attempt-is-unreconciled", 1, 13, 0),
            new("Preserved", "final-attempt", 2, 36, 0),
            new("Preserved", "last-failing-attempt", 2, 23, 1),
            new("Preserved", "non-terminal-evidence-unbounded-by-policy", 1, 18, 0)
        ];
        Assert.Equal(expected, summary.Rows);
    }

    [Fact]
    public void RepeatedPaths_CountAndMeasureEachDecision()
    {
        using var fixture = new RetentionReclaimFixture();
        var path = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "a"), 1);
        var decision = new EvidenceRetentionDecision(EvidenceArtifactFamily.AcceptanceGateAttempts,
            EvidenceRetentionAction.ReceiptWritten, path, null, EvidenceOwnerResolution.Unrecorded, "receipt");

        var row = Assert.Single(AcceptanceHeldEvidenceSummary.FromDecisions([decision, decision]).Rows);

        Assert.Equal(new AcceptanceHeldEvidenceRow("ReceiptWritten", "receipt", 2,
            2 * new FileInfo(path).Length, 0), row);
    }
}
