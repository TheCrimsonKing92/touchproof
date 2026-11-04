using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PassedCohortApprovalOverlapObservationTests
{
    private static readonly DateTimeOffset PassedAt = new(2026, 10, 29, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly string Main = new('a', 40);
    private static readonly string Revision = new('b', 40);
    private static readonly GoalId First = new(new string('1', 32));
    private static readonly GoalId Second = new(new string('2', 32));

    [Fact]
    public void SharedPaths_UsesReceiptUnionAndExistingNormalizationAndPrefixSemantics()
    {
        var receipt = Receipt([" src\\Z.cs/ ", "src/Tree", "src/Z.cs"], ["src/A.cs", "SRC/z.cs"]);
        var candidates = new[]
        {
            Candidate(new GoalId(new string('4', 32)), ["src/z.cs", "src/A.cs", "src/Tree/Child.cs"]),
            Candidate(new GoalId(new string('3', 32)), ["src/a.cs", "src/TreeSibling.cs"]),
            Candidate(First, ["src/A.cs"], revision: new string('c', 40)),
            Candidate(Second, ["src/Z.cs"])
        };
        using var document = Parse(Format(receipt, candidates));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("totalOverlapGoals").GetInt32());
        Assert.Equal(0, root.GetProperty("omittedGoalCount").GetInt32());
        var overlaps = root.GetProperty("overlaps").EnumerateArray().ToArray();
        Assert.Equal(new string('3', 32), overlaps[0].GetProperty("goalId").GetString());
        Assert.Equal(new[] { "src/A.cs" }, Strings(overlaps[0], "sharedPaths"));
        Assert.Equal(1, overlaps[0].GetProperty("sharedPathCount").GetInt32());
        Assert.Equal(new string('4', 32), overlaps[1].GetProperty("goalId").GetString());
        Assert.Equal(Revision, overlaps[1].GetProperty("candidateRevision").GetString());
        Assert.Equal(new[] { "SRC/z.cs", "src/A.cs", "src/Tree" }, Strings(overlaps[1], "sharedPaths"));
        Assert.Equal(3, overlaps[1].GetProperty("sharedPathCount").GetInt32());
        Assert.Equal(0, overlaps[1].GetProperty("omittedPathCount").GetInt32());
        Assert.Equal(Format(receipt, candidates), Format(receipt, candidates.Reverse().ToArray()));
        var reordered = Receipt(["src/Z.cs", "src/Tree", " src\\Z.cs/ "], ["SRC/z.cs", "src/A.cs"]);
        using var reorderedJson = Parse(Format(reordered, candidates));
        Assert.Equal(root.GetProperty("overlaps").GetRawText(),
            reorderedJson.RootElement.GetProperty("overlaps").GetRawText());
    }

    [Fact]
    public void PreviewLimits_CountAllGoalsAndPathsBeforeTakingTen()
    {
        var paths = Enumerable.Range(0, 13).Select(index => $"src/P{index:D2}.cs").ToArray();
        var candidates = Enumerable.Range(20, 12).Reverse()
            .Select(index => Candidate(new GoalId(index.ToString("x32")), paths.Reverse().ToArray())).ToArray();
        using var document = Parse(Format(Receipt(paths, ["elsewhere.cs"]), candidates));
        var root = document.RootElement;
        Assert.Equal(12, root.GetProperty("totalOverlapGoals").GetInt32());
        Assert.Equal(2, root.GetProperty("omittedGoalCount").GetInt32());
        var rows = root.GetProperty("overlaps").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(20, 10).Select(index => index.ToString("x32")),
            rows.Select(row => row.GetProperty("goalId").GetString()));
        Assert.All(rows, row =>
        {
            Assert.Equal(13, row.GetProperty("sharedPathCount").GetInt32());
            Assert.Equal(paths.Take(10), Strings(row, "sharedPaths"));
            Assert.Equal(3, row.GetProperty("omittedPathCount").GetInt32());
        });
    }

    [Fact]
    public void DisjointUnavailableAndDifferentMain_ProduceExplicitEmptyOverlaps()
    {
        var unavailableId = new GoalId(new string('5', 32));
        ConductorSpeculativeAcceptanceCandidate[] candidates =
        [
            Candidate(new GoalId(new string('3', 32)), ["src/Other.cs"]),
            Candidate(new GoalId(new string('4', 32)), ["src/A.cs"], main: new string('c', 40)),
            new(unavailableId, new GateReadyCandidateProjectionResult.Excluded(
                GateReadyCandidateExclusionReason.RevisionUnknown)),
            Candidate(First, ["src/A.cs"], revision: new string('d', 40))
        ];
        using var document = Parse(Format(Receipt(["src/A.cs"], ["src/B.cs"]), candidates));
        Assert.Empty(document.RootElement.GetProperty("overlaps").EnumerateArray());
        Assert.Equal(0, document.RootElement.GetProperty("totalOverlapGoals").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("omittedGoalCount").GetInt32());
    }

    [Theory]
    [InlineData(12750, 12)]
    [InlineData(-12750, 0)]
    public void IdentityAndAge_KeepFullBindingsAndUseFlooredNonnegativePassAge(int elapsedMs, long age)
    {
        var receipt = Receipt(["src/A.cs"], ["src/B.cs"]);
        using var document = Parse(Format(receipt, [], PassedAt.AddMilliseconds(elapsedMs)));
        var root = document.RootElement;
        Assert.Equal(new[] { "receiptIdentity", "heldMemberIds", "missingApprovalIds", "completedAt",
                "receiptAgeSeconds", "totalOverlapGoals", "omittedGoalCount", "overlaps" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("2026-10-29T10:00:00.0000000Z", root.GetProperty("completedAt").GetString());
        Assert.Equal(age, root.GetProperty("receiptAgeSeconds").GetInt64());
        Assert.Equal(new[] { First.Value, Second.Value }, Strings(root, "heldMemberIds"));
        Assert.Equal(new[] { Second.Value }, Strings(root, "missingApprovalIds"));
        var identity = root.GetProperty("receiptIdentity");
        Assert.Equal(receipt.ReceiptId, identity.GetProperty("receiptId").GetString());
        Assert.Equal(AcceptanceCohortIdentity.Version, identity.GetProperty("version").GetString());
        Assert.Equal(receipt.Identity.Value, identity.GetProperty("value").GetString());
        Assert.Equal(Main, identity.GetProperty("observedMainRevision").GetString());
        Assert.Equal(receipt.Identity.CombinedTreeRevision, identity.GetProperty("combinedTreeRevision").GetString());
        Assert.Equal(receipt.Identity.ManifestIdentity, identity.GetProperty("manifestIdentity").GetString());
        var bindings = identity.GetProperty("members").EnumerateArray().ToArray();
        for (var index = 0; index < bindings.Length; index++)
        {
            var expected = receipt.Identity.Members[index];
            var actual = bindings[index];
            Assert.Equal(expected.GoalId.Value, actual.GetProperty("goalId").GetString());
            Assert.Equal(expected.BranchRevision, actual.GetProperty("branchRevision").GetString());
            Assert.Equal(expected.CandidateRevision, actual.GetProperty("candidateRevision").GetString());
            Assert.Equal(expected.LandingPaths, Strings(actual, "landingPaths"));
            Assert.Equal(expected.ResourceKeys, Strings(actual, "resourceKeys"));
            Assert.Equal(expected.ChangeRiskTier.ToString(), actual.GetProperty("changeRiskTier").GetString());
            Assert.Equal(expected.AutoPromotionDisposition.ToString(), actual.GetProperty("autoPromotionDisposition").GetString());
            Assert.Equal(expected.MergeStatus, actual.GetProperty("mergeStatus").GetString());
            Assert.Equal(expected.MergeReason, actual.GetProperty("mergeReason").GetString());
            Assert.Equal(expected.BriefVersion, actual.GetProperty("briefVersion").GetInt32());
        }
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("invalidated")]
    [InlineData("stale-main")]
    [InlineData("stale-member")]
    [InlineData("unavailable-member")]
    [InlineData("unrelated-hold")]
    [InlineData("approved")]
    public void IneligibleReceipt_CannotSupplyApprovalOnlyObservation(string condition)
    {
        // Isolated content-bound files; no repository, process or shared store is used.
        var root = Directory.CreateTempSubdirectory("approval-overlap-").FullName;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 2).Select(index =>
            {
                var goal = kernel.CreateGoal($"Member {index}", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
                kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id,
                    ManualVerificationRecorder.Create(true, "Passed.", root, PassedAt));
                Assert.Equal(GoalStatus.Verified, goal.Status);
                return goal;
            }).ToArray();
            var receipt = Receipt(["src/A.cs"], ["src/B.cs"], goals.Select(goal => goal.Id).ToArray());
            var trx = Path.Combine(root, "pass.trx");
            File.WriteAllText(trx, """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
                  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
                </TestRun>
                """);
            var verdict = Path.Combine(root, "verdict.json");
            File.WriteAllText(verdict, "{}");
            receipt = receipt with { GateTestResultPaths = [trx], GateEvidenceArtifacts = [Artifact("trx", trx), Artifact("gate-verdict", verdict)] };
            Assert.True(receipt.HasAuthoritativeLandingEvidence);
            var projections = goals.ToDictionary(goal => goal.Id,
                goal => Candidate(goal.Id, ["src/A.cs"]).ProjectionResult);
            PassedCohortReceiptApprovalHold.Decision? Evaluate(AcceptanceCohortReceipt current,
                Func<AcceptanceCohortMemberBinding, LandingDenylistOwnerGate.Result>? approval = null) =>
                PassedCohortReceiptApprovalHold.Evaluate(current, goals.ToDictionary(goal => goal.Id),
                    projections, goals.Select(goal => goal.Id).ToHashSet(), _ => false,
                    approval ?? (_ => new([], "content-fingerprint", false, "missing approval")));
            Assert.NotNull(Evaluate(receipt));
            Func<AcceptanceCohortMemberBinding, LandingDenylistOwnerGate.Result>? approval = null;
            switch (condition)
            {
                case "failed": receipt = receipt with { Outcome = AcceptanceCohortGateOutcome.Failed }; break;
                case "invalidated": receipt = receipt with { Invalidation = new("invalidation", receipt.Identity.Value,
                    AcceptanceCohortInvalidationReason.BindingChanged, "main changed", PassedAt, Main,
                    receipt.Identity.CombinedTreeRevision, receipt.Identity.ManifestIdentity, receipt.Identity.Members) }; break;
                case "stale-main": projections[goals[0].Id] = Candidate(goals[0].Id, ["src/A.cs"], main: new string('c', 40)).ProjectionResult; break;
                case "stale-member": projections[goals[0].Id] = Candidate(goals[0].Id, ["src/A.cs"], revision: new string('c', 40)).ProjectionResult; break;
                case "unavailable-member": projections[goals[0].Id] = new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.RevisionUnknown); break;
                case "unrelated-hold": approval = _ => new([], "unavailable", false, "observation unavailable"); break;
                case "approved": approval = _ => new([], "content-fingerprint", true, ""); break;
            }
            var hold = Evaluate(receipt, approval);
            Assert.Null(hold);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AcceptanceCohortEvidenceArtifact Artifact(string kind, string path) =>
        new(kind, path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length);

    private static AcceptanceCohortReceipt Receipt(string[] firstPaths, string[] secondPaths, GoalId[]? ids = null)
    {
        ids ??= [First, Second];
        var members = ids.Select((id, index) => new AcceptanceCohortMemberBinding(id, Revision, Revision,
            index == 0 ? firstPaths : secondPaths, [$"resource:{index}"], ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected", briefVersion: 1)).Reverse().ToArray();
        return new("full-receipt-identity", AcceptanceCohortIdentity.Create(members, Main, new string('d', 40), "manifest-v1"),
            AcceptanceCohortGateOutcome.Passed, PassedAt, 12, [], 0, [], ValidForLanding: true);
    }

    private static ConductorSpeculativeAcceptanceCandidate Candidate(GoalId id, string[] paths,
        string? main = null, string? revision = null) => new(id,
        new GateReadyCandidateProjectionResult.Ready(new(id, GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            paths, ["resource:ready"], new(revision ?? Revision, main ?? Main,
                GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected))));

    private static string Format(AcceptanceCohortReceipt receipt, ConductorSpeculativeAcceptanceCandidate[] candidates,
        DateTimeOffset? observedAt = null) => PassedCohortApprovalOverlapObservation.Format(
        new(receipt, "unchanged hold reason", [(Second, "missing approval")]), candidates, observedAt ?? PassedAt);

    private static JsonDocument Parse(string line)
    {
        Assert.StartsWith("COHORT_APPROVAL_OVERLAP ", line, StringComparison.Ordinal);
        return JsonDocument.Parse(line["COHORT_APPROVAL_OVERLAP ".Length..]);
    }

    private static string[] Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();
}
