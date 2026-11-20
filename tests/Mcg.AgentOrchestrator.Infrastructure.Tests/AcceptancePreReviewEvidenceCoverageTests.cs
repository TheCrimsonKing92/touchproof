using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class AcceptancePreReviewEvidenceCoverageTests
{
    internal const string SharedGoalId = "12345678123456781234567812345678";
    internal const string Tree = "tree-a";
    internal const string Manifest = "manifest-a";
    internal const string Filter = "FullyQualifiedName~Alpha|FullyQualifiedName~Beta";

    [Fact]
    public void CombinesPassingChecksAndPreservesEveryContributingSource()
    {
        var alpha = Receipt("alpha-attempt", "FullyQualifiedName~Alpha");
        var beta = Receipt("beta-attempt", "FullyQualifiedName~Beta");
        var extra = Receipt("extra-attempt", "FullyQualifiedName~Gamma");

        var match = Assert.IsType<AcceptancePreReviewEvidenceCoverage.MatchResult>(Match([alpha, beta, extra]));
        Assert.Equal(new[] { "alpha-attempt", "beta-attempt" }, match.AttemptIds.Order());
        Assert.Equal(new[] { "alpha-attempt.evidence", "beta-attempt.evidence" }, match.EvidencePointers.Order());
        Assert.Equal(new[] { "alpha-attempt.focused.trx", "beta-attempt.focused.trx" }, match.TestResultPaths.Order());
    }

    [Theory]
    [InlineData("FullyQualifiedName~AlphaTests.Method")]
    [InlineData("FullyQualifiedName=Alpha")]
    [InlineData("FullyQualifiedName~Alpha&Category!=Slow")]
    [InlineData("FullyQualifiedName~Alpha|")]
    public void AmbiguousOrRestrictedGateSelectionsCannotReuse(string filter) =>
        Assert.Null(AcceptancePreReviewEvidenceCoverage.Match(Lane(filter), SharedGoalId, Tree, Manifest,
            [Receipt("source", filter)]));

    [Theory]
    [InlineData("reviewer focused evidence: Core.Tests FullyQualifiedName~Alpha|FullyQualifiedName~Beta")]
    [InlineData("reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~Alpha|FullyQualifiedName~Beta.Method")]
    [InlineData("reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~Alpha|FullyQualifiedName~Beta&Category!=Slow")]
    [InlineData("reviewer mapped project evidence: Infrastructure.Tests")]
    public void RequestedSelectionAloneCannotProveCoverage(string executedCheckName)
    {
        var receipt = Receipt("source", Filter);
        Assert.Null(Match([receipt with { Checks = [receipt.Checks[0] with { Name = executedCheckName }] }]));
    }

    [Fact]
    public void NewerFailureSupersedesOldGreenCoverageOfTheSameTree()
    {
        var green = Receipt("green", Filter);
        var red = Receipt("red", "FullyQualifiedName~Beta") with
        {
            CandidateSha = "other-commit",
            Disposition = PreReviewEvidenceDisposition.Red,
            RecordedAt = green.RecordedAt.AddTicks(1)
        };

        Assert.NotNull(Match([green]));
        Assert.Null(Match([green, red]));
    }

    [Fact]
    public void SyntheticReceiptDoesNotSupersedeOriginalOwnedEvidence()
    {
        var original = Receipt("transport") with { SourceAttemptId = "actual-pre-review" };
        var synthetic = original with
        {
            Advisories = [PreReviewEvidenceReceipt.SyntheticReuseAdvisory],
            RecordedAt = original.RecordedAt.AddTicks(1)
        };

        var match = Assert.IsType<AcceptancePreReviewEvidenceCoverage.MatchResult>(Match([original, synthetic]));
        Assert.Equal("actual-pre-review", Assert.Single(match.AttemptIds));
    }

    private static AcceptancePreReviewEvidenceCoverage.MatchResult? Match(IReadOnlyList<PreReviewEvidenceReceipt> receipts) =>
        AcceptancePreReviewEvidenceCoverage.Match(Lane(), SharedGoalId, Tree, Manifest, receipts);

    internal static AcceptanceManifestCheck Lane(string filter = Filter) => new()
    {
        Name = "infrastructure tests: focused proof",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", filter]
    };

    internal static PreReviewEvidenceReceipt Receipt(string attempt, string filter = Filter) =>
        new(SharedGoalId, 1, "review-commit", [$"Infrastructure.Tests: {filter}"], PreReviewEvidenceDisposition.Green,
            1, 0, [new($"reviewer focused evidence: Infrastructure.Tests {filter}", filter, true, 0,
                TestResultPaths: [$"{attempt}.focused.trx"])], [], "focused", $"{attempt}.evidence",
            DateTimeOffset.UnixEpoch, CandidateTreeSha: Tree, ManifestIdentity: Manifest);
}
