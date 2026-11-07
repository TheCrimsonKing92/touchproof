using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class WorkerWriteIntegrityBlockedRetryCauseTests
{
    [Theory]
    [InlineData(WorkerWriteIntegrityLabel.Medium, true, true)]
    [InlineData(WorkerWriteIntegrityLabel.Low, true, false)]
    [InlineData(WorkerWriteIntegrityLabel.Medium, false, false)]
    [InlineData(null, false, false)]
    public void ResolveSpendsRetryOnlyForIncompleteScope(WorkerWriteIntegrityLabel? label, bool success, bool environmental)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Classify denied write",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var agent = new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "worker", "C:\\repo", now));
        var verification = new TaskVerificationRecord("worker", "C:\\repo", 0, """
            WORKER_RESULT:
            files: none
            commands: none
            tests: deferred - conductor
            commit: none
            blockers: exact-blocker - Cannot write src/Feature.cs; integrity mismatch claims Medium
            assigned_scope_complete: false
            model_fit: fixture/model - adequate - retry test
            skills: none
            confidence: high
            END_WORKER_RESULT
            """, "", now.AddMinutes(1), WorkerResultPresent: true, AssignedScopeComplete: false,
            MergedReviewFindings: [new ReviewFinding("source-finding", ReviewFindingState.Open,
                new ReviewFindingLocation("src/Feature.cs", "Write"), "Blocked implementation still needs work",
                Category: FindingCategory.Correctness)])
        { WriteIntegrityObservation = label is { } value ? new("src/Feature.cs", value, success) : null };
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification!);

        Assert.Equal(environmental ? "worker-write-integrity-blocked" : "incomplete-scope-declaration",
            TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(environmental ? TaskOutcomeClass.Environmental : TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal(environmental ? (RetryCause?)null : RetryCause.ContractClarification, AutomaticWorkerRetryCause.Resolve(task));
    }
}
