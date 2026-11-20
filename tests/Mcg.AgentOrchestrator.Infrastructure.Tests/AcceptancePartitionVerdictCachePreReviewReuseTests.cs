using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.AcceptancePreReviewEvidenceCoverageTests;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class AcceptancePartitionVerdictCachePreReviewReuseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-pre-review-reuse", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("gate-commit", null)]
    [InlineData("different-review-commit", "missing-closure")]
    public void SameTreeAndManifestReusePassingCoverageWithoutExecuting(string reviewCommit, string? closure)
    {
        var lane = Lane();
        var source = Receipt("source-pre-review") with { CandidateSha = reviewCommit };
        var cache = Create(() => [source], closure);
        var reused = cache.TryReuse(lane);
        var executions = 0;
        var result = reused ?? RunLane();

        Assert.Equal(0, executions);
        Assert.True(result.Passed);
        Assert.True(result.TestResultIsExplicitCrossAttemptReuse);
        Assert.Equal("source-pre-review", result.TestResultAttemptId);
        Assert.Equal(new[] { "source-pre-review.focused.trx" }, result.TestResultPaths);
        Assert.Contains("evidence_pointers=source-pre-review.evidence", result.ResultSummary);
        Assert.Empty(cache.Misses);
        Assert.Contains("reuse_rule=pre-review-evidence", Assert.IsType<AcceptanceCheckResult>(cache.CompleteAttempt()).ResultSummary);

        AcceptanceCheckResult RunLane()
        {
            executions++;
            return new(lane.Name, false, 1, "Unexpected lane execution.");
        }
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("manifest")]
    [InlineData("missing-tree")]
    [InlineData("missing-manifest")]
    [InlineData("red")]
    [InlineData("failed-count")]
    [InlineData("failing-tests")]
    [InlineData("timeout")]
    [InlineData("partial-receipt")]
    [InlineData("failed-check")]
    [InlineData("nonzero-exit")]
    [InlineData("synthetic")]
    [InlineData("partial-coverage")]
    [InlineData("missing-pointer")]
    public void IneligibleEvidenceFallsThroughToExecution(string condition)
    {
        var lane = Lane();
        var receipt = Receipt("source-pre-review");
        var ineligible = condition switch
        {
            "tree" => receipt with { CandidateTreeSha = "other-tree" },
            "manifest" => receipt with { ManifestIdentity = "other-manifest" },
            "missing-tree" => receipt with { CandidateTreeSha = null },
            "missing-manifest" => receipt with { ManifestIdentity = null },
            "red" => receipt with { Disposition = PreReviewEvidenceDisposition.Red },
            "failed-count" => receipt with { FailedCheckCount = 1 },
            "failing-tests" => receipt with { FailingTestIdentities = ["Alpha.Fails"] },
            "timeout" => receipt with { EvidenceTimeoutChecks = [receipt.Checks[0].Name] },
            "partial-receipt" => receipt with { PassedCheckCount = 0 },
            "failed-check" => receipt with { Checks = [receipt.Checks[0] with { Passed = false }] },
            "nonzero-exit" => receipt with { Checks = [receipt.Checks[0] with { ExitCode = 1 }] },
            "synthetic" => receipt with { Advisories = [PreReviewEvidenceReceipt.SyntheticReuseAdvisory] },
            "partial-coverage" => Receipt("source-pre-review", "FullyQualifiedName~Alpha"),
            "missing-pointer" => receipt with { EvidencePointer = null },
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };
        var cache = Create(() => [ineligible]);

        Assert.Null(cache.TryReuse(lane));
        Assert.Equal(PartitionVerdictMissReasons.ClosureHashUnavailable, Assert.Single(cache.Misses).Reason);
        cache.RecordExecution(lane, new AcceptanceCheckResult(lane.Name, true, 0, null));
        var attempt = Assert.IsType<AcceptanceCheckResult>(cache.CompleteAttempt());
        Assert.DoesNotContain("reuse_rule=pre-review-evidence", attempt.ResultSummary);
        Assert.Contains("verdict=GREEN", attempt.ResultSummary);
    }

    [Fact]
    public void AuthoritativeRedVerdictNeverConsultsPreReviewEvidence()
    {
        var lane = Lane();
        var prior = Create(null);
        prior.RecordExecution(lane, new AcceptanceCheckResult(lane.Name, false, 1, "Gate failed."));
        Assert.NotNull(prior.CompleteAttempt());
        var supplierCalls = 0;
        var cache = Create(() =>
        {
            supplierCalls++;
            return [Receipt("source-pre-review")];
        }, attempt: "second");

        Assert.Null(cache.TryReuse(lane));
        Assert.Equal(0, supplierCalls);
        Assert.Equal(PartitionVerdictMissReasons.NonreusableAuthoritativeVerdict, Assert.Single(cache.Misses).Reason);
    }

    [Fact]
    public void MissingRetainedTrxFallsThroughWhenStructuralCoverageIsEnforced()
    {
        var receipt = Receipt("source-pre-review") with { SourceAttemptId = "owner-attempt" };
        var cache = Create(() => [receipt], enforceStructuralCoverage: true);

        Assert.Null(cache.TryReuse(Lane()));
        Assert.Equal(PartitionVerdictMissReasons.MissingStructuralCoverageEvidence, Assert.Single(cache.Misses).Reason);
    }

    private AcceptancePartitionVerdictCache Create(Func<IReadOnlyList<PreReviewEvidenceReceipt>?>? supplier,
        string? closureHash = null, string attempt = "gate", bool enforceStructuralCoverage = false) =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(new GoalId(SharedGoalId), _root, [Lane()], 5, true,
                _ => Tree, _ => "main", _ => "gate-commit", () => attempt, () => Manifest, () => enforceStructuralCoverage,
                ResolveClosureHash: _ => closureHash, ResolvePreReviewEvidenceReceipts: supplier)));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
