using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: private kernel and source snapshot; no real evidence runner or shared state.
public sealed class Ce9572ecCandidateRedAttributionTests
{
    private const string CandidateSha = "aaa23b4bf79cf2301f94e24fe280e243c0fe54c8";
    private const string LifecyclePath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CompletionBuildEvidenceLifecycleTests.cs";
    private const string CapacityPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerBuildCheckRecoveryTestsCapacityQueue.cs";
    private static readonly string[] Failures =
    [
        "CompletionBuildEvidenceLifecycleTests.Protocol1QueueDeadlineBuildsSameCandidateWithoutWorkerRetry",
        "WorkerBuildCheckRecoveryTestsCapacityQueue.NonQueueInfrastructureWithIncompleteScopeMatchesNoInvocationDecision(outcome: Interrupted)",
        "WorkerBuildCheckRecoveryTestsCapacityQueue.NonQueueInfrastructureWithIncompleteScopeMatchesNoInvocationDecision(outcome: TimeoutAfterStart)",
        "WorkerBuildCheckRecoveryTestsCapacityQueue.NonQueueInfrastructureWithIncompleteScopeMatchesNoInvocationDecision(outcome: ApparatusFailure)",
        "WorkerBuildCheckRecoveryTestsCapacityQueue.CompilerFailureBesideUnconsumedQueueMatchesCompilerOnlyDecision"
    ];

    [Theory]
    [InlineData(FindingCategory.Unspecified, true)]
    [InlineData(FindingCategory.SpecCompliance, true)]
    [InlineData(FindingCategory.SpecDefect, false)]
    [InlineData(FindingCategory.Correctness, true)]
    [InlineData(FindingCategory.TestEvidence, true)]
    [InlineData(FindingCategory.TestCoverage, true)]
    [InlineData(FindingCategory.CodeQuality, true)]
    [InlineData(FindingCategory.OperatorOwned, false)]
    [InlineData(FindingCategory.AcceptanceOwned, false)]
    public void CheckLevelFailuresReachAttribution_OnlyOwnershipCategoriesReturnNull(
        FindingCategory category, bool developerOwned)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            // Only source declarations are needed by the real changed-source resolver. Keep the
            // recorded paths/classes while avoiding a dependency on the current checkout's contents.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, LifecyclePath))!);
            File.WriteAllText(Path.Combine(root, LifecyclePath), "public sealed class " + Path.GetFileNameWithoutExtension(LifecyclePath) + " {}");
            File.WriteAllText(Path.Combine(root, CapacityPath), "public sealed class " + Path.GetFileNameWithoutExtension(CapacityPath) + " {}");
            var (_, goal) = SoftwareGoal();
            var driver = MakeDriver(executionDirectory: root,
                getLandingFileScopes: _ => [LifecyclePath, CapacityPath]);
            var batch = RecordedBatch(category);
            var check = new AcceptanceCheckResult("candidate", false, 2, "failed: 5",
                FailingTestIdentities: Failures, ExecutedTestCount: 35);
            var result = new FocusedEvidenceArmRunResult(FindingEvidenceArm.Candidate, CandidateSha,
                FindingEvidenceArmDisposition.Red, true, false, "candidate red", [check]);

            // Use the production builder: result.json has failures under checks, not on the arm.
            var arm = ConductorDriver.CreateFindingEvidenceArmReceipt(result);
            Assert.Equal(Failures, arm.FailingTestIdentities);
            Assert.Equal(35, arm.ExecutedTestCount);
            Assert.Equal(Failures, CandidateRedBaselineInheritance.AttributableFailures(arm, null).Failures);

            var attribution = driver.TryAttributeActionableCandidateRed(goal, CandidateSha, [arm], batch);

            // A developer-owned control with the identical receipt and source scopes distinguishes
            // category filtering (594-608) from arm/changed-source (565-571) or inherited (575-577).
            var control = driver.TryAttributeActionableCandidateRed(
                goal, CandidateSha, [arm], RecordedBatch(FindingCategory.TestEvidence));
            Assert.NotNull(control);
            Assert.Equal(Failures, control.FailingTestIdentities);
            Assert.Equal(2, control.Findings.Count);
            if (developerOwned)
            {
                Assert.NotNull(attribution);
                Assert.Equal(Failures, attribution.FailingTestIdentities);
                Assert.Equal(batch.Findings, attribution.Findings);
            }
            else
            {
                Assert.Null(attribution);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FindingEvidenceBatch RecordedBatch(FindingCategory category)
    {
        List<ReviewFinding> findings =
        [
            Finding("ce9572ec-lifecycle-history-equality", LifecyclePath),
            Finding("ce9572ec-capacity-control-feedback-delegate", CapacityPath)
        ];
        var members = findings.Select(finding => new FindingEvidenceRequestGroup(
            finding.StableId, "recorded ce9572ec selection", finding.EvidenceRequest!, [finding])).ToArray();
        return new("ce9572ec-batch", "recorded ce9572ec selection", members[0].TypedRequest, findings, members);

        ReviewFinding Finding(string id, string path) => EvidenceFindingWithRequest("Recorded candidate red", id,
            category: category, classes: ["CompletionBuildEvidenceLifecycleTests", "WorkerBuildCheckRecoveryTestsCapacityQueue"]) with
        {
            Location = new ReviewFindingLocation(path, id)
        };
    }
}
