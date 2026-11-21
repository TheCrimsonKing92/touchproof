using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each invocation owns its kernel, fake providers and temporary directory.
public sealed class CommittedArtifactOwnerRewriteRefinementTests
{
    private const string TesterArtifact = "A committed test asserts X. Tester owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string DeveloperArtifact = "A committed test asserts X. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string DiagnosticPrefix = "Spec refiner parse diagnostic: Spec refiner owner rewritten: ";

    [Fact]
    public async Task FilingStoresDeveloperOwnerAndRecordsRewrite()
    {
        var goal = await FileAndRefine(false, TesterArtifact);

        Assert.Equal(DeveloperArtifact, Assert.Single(goal.RefinedSpec!.AcceptanceCriteria));
        Assert.Equal(DeveloperArtifact, Assert.Single(goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria));
        Assert.Equal(CriterionEvidenceOwner.Acceptance, Assert.Single(goal.CriterionEvidenceObligations).Owner);
        Assert.Equal(DiagnosticPrefix + "declared criterion 1 requires a committed artifact; owner Tester -> Developer",
            Assert.Single(RewriteDiagnostics(goal)));
    }

    [Theory]
    [InlineData("A new machine-readable audit report is committed.")]
    [InlineData("The change writes a machine-readable audit report.")]
    public async Task QualifiedArtifactProductionMovesTesterOwnershipToDeveloper(string requirement)
    {
        var goal = await FileAndRefine(false,
            $"{requirement} Tester owns; Acceptance executes. TEST-VERIFIABLE.");

        var expected = $"{requirement} Developer owns; Acceptance executes. TEST-VERIFIABLE.";
        Assert.Equal(expected, Assert.Single(goal.RefinedSpec!.AcceptanceCriteria));
        Assert.Equal(expected, Assert.Single(goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria));
        Assert.Equal(DiagnosticPrefix + "declared criterion 1 requires a committed artifact; owner Tester -> Developer",
            Assert.Single(RewriteDiagnostics(goal)));
    }

    [Theory]
    [InlineData(false, "The change adds a script and runs the existing tests.")]
    [InlineData(true, "The existing `docs/behavior.md` file must be updated.")]
    public async Task FileChangesMoveReviewerOwnershipToDeveloper(bool useFallback, string requirement)
    {
        var goal = await FileAndRefine(useFallback,
            $"{requirement} Reviewer owns; Reviewer executes. TEST-VERIFIABLE.");

        Assert.Equal($"{requirement} Developer owns; Reviewer executes. TEST-VERIFIABLE.",
            Assert.Single(goal.RefinedSpec!.AcceptanceCriteria));
        Assert.Equal(DiagnosticPrefix + "declared criterion 1 requires a committed artifact; owner Reviewer -> Developer",
            Assert.Single(RewriteDiagnostics(goal)));
    }

    [Fact]
    public async Task FocusedEvidenceFromExistingTestsKeepsTesterOwner()
    {
        const string evidenceOnly =
            "A focused evidence run of existing named tests BriefLintTests and AcceptanceCriterionOwnershipMarkerTests passes. " +
            "Tester owns; Acceptance executes. TEST-VERIFIABLE.";
        var goal = await FileAndRefine(false, evidenceOnly, TesterArtifact);

        Assert.Equal([evidenceOnly, DeveloperArtifact], goal.RefinedSpec!.AcceptanceCriteria);
        Assert.Equal([evidenceOnly, DeveloperArtifact], goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Assert.Equal(DiagnosticPrefix + "declared criterion 2 requires a committed artifact; owner Tester -> Developer",
            Assert.Single(RewriteDiagnostics(goal)));
    }

    [Theory]
    [InlineData("Existing tests are included in the focused evidence run.")]
    [InlineData("A new focused evidence run of existing tests is produced.")]
    public async Task EvidenceRunLanguageKeepsTesterOwnerWithoutRewriteDiagnostic(string requirement)
    {
        var criterion = $"{requirement} Tester owns; Acceptance executes. TEST-VERIFIABLE.";
        var goal = await FileAndRefine(false, criterion);

        Assert.Equal(criterion, Assert.Single(goal.RefinedSpec!.AcceptanceCriteria));
        Assert.Equal(criterion, Assert.Single(goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria));
        Assert.Empty(RewriteDiagnostics(goal));
    }

    private static IEnumerable<string> RewriteDiagnostics(Goal goal) => goal.Timeline
        .Where(entry => entry.Kind == ProgressKind.GoalPolicyDecision &&
                        entry.Message.StartsWith(DiagnosticPrefix, StringComparison.Ordinal))
        .Select(entry => entry.Message);

    private static async Task<Goal> FileAndRefine(bool useFallback, params string[] criteria)
    {
        const string contract = "Implement the focused behavior.";
        var objective = $"{contract}\n\n## Acceptance criteria\n\n" +
            string.Join('\n', criteria.Select((criterion, index) => $"{index + 1}. {criterion}"));
        // Text-only claims exercise matching when the model omits declared_index.
        var response = useFallback ? "{}" : JsonSerializer.Serialize(new
        {
            behavioralContract = contract,
            acceptanceCriteria = criteria.Select(text => new { text, evidence_owner = "acceptance-gate" }),
            verificationClass = "TestVerifiable",
            decisions = Array.Empty<object>(),
            forks = Array.Empty<object>()
        });
        var provider = new FakeSmokeProvider(response, providerName: "fake-refiner");
        var root = Directory.CreateTempSubdirectory("artifact-owner-refinement-");
        try
        {
            var service = new GoalRefinementService(
                new InMemoryModelProviderRegistry([provider]),
                new ModelFunctionCatalog([
                    new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                        new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
                ]),
                new FakeCollaborationItemStore(),
                new SpecRefinerPrecedentStore(Path.Combine(root.FullName, "precedents.json")),
                rawOutputDirectory: Path.Combine(root.FullName, "logs"));
            var kernel = new AgentOrchestratorKernel();
            var filed = kernel.CreateGoal(objective);

            var result = await service.RefineAsync(kernel, filed.Id);

            Assert.NotNull(provider.LastRequest);
            Assert.Equal(useFallback ? RefinementDisposition.Fallback : RefinementDisposition.Completed,
                result.Disposition);
            Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
            var stored = kernel.GetGoal(filed.Id);
            Assert.Empty(stored.RefinedSpec!.OpenQuestions);
            return stored;
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
