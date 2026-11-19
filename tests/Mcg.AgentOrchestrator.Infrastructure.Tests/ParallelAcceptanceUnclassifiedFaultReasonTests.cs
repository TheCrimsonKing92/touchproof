using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ParallelAcceptanceUnclassifiedFaultReasonTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const string Scope = "src/Diagnostics.cs";
    private const string DiagnosticPrefix = "parallel acceptance fault: System.ArgumentException ";

    [Fact]
    public void DirectCompletionPreservesTypeAndFirstFrameBeforeLongMessage()
    {
        var fault = Record.Exception(() => { ThrowUnclassified(); });
        AssertLongReason(CompleteDirectly(Assert.IsType<ArgumentException>(fault)));
    }

    [Fact]
    public void ArtifactReplayPreservesTypeAndFirstFrameBeforeLongMessage()
    {
        var root = CreateTempDirectory("mcg-acceptance-fault-reason");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            CreateVerifiedSimpleGoal(kernel, $"Update {Scope}");
            var escalations = new List<string>();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => ThrowUnclassified(),
                getLandingFileScopes: _ => [Scope],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, TrunkBranchName.Default, runInline: true,
                    buildStorageRoot: new DotnetBuildStorageRoot(Path.Combine(root, "test-dotnet"))),
                writeEscalation: (_, _, reason) => escalations.Add(reason));

            var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1);

            Assert.Equal(1, summary.Escalated);
            AssertLongReason(Assert.Single(escalations));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void MissingStackUsesAnExplicitMarker() =>
        Assert.Equal(DiagnosticPrefix + "<unavailable> no_stack",
            CompleteDirectly(new ArgumentException("no stack")));

    private static string CompleteDirectly(Exception fault)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, $"Update {Scope}");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope], "branch", "main");
        var attempt = new ConductorParallelAcceptanceAttempt(
            "attempt", goal.Id.Value, goal.Id.Value[..8], 0, "branch", "main",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1,
            ConductorParallelAcceptanceAttemptOutcome.Failed, "stdout", "stderr", "exit", "heartbeat",
            "result", "metadata");
        var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(MakeDriver(),
            ConductorAutonomyPolicy.Conservative, ConductorParallelAcceptanceRunResult.Fault(candidate, fault),
            attempt, out _);
        return Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome).Reason;
    }

    private static void AssertLongReason(string reason)
    {
        Assert.StartsWith(
            DiagnosticPrefix + $"{nameof(ParallelAcceptanceUnclassifiedFaultReasonTests)}.{nameof(ThrowUnclassified)} long_message_",
            reason, StringComparison.Ordinal);
        Assert.Equal(512, reason.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AcceptanceVerificationSummary ThrowUnclassified() =>
        throw new ArgumentException("long message\r\n" + new string('x', 600));
}
