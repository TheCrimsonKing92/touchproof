using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its prompt directory and mutates no environment.
public sealed class RetryCorrectionObligationPromptGuardTests
{
    private const string Candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("candidate")]
    [Xunit.InlineData("condition")]
    [Xunit.InlineData("hash")]
    [Xunit.InlineData("feedback")]
    public void TamperedObligation_BlocksDispatchDespiteExactTypedFeedback(string tamper)
    {
        WithPrompt((goal, task, package, path) =>
        {
            var accepted = task.AcceptedRetryFeedback!;
            var projection = RetryCorrectionObligationBriefSection.Projection(accepted.Obligation!, accepted.Message);
            projection = tamper switch
            {
                "missing" => string.Empty,
                "candidate" => projection.Replace(Candidate, new string('c', 40), StringComparison.Ordinal),
                "condition" => projection.Replace("source-size-ratchet", "unrelated-condition", StringComparison.Ordinal),
                "hash" => projection.Replace(accepted.Obligation!.FeedbackSha256, new string('0', 64), StringComparison.Ordinal),
                _ => projection.Replace(accepted.Message, "old finding repaired", StringComparison.Ordinal)
            };
            File.WriteAllText(path, WorkerContextPackageBuilder.Render(package) + Environment.NewLine + projection);

            var error = Xunit.Assert.Throws<WorkerSubscriptionPreflightException>(() =>
                WorkerRetryFeedbackPromptGuard.Validate(goal, task, package,
                    WorkerContextPackageBuilder.CreateReceipt(package), path));
            Xunit.Assert.Equal("retry-feedback-not-delivered", error.ErrorCode);
            Xunit.Assert.Null(task.LastDispatch);
            Xunit.Assert.Null(task.LastProcess);
        });
    }

    [Xunit.Fact]
    public void DeliveredObligation_RecordsBoundHashCandidateAndProjection()
    {
        WithPrompt((goal, task, package, path) =>
        {
            var accepted = task.AcceptedRetryFeedback!;
            var projection = RetryCorrectionObligationBriefSection.Projection(accepted.Obligation!, accepted.Message);
            File.WriteAllText(path, WorkerContextPackageBuilder.Render(package) + Environment.NewLine + projection);
            var receipt = WorkerRetryFeedbackPromptGuard.Validate(goal, task, package,
                WorkerContextPackageBuilder.CreateReceipt(package), path)!;

            var delivery = Xunit.Assert.IsType<WorkerRetryFeedbackPromptReceipt>(receipt.RetryFeedbackPromptReceipt);
            Xunit.Assert.Equal(RetryCorrectionObligation.HashFeedback(accepted.Message), delivery.ObligationFeedbackSha256);
            Xunit.Assert.Equal(Candidate, delivery.ObligationCandidateSha);
            Xunit.Assert.Equal("source-size-ratchet", delivery.ObligationCondition);
            Xunit.Assert.Equal(WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(projection)), delivery.ObligationProjectionSha256);
            Xunit.Assert.Equal(WorkerContextArtifact.Hash(File.ReadAllBytes(path)), delivery.GeneratedPromptSha256);
            Xunit.Assert.Equal("task/criterion-retry-feedback.json", delivery.LogicalIdentity);
        });
    }

    [Xunit.Fact]
    public void LegacyFeedback_KeepsExistingDeliveryWithoutObligationFields()
    {
        WithPrompt((goal, task, package, path) =>
        {
            File.WriteAllText(path, WorkerContextPackageBuilder.Render(package));
            var receipt = WorkerRetryFeedbackPromptGuard.Validate(goal, task, package,
                WorkerContextPackageBuilder.CreateReceipt(package), path)!;
            var delivery = Xunit.Assert.IsType<WorkerRetryFeedbackPromptReceipt>(receipt.RetryFeedbackPromptReceipt);
            Xunit.Assert.Equal(RetryCorrectionObligation.HashFeedback(task.AcceptedRetryFeedback!.Message), delivery.AcceptedFeedbackSha256);
            Xunit.Assert.Null(delivery.ObligationFeedbackSha256);
            Xunit.Assert.Null(delivery.ObligationCandidateSha);
            Xunit.Assert.Null(delivery.ObligationProjectionSha256);
        }, legacy: true);
    }

    private static void WithPrompt(Action<Goal, TaskSpec, WorkerContextPackage, string> check, bool legacy = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-obligation-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Apply correction", AgentRole.Developer);
            var goal = kernel.CreateGoal("Deliver candidate-bound correction", [task]);
            kernel.RetryTaskAutomatically(goal.Id, task.Id, "alpha.cs exceeds the ceiling", RetryCause.NewSourceFinding,
                authoritativeRetryFeedback: true,
                obligation: legacy ? null : RetryCorrectionObligation.ForSourceSizeRatchet(Candidate));
            var artifact = WorkerContextArtifact.Create(new LogicalArtifactIdentity("task/criterion-retry-feedback.json"),
                ContextArtifactKind.AcceptanceCriteria, JsonSerializer.SerializeToUtf8Bytes(task.CriterionRetryFeedback),
                [AgentRole.Developer], ContextDeliveryMode.InlineFull, ContextContractVersion.V1);
            var package = new WorkerContextPackage("ctxpkg-test", ContextContractVersion.V1, AgentRole.Developer, [artifact]);
            check(goal, task, package, Path.Combine(root, "prompt.md"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
