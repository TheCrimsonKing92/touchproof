using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class PreReviewEvidenceReceiptIdentityTests
{
    [Fact]
    public void RecordingStampsCandidateIdentityAndSyntheticReuseCopiesIt()
    {
        var (_, goal) = ConductorDriverTests.SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        const string filter = "FullyQualifiedName~Alpha";
        const string selection = "Infrastructure.Tests: " + filter;
        var context = new PreReviewEvidenceContext("review-commit", [selection], selection, "focused", false, false);
        var check = new AcceptanceCheckResult("reviewer focused evidence: Infrastructure.Tests " + filter, true, 0, null);
        var receipts = new List<PreReviewEvidenceReceipt>();

        var recorded = PreReviewEvidenceReceipts.Record((_, _, receipt) => receipts.Add(receipt), goal, reviewer,
            context, 1, PreReviewEvidenceDisposition.Green, [check], [], "source.trx",
            candidateIdentity: ("tree-sha", "effective-manifest-sha256-example"), sourceAttemptId: "pre-review-run");
        Assert.Same(recorded, Assert.Single(receipts));
        Assert.Equal("tree-sha", recorded.CandidateTreeSha);
        Assert.Equal("effective-manifest-sha256-example", recorded.ManifestIdentity);
        Assert.Equal("pre-review-run", recorded.SourceAttemptId);
        Assert.False(recorded.ContentEquals(recorded with { ManifestIdentity = "other-manifest" }));

        PreReviewEvidenceReceipts.RecordReuse((_, _, receipt) => receipts.Add(receipt), goal, reviewer, context, 2, [recorded]);

        Assert.Equal(2, receipts.Count);
        var reused = receipts[1];
        Assert.True(reused.IsSyntheticReuse);
        Assert.Equal(recorded.CandidateTreeSha, reused.CandidateTreeSha);
        Assert.Equal(recorded.ManifestIdentity, reused.ManifestIdentity);
    }

    [Fact]
    public void LegacyJsonDeserializesAndRewritesWithoutIdentityProperties()
    {
        const string legacy = """
            {"GoalId":"goal","ReviewerRound":1,"CandidateSha":"commit","SelectedFocusedTests":["Alpha"],
             "Disposition":"Green","PassedCheckCount":1,"FailedCheckCount":0,"Checks":[],
             "FailingTestIdentities":[],"MappingReason":"focused","EvidencePointer":"source.trx",
             "RecordedAt":"2026-11-19T00:00:00+00:00"}
            """;

        var receipt = Assert.IsType<PreReviewEvidenceReceipt>(JsonSerializer.Deserialize<PreReviewEvidenceReceipt>(legacy));

        Assert.Equal("commit", receipt.CandidateSha);
        Assert.Null(receipt.CandidateTreeSha);
        Assert.Null(receipt.ManifestIdentity);
        var rewritten = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("CandidateTreeSha", rewritten);
        Assert.DoesNotContain("ManifestIdentity", rewritten);
        Assert.DoesNotContain("SourceAttemptId", rewritten);
    }
}
