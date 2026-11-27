using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RejectedPlannerLegacyStdoutRecoveryTests
{
    private const string LegacyUnavailableReason = "legacy-snapshot-authoritative-output-unavailable";
    private const string RejectedPlannerStdoutIdentity = "task/rejected-planner/stdout";

    [Fact]
    public void Legacy_snapshot_with_log_over_bound_fails_preparation_with_legacy_reason()
    {
        var root = CreateTempDirectory();
        try
        {
            var (goal, task, brief, _) = ArrangeRejectedPlannerRetry(root, VerificationTextBounds.BoundThreshold + 1);

            var error = Assert.Throws<WorkerContextPreparationException>(() =>
                WorkerProfileDispatcher.BuildContextPackage(goal, task, root, root, brief));

            Assert.Equal(LegacyUnavailableReason, error.Reason);
            Assert.Equal(RejectedPlannerStdoutIdentity, error.Identity.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Legacy_snapshot_with_matching_log_at_bound_delivers_full_log_as_mandatory_artifact()
    {
        var root = CreateTempDirectory();
        try
        {
            var (goal, task, brief, logPath) = ArrangeRejectedPlannerRetry(root, VerificationTextBounds.BoundThreshold);

            var package = WorkerProfileDispatcher.BuildContextPackage(goal, task, root, root, brief);

            var artifact = Assert.Single(package.Artifacts, item => item.Identity.Value == RejectedPlannerStdoutIdentity);
            Assert.Equal(ContextDeliveryMode.MandatoryFile, artifact.DeliveryMode);
            Assert.NotNull(artifact.MandatoryRelativePath);
            var expectedBytes = File.ReadAllBytes(logPath);
            Assert.Equal(expectedBytes, File.ReadAllBytes(Path.Combine(root, artifact.MandatoryRelativePath)));
            Assert.Equal(WorkerContextArtifact.Hash(expectedBytes), artifact.ContentHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (Goal Goal, TaskSpec Task, TaskBriefSource Brief, string LogPath) ArrangeRejectedPlannerRetry(
        string root, int logLength)
    {
        const string head = "Rejected plan\n";
        const string tail = "\nEnd of plan\n";
        var log = head + new string('x', logLength - head.Length - tail.Length) + tail;
        var logPath = Path.Combine(root, "planner.out.log");
        File.WriteAllText(logPath, log);
        File.WriteAllText(Path.Combine(root, "artifact-registry.json"), """{"artifacts":[]}""");

        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Repair the rejected plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Deliver the entire rejected plan for repair", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var verification = new TaskVerificationRecord("worker", root, 1, log, "contract rejection",
            DateTimeOffset.UtcNow.AddMinutes(-1), StandardOutputPath: logPath,
            CompletionVerdictRule: "planner-output-contract-rejected",
            FullStandardOutputUnavailableReason: LegacyUnavailableReason, StandardOutputIsAuthoritative: false);
        // Preserve every character so the over-bound case can fail on length alone.
        kernel.RecordTaskVerification(goal.Id, task.Id, verification with { StandardOutput = log });
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, "Format repair", RetryCause.ContractClarification);

        var rejected = Assert.Single(task.VerificationHistory);
        Assert.Null(rejected.AuthoritativeStandardOutput);
        Assert.Equal(LegacyUnavailableReason, rejected.FullStandardOutputUnavailableReason);
        Assert.True(File.Exists(logPath));
        var onDisk = File.ReadAllText(logPath);
        Assert.Equal(logLength, onDisk.Length);
        Assert.Equal(onDisk, rejected.StandardOutput);

        var brief = TypedBriefSourceFixture.Create(goal, task, "Repair the rejected plan.");
        return (goal, task, brief, logPath);
    }
}
