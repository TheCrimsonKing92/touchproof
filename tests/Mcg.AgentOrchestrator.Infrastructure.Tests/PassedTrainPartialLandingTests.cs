using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the kernel is in memory; each store and its evidence use a unique fixture root.
public sealed class PassedTrainPartialLandingTests
{
    [Fact]
    public void ThreeReceiptMembers_TwoSelected_ReportsOmissionAndTimeline()
    {
        var fixture = new Fixture(receiptMembers: 3);
        fixture.Land(selectedMembers: 2);

        fixture.AssertPartial("SelectionExcluded");
        Assert.Equal(fixture.Goals.Take(2).Select(goal => goal.Id),
            Assert.Single(fixture.Landings).Members.Select(member => member.GoalId));
    }

    [Fact]
    public void FullSelection_NoEjections_EmitsNoPartialDecision()
    {
        var fixture = new Fixture(receiptMembers: 3);
        fixture.Land(selectedMembers: 3);

        Assert.Single(fixture.Landings);
        Assert.DoesNotContain(fixture.Progress, line => line.StartsWith("TRAIN_RECEIPT_PARTIAL "));
        Assert.All(fixture.Goals, goal => Assert.DoesNotContain(goal.Timeline,
            item => item.Message.Contains("landed without this goal", StringComparison.Ordinal)));
    }

    [Fact]
    public void NewestActivationMember_DroppedBeforePass_ReportsRedNewestMember()
    {
        var fixture = new Fixture(receiptMembers: 2);
        var newest = fixture.Projections[^1];
        Assert.Equal("42c77c56", newest.GoalId.Value[..8]);
        Assert.Equal("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorContinuitySupervisor.Activation.cs",
            Assert.Single(newest.LandingPaths));
        Assert.DoesNotContain(fixture.Receipt.Identity.Members, member => member.GoalId == newest.GoalId);
        // ConductorDriver.MergeTrains.cs:393,403 records this ejection when attribution is absent.
        // Reproduce that persisted boundary; the real selector/admission must preserve the two-member pass.
        fixture.Ejections = [new(newest.GoalId, MergeTrainEjectionReason.RedNewestMember, [], "Unattributed RED")];
        fixture.Land(selectedMembers: 2, useSelector: true);

        fixture.AssertPartial("RedNewestMember");
        Assert.Equal(fixture.Goals.Take(2).Select(goal => goal.Id),
            Assert.Single(fixture.Landings).Members.Select(member => member.GoalId));
        Assert.Equal("attempt-x", Assert.Single(fixture.AttemptReads));
    }

    [Theory]
    [InlineData("outcome=replay-miss")]
    [InlineData("outcome=inflight")]
    public void NoPassedLanding_ProducesNoPartialReport(string outcome)
    {
        var fixture = new Fixture(receiptMembers: 3);
        fixture.Land(selectedMembers: 2, outcome: outcome);

        Assert.Single(fixture.Landings);
        Assert.Empty(fixture.AttemptReads);
        Assert.DoesNotContain(fixture.Progress, line => line.StartsWith("TRAIN_RECEIPT_PARTIAL "));
    }

    [Theory]
    [InlineData(MergeTrainEjectionReason.RebaseConflict)]
    [InlineData(MergeTrainEjectionReason.StaleBinding)]
    [InlineData(MergeTrainEjectionReason.MaterializationFailure)]
    [InlineData(MergeTrainEjectionReason.RedNewestMember)]
    [InlineData(MergeTrainEjectionReason.RedAttributedMember)]
    public void AttemptEjection_ReportsItsTypedRule(MergeTrainEjectionReason reason)
    {
        var fixture = new Fixture(receiptMembers: 2);
        fixture.Ejections = [new(fixture.Goals[2].Id, reason, [], "Omitted")];
        fixture.Land(selectedMembers: 2);

        fixture.AssertPartial(reason.ToString());
    }

    [Fact]
    public void DuplicateEjections_ReportsOnceUsingLastReason()
    {
        var fixture = new Fixture(receiptMembers: 2);
        fixture.Ejections =
        [
            new(fixture.Goals[2].Id, MergeTrainEjectionReason.RebaseConflict, [], "First"),
            new(fixture.Goals[2].Id, MergeTrainEjectionReason.RedAttributedMember, [], "Last"),
            new(fixture.Goals[0].Id, MergeTrainEjectionReason.StaleBinding, [], "Ultimately landed")
        ];
        fixture.Land(selectedMembers: 2);

        fixture.AssertPartial("RedAttributedMember");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedEvidence_RetainsAttemptMembersWithoutChangingIdentity(bool recheck)
    {
        var root = Path.Combine(Path.GetTempPath(), $"partial-train-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var fixture = new Fixture(receiptMembers: 2);
            var storePath = Path.Combine(root, "merge-train.db");
            var store = new MergeTrainAcceptanceStore(storePath);
            var omitted = new MergeTrainEjection(fixture.Goals[2].Id,
                MergeTrainEjectionReason.RedNewestMember, ["src/conflict.cs"], "Dropped after RED");
            store.RecordEjections("other-attempt", [omitted with { GoalId = GoalId.New() }]);
            store.RecordEjections("attempt-x", [omitted, omitted]);
            var receipt = fixture.Receipt with { ValidForLanding = true };
            if (recheck)
                receipt = receipt with
                {
                    GateAttemptId = null,
                    Recheck = new("red-train", "red-receipt", "attempt-x", "recheck-x", omitted.GoalId,
                        "reproduction", ["TestClass"], true, [], true, Fixture.Main, Fixture.Revision, ["TestClass"], 0)
                };
            var saved = MergeTrainDropBacktestTests.Fixture.Save(store, root, receipt);
            Assert.True(saved.HasAuthoritativeLandingEvidence);
            Assert.Equal(receipt.Identity.Value, saved.Identity.Value);
            Assert.Equal(2, saved.Identity.Members.Count);
            Assert.Empty(store.ReadPassedReceiptsForGoal(fixture.Goals[2].Id));
            var artifact = Assert.Single(saved.GateEvidenceArtifacts.Where(item => item.Kind == "gate-verdict"));
            using var document = JsonDocument.Parse(File.ReadAllText(artifact.Path));
            Assert.Equal(fixture.Goals.Select(goal => goal.Id.Value), document.RootElement
                .GetProperty("memberGoalIds").EnumerateArray().Select(item => item.GetString()));
            var readOnly = MergeTrainAcceptanceStore.OpenReadOnly(storePath);
            Assert.All(readOnly.ReadAttemptEjections("attempt-x"), actual =>
            {
                Assert.Equal(omitted.GoalId, actual.GoalId);
                Assert.Equal(omitted.Reason, actual.Reason);
                Assert.Equal(omitted.ConflictPaths, actual.ConflictPaths);
                Assert.Equal(omitted.Detail, actual.Detail);
            });
            Assert.Equal(2, readOnly.ReadAttemptEjections("attempt-x").Count);
            Assert.Empty(readOnly.ReadAttemptEjections("missing-attempt"));
            // Existing receipts remain usable without inspecting or rewriting their evidence.
            var bytes = File.ReadAllBytes(artifact.Path);
            fixture.Ejections = readOnly.ReadAttemptEjections("attempt-x");
            fixture.Land(selectedMembers: 2, useSelector: true);
            fixture.AssertPartial("RedNewestMember");
            Assert.Equal(bytes, File.ReadAllBytes(artifact.Path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Fixture
    {
        internal static readonly string Main = new('a', 40);
        internal static readonly string Revision = new('b', 40);
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal[] Goals { get; }
        internal GateReadyCandidateProjection[] Projections { get; }
        internal MergeTrainReceipt Receipt { get; }
        internal IReadOnlyList<MergeTrainEjection> Ejections { get; set; } = [];
        internal List<string> Progress { get; } = [];
        internal List<string> AttemptReads { get; } = [];
        internal List<ConductorMergeTrainSelection> Landings { get; } = [];

        internal Fixture(int receiptMembers)
        {
            Goals = new[] { "b3c41184", "a837a8a0", "42c77c56" }.Select(prefix =>
                Kernel.CreateGoal(new GoalId(prefix.PadRight(32, '0')), $"Member {prefix}")).ToArray();
            foreach (var goal in Goals)
                Kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["Full acceptance passes"], VerificationClass.TestVerifiable, [], [])
                    { AcceptanceGateOwnedAcceptanceCriteria = ["Full acceptance passes"] });
            Projections = Goals.Select((goal, index) => new GateReadyCandidateProjection(
                goal.Id, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied,
                ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
                [index == 2
                    ? "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorContinuitySupervisor.Activation.cs"
                    : $"src/Member{index}.cs"], [$"resource:{index}"],
                new GateReadyMergeEvidence(Revision, Main, GateReadyMergeStatus.Clean,
                    GateReadyMergeReason.NoConflictsDetected))).ToArray();
            var members = new ConductorMergeTrainSelection(Projections.Take(receiptMembers).ToArray())
                .BindMembers().Select(member => member.WithRebasedHead(Revision)).ToArray();
            Receipt = new("passed-receipt", MergeTrainIdentity.Create(members, Main, Revision, "manifest"),
                MergeTrainGateOutcome.Passed, DateTimeOffset.UnixEpoch, 0, [], 0, []) { GateAttemptId = "attempt-x" };
        }

        internal void Land(int selectedMembers, bool useSelector = false, string outcome = "outcome=passed")
        {
            var selection = new ConductorMergeTrainSelection(Projections.Take(selectedMembers).ToArray());
            var candidates = Projections.Select(projection => new ConductorSpeculativeAcceptanceCandidate(
                projection.GoalId, new GateReadyCandidateProjectionResult.Ready(projection))).ToArray();
            var operations = new PassedMergeTrainReceiptAdmission.Operations(
                (ready, ineligible, stale, blocked, scopedGoals, observed) => useSelector
                    ? PassedMergeTrainReceiptSelector.Find(ready, ineligible, stale, blocked, scopedGoals, observed,
                        _ => (Revision, Main), Goals, _ => [Receipt], () => new HashSet<string>(),
                        (actual, receipt) => actual.Members.Select(member => member.GoalId)
                            .SequenceEqual(receipt.Identity.Members.Select(member => member.GoalId)), TrunkBranchName.Default)
                    : [(selection, Receipt)],
                (goal, _) => new GateReadyCandidateProjectionResult.Ready(
                    Projections.Single(projection => projection.GoalId == goal.Id)),
                (actual, _, _) =>
                {
                    Landings.Add(actual);
                    return new(Receipt, actual.Members.ToDictionary(member => member.GoalId.Value, member =>
                        new ConductorAdvanceResult(member.GoalId.Value, member.GoalId.Value[..8], "Permissive",
                            new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp))), [], outcome);
                },
                ReadAttemptEjections: attempt => { AttemptReads.Add(attempt); return Ejections; },
                RecordDecision: (goalId, message) => Kernel.RecordGoalPolicyDecision(goalId, message));
            PassedMergeTrainReceiptAdmission.Apply(operations, ConductorAutonomyPolicy.Permissive,
                Goals, candidates, Goals, [], 49, [], new(), [], Progress.Add,
                (line, _) => Progress.Add(line), (_, _) => throw new InvalidOperationException("Unexpected hold"));
        }

        internal void AssertPartial(string reason)
        {
            Assert.Equal($"TRAIN_RECEIPT_PARTIAL tick=49 train={Receipt.Identity.Value} receipt={Receipt.ReceiptId} " +
                $"landed=b3c41184,a837a8a0 omitted=42c77c56:{reason}",
                Assert.Single(Progress.Where(line => line.StartsWith("TRAIN_RECEIPT_PARTIAL "))));
            Assert.Equal($"merge train receipt {Receipt.ReceiptId} landed without this goal: {reason}",
                Assert.Single(Goals[2].Timeline.Where(item => item.Kind == ProgressKind.GoalPolicyDecision &&
                    item.Message.Contains("landed without this goal", StringComparison.Ordinal))).Message);
            Assert.All(Goals.Take(2), goal => Assert.DoesNotContain(goal.Timeline,
                item => item.Message.Contains("landed without this goal", StringComparison.Ordinal)));
        }
    }
}
