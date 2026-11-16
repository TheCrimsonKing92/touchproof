using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class MergeTrainGateProgressEventTests : AcceptanceCohortWorkflowTests
{
    [Fact(DisplayName = "Ejections report the choosing rule and failing identities")]
    public void EjectionFormatter_ReportsPositionAndAttributionSource()
    {
        var identity = CreateEjectionIdentity();
        var goal = identity.Members[^1].GoalId;

        var position = ConductorDriver.FormatTrainEjectionObservation("outer", identity, goal,
            MergeTrainEjectionReason.RedNewestMember, "position", ["Beta.Second", "Alpha.First"]);
        var attributed = ConductorDriver.FormatTrainEjectionObservation("outer", identity, goal,
            MergeTrainEjectionReason.RedAttributedMember, "stack-frame", ["Beta.Second", "Alpha.First"]);

        Assert.Equal($"PHASE_PROGRESS phase=member-ejected outer_attempt=outer train_identity={identity.Value} " +
            $"goal={goal.Value} reason=RedNewestMember scope=merge-train rule=position failing_tests=Beta.Second,Alpha.First", position);
        Assert.Equal($"PHASE_PROGRESS phase=member-ejected outer_attempt=outer train_identity={identity.Value} " +
            $"goal={goal.Value} reason=RedAttributedMember scope=merge-train rule=stack-frame failing_tests=Beta.Second,Alpha.First", attributed);
    }

    [Theory(DisplayName = "Failing identities retain their sequence and show overflow after eight")]
    [InlineData(8, "T08,T03,T03,T01,T07,T02,T06,T04")]
    [InlineData(11, "T08,T03,T03,T01,T07,T02,T06,T04,...(+3)")]
    public void EjectionFormatter_BoundsFailingIdentities(int count, string expected)
    {
        var identity = CreateEjectionIdentity();
        var goal = identity.Members[^1].GoalId;
        string[] failingTests = ["T08", "T03", "T03", "T01", "T07", "T02", "T06", "T04", "T09", "T10", "T11"];

        var line = ConductorDriver.FormatTrainEjectionObservation("outer", identity, goal,
            MergeTrainEjectionReason.RedNewestMember, "position", failingTests.Take(count).ToArray());

        Assert.Equal($"PHASE_PROGRESS phase=member-ejected outer_attempt=outer train_identity={identity.Value} " +
            $"goal={goal.Value} reason=RedNewestMember scope=merge-train rule=position failing_tests={expected}", line);
    }

    [Fact(DisplayName = "Unreadable or empty failures retain the rule without a failing_tests field")]
    public void EjectionFormatter_OmitsUnreadableAndEmptyIdentities()
    {
        var identity = CreateEjectionIdentity();
        var goal = identity.Members[^1].GoalId;
        var expected = $"PHASE_PROGRESS phase=member-ejected outer_attempt=outer train_identity={identity.Value} " +
            $"goal={goal.Value} reason=RedNewestMember scope=merge-train rule=position";

        Assert.Equal(expected, ConductorDriver.FormatTrainEjectionObservation("outer", identity, goal,
            MergeTrainEjectionReason.RedNewestMember, "position", null));
        Assert.Equal(expected, ConductorDriver.FormatTrainEjectionObservation("outer", identity, goal,
            MergeTrainEjectionReason.RedNewestMember, "position", []));
    }

    [Fact(DisplayName = "Optional ejection diagnostics preserve the legacy line when absent")]
    public void EjectionFormatter_PreservesLegacyLine()
    {
        var identity = CreateEjectionIdentity();
        var goal = identity.Members[^1].GoalId;

        Assert.Equal($"PHASE_PROGRESS phase=member-ejected outer_attempt=outer train_identity={identity.Value} " +
            $"goal={goal.Value} reason=RedNewestMember scope=merge-train",
            ConductorDriver.FormatTrainEjectionObservation("outer", identity, goal, MergeTrainEjectionReason.RedNewestMember));
    }

    private static MergeTrainIdentity CreateEjectionIdentity()
    {
        var revision = new string('a', 40);
        var first = new MergeTrainMemberBinding(GoalId.New(), revision, revision, ["src/Changed.cs"], ["resource"],
            ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto, "ready", "fixture", RebasedTrainHeadRevision: revision);
        return MergeTrainIdentity.Create([first, first with { GoalId = GoalId.New() }], revision, revision, "manifest-v1");
    }

    [Fact]
    public void MergeTrainGate_ReportsProgressForEveryMemberWhileAttemptRuns()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = new[]
            {
                CreateCompletedGoal(kernel, "First train progress member", repo),
                CreateCompletedGoal(kernel, "Second train progress member", repo),
                CreateCompletedGoal(kernel, "Third train progress member", repo)
            };
            var paths = new[]
            {
                "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs",
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs"
            };
            for (var index = 0; index < goals.Length; index++)
            {
                var candidate = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"member {index}");
                kernel.RecordGoalRefinement(goals[index].Id, new RefinedSpec(goals[index].Objective,
                    ["The train acceptance gate passes"], VerificationClass.TestVerifiable, [], []));
                kernel.MapCriterionEvidenceOwner(goals[index].Id, 0, 1,
                    CriterionEvidenceOwner.Acceptance, "test",
                    CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
            }
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new GateProgressReportingVerifier(workspace.ConductEventsLogPath,
            [
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("train gate", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "train-progress-green.trx")])
            ]);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectTrainSelection(driver, goals);

            const string outer = "progress-test-grouped-attempt";
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, outerAttemptId: outer);

            var receipt = Assert.IsType<MergeTrainReceipt>(result.Receipt);
            var duringAttempt = Assert.Single(verifier.EventsAfterProgress);
            var trainTag = $"train={receipt.Identity.Value[..(MergeTrainIdentity.Version.Length + 9)]}";
            var orderedMembers = string.Join(',', receipt.Identity.Members.Select(member => member.GoalId.Value[..8]));
            foreach (var member in receipt.Identity.Members)
            {
                var goalId = member.GoalId.Value[..8];
                Assert.Contains(duringAttempt, record => record.EventKind == "gate-progress" &&
                    record.GoalId == goalId &&
                    record.Detail.Contains(trainTag, StringComparison.Ordinal) &&
                    record.Detail.Contains($"member={goalId}", StringComparison.Ordinal) &&
                    record.Detail.Contains($"members={orderedMembers}", StringComparison.Ordinal));
            }
            Assert.Contains(duringAttempt, record => record.Detail.Contains("phase=execution-admitted", StringComparison.Ordinal));
            Assert.All(duringAttempt.Where(record => record.EventKind == "gate-progress"), record =>
            {
                Assert.Contains("outer_attempt=" + outer, record.Detail);
                Assert.Contains("train_identity=" + receipt.Identity.Value, record.Detail);
                Assert.Contains(" execution=", record.Detail);
            });
            var replay = OwnerConsoleGateProgress.Build(duringAttempt.Max(record => record.Timestamp),
                duringAttempt.Select(record => new OwnerConductEvent(record.Timestamp, record.EventKind, record.GoalId, record.Detail)), [], []);
            Assert.Empty(replay.OtherLiveGates!); // Repeated per-member events are idempotent.
            foreach (var member in receipt.Identity.Members)
            {
                var projection = Assert.IsType<OwnerConsoleGateProgress>(replay.ForMember(member.GoalId.Value));
                Assert.Equal(outer, projection.OuterAttemptId);
                Assert.Equal(receipt.Identity.Value, projection.TrainIdentity);
                Assert.NotNull(projection.ExecutionId);
                Assert.Equal(receipt.Identity.Members.Select(binding => binding.GoalId.Value), projection.Members.Select(binding => binding.Id));
            }
            var dropped = receipt.Identity.Members[^1].GoalId;
            var at = duringAttempt.Max(record => record.Timestamp).AddSeconds(1);
            var ejectionReplay = duringAttempt.Select(record => new OwnerConductEvent(record.Timestamp, record.EventKind, record.GoalId, record.Detail))
                .Append(new(at, "gate-progress", dropped.Value,
                    ConductorDriver.FormatTrainEjectionObservation(outer, receipt.Identity, dropped, MergeTrainEjectionReason.RedNewestMember)))
                .Append(new(at.AddSeconds(1), "acceptance-cohort", dropped.Value,
                    $"ACCEPTANCE_COHORT_INFLIGHT attempt={outer} members={orderedMembers} outcome=inflight"));
            var afterEjection = OwnerConsoleGateProgress.Build(at.AddSeconds(1), ejectionReplay, [], []).ForMember(dropped.Value)!;
            Assert.False(afterEjection.Live);
            Assert.Equal("ejected / awaiting survivor", afterEjection.Participation);
            Assert.Equal(receipt.Identity.Value, afterEjection.TrainIdentity);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
