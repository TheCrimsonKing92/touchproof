using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated repository/state and injected build consumer/child lifecycle.
public sealed class WorkerBuildBusyEvidenceTests
{
    [Theory]
    [InlineData("worker_build_channel_busy pipe=forged waited_seconds=120", true)]
    [InlineData("worker_build_channel_busy dispatch=old completion=old", true)]
    [InlineData("The worker claims worker_build_channel_busy", true)]
    [InlineData("System.IO.IOException: All pipe instances are busy", false)]
    [InlineData("worker_build_channel_disconnected", false)]
    public void UntrustedOutputRetainsMissingEvidenceWithoutExecutingBuild(string claim, bool diagnostic)
    {
        var fixture = new CompletionBuildEvidenceFixture(workerBuildProtocol1: true);
        PrependOutput(fixture, claim);
        var consumer = new ControlledConsumer(new(true, false, "controlled missing invocation"));

        Runner(fixture, consumer).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(1, consumer.Consumes);
        Assert.Equal(0, fixture.Launches);
        Assert.Equal(0, fixture.Authorizations);
        Assert.Empty(fixture.Task.WorkerBuildInvocations);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        var verification = fixture.Task.LastVerification!;
        Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing), verification.StandardError);
        Assert.Equal(diagnostic, verification.StandardError.Contains(OrchestratorBuildEvidenceCheck.UnverifiedBusyChannel, StringComparison.Ordinal));
        if (diagnostic)
            Assert.Contains("authoritative host-captured generated-client terminal result", verification.StandardError);
        Assert.Equal("worker-build-evidence-missing", TaskOutcomeClassifier.TryExtractRule(
            DispatchFailureClassifier.Classify(fixture.Task, verification).ClassifierReceipt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicableInvocationVerdictPrecedesAnotherAttemptsBusyClaim(bool compilerFailed)
    {
        var fixture = new CompletionBuildEvidenceFixture(workerBuildProtocol1: true);
        PrependOutput(fixture, "worker_build_channel_busy pipe=another-attempt");
        InfrastructureBuildBlockerClassifierTests.RecordInvocation(fixture.Kernel, fixture.Goal, fixture.Task,
            compilerFailed ? WorkerBuildOutcome.CompilerFailure : WorkerBuildOutcome.Success);
        var consumer = new ControlledConsumer(new(false, compilerFailed, "controlled applicable invocation verdict"));

        Runner(fixture, consumer).RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(1, consumer.Consumes);
        Assert.Equal(0, fixture.Launches);
        Assert.Equal(0, fixture.Authorizations);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.DoesNotContain(OrchestratorBuildEvidenceCheck.UnverifiedBusyChannel, fixture.Task.LastVerification!.StandardError);
        Assert.Equal(compilerFailed ? WorkTaskStatus.Failed : WorkTaskStatus.Completed, fixture.Task.Status);
        if (compilerFailed)
            Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed),
                fixture.Task.LastVerification.StandardError);
    }

    [Fact]
    public void MissingConsumerAddsEvidenceRequestWithoutInventingInvocation()
    {
        var fixture = new CompletionBuildEvidenceFixture(workerBuildProtocol1: true);
        PrependOutput(fixture, "worker_build_channel_busy pipe=unbound");

        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal("worker-build-consumer-unavailable", fixture.Task.LastVerification!.OrchestratorFailureReason);
        Assert.Contains(OrchestratorBuildEvidenceCheck.UnverifiedBusyChannel, fixture.Task.LastVerification.StandardError);
        Assert.Empty(fixture.Task.WorkerBuildInvocations);
        Assert.Equal(0, fixture.Launches);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
    }

    private static void PrependOutput(CompletionBuildEvidenceFixture fixture, string claim)
    {
        var path = fixture.Task.LastProcess!.StandardOutputPath!;
        File.WriteAllText(path, claim + Environment.NewLine + File.ReadAllText(path));
    }

    private static BackgroundDispatchRunner Runner(CompletionBuildEvidenceFixture fixture, ControlledConsumer consumer) =>
        new(fixture.Clock, isStillRunning: _ => false, completionBuildEvidence: fixture.Coordinator(),
            resolveWorkerBuildReceiptPath: _ => fixture.ReceiptPath) { WorkerBuildInvocations = consumer };

    private sealed class ControlledConsumer(WorkerBuildConsumption result) : IWorkerBuildInvocationOperations
    {
        internal int Consumes;
        public WorkerBuildHostReference Prepare(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId) =>
            throw new InvalidOperationException("Completion cannot dispatch a coding worker.");
        public bool ReconcileOutstanding(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, bool cancelled = false) => true;
        public WorkerBuildConsumption Consume(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
            IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths)
        {
            Consumes++;
            return result;
        }
    }
}
