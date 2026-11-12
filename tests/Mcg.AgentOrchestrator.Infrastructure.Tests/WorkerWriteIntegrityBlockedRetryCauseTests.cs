using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class WorkerWriteIntegrityBlockedRetryCauseTests
{
    [Theory]
    [InlineData(WorkerWriteIntegrityLabel.Medium, true, true, false)]
    [InlineData(WorkerWriteIntegrityLabel.Low, true, false, false)]
    [InlineData(WorkerWriteIntegrityLabel.Medium, false, false, false)]
    [InlineData(null, false, false, false)]
    [InlineData(WorkerWriteIntegrityLabel.Medium, true, true, true)]
    [InlineData(WorkerWriteIntegrityLabel.Low, true, false, true)]
    [InlineData(WorkerWriteIntegrityLabel.Medium, false, false, true)]
    public void ResolveSpendsRetryOnlyForIncompleteScope(WorkerWriteIntegrityLabel? label, bool success, bool environmental, bool multiple)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Classify denied write",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var agent = new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "worker", "C:\\repo", now,
            SandboxLowIntegrity: true, WorktreeHeadSha: "head", DirtyStateHash: "dirty"));
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
            DispatchAttemptAt: now, DispatchStartedAt: now.AddSeconds(1), ChildProcessId: 123, EvidenceSource: VerificationEvidenceSource.Dispatch,
            MergedReviewFindings: [new ReviewFinding("source-finding", ReviewFindingState.Open,
                new ReviewFindingLocation("src/Feature.cs", "Write"), "Blocked implementation still needs work",
                Category: FindingCategory.Correctness)])
        { WriteIntegrityObservation = label is { } value ? new("src/Feature.cs", value, success) : null };
        var blocker = "exact-blocker - Cannot write src/Feature.cs; integrity mismatch claims Medium";
        if (multiple)
        {
            var replacement = AgentOutputDirectives.WorkerWriteDenialExample["blockers: ".Length..];
            var output = verification.StandardOutput.Replace(blocker, replacement, StringComparison.Ordinal);
            blocker = replacement;
            verification = verification with { StandardOutput = output, AuthoritativeStandardOutput = output, WriteIntegrityObservation = null };
        }
        if (label is { } observed)
        {
            IReadOnlyList<WorkerWriteIntegrityObservation> observations = multiple
                ? [new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true), new("src/Other.cs", observed, success)]
                : [verification.WriteIntegrityObservation!];
            verification = verification with
            {
                WriteIntegrityEvidence = new(task.Id, now, now.AddSeconds(1), "head", "dirty", blocker, observations,
                    new(task.Id, now, now.AddSeconds(1), "head", "dirty", 123, now.AddSeconds(2),
                        "C:\\tools\\worker.exe", 4096, "fixture native token query: S-1-16-4096"))
            };
        }
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
        Assert.Equal(VerificationEvidenceSource.Dispatch, task.LastVerification!.EvidenceSource);
        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification!);

        Assert.Equal(environmental ? "worker-write-integrity-blocked" : "incomplete-scope-declaration",
            TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(environmental ? TaskOutcomeClass.Environmental : TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal(environmental ? (RetryCause?)null : RetryCause.ContractClarification, AutomaticWorkerRetryCause.Resolve(task));
    }
}
