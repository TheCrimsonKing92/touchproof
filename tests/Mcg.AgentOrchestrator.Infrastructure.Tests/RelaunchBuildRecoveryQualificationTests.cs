using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static RelaunchBuildRecoveryWorkflowTests;

// Parallel-safe: each case owns its kernel, log directory and injected evaluation time.
public sealed class RelaunchBuildRecoveryQualificationTests
{
    [Theory]
    [InlineData("compiler")]
    [InlineData("missing-token")]
    [InlineData("no-handoff")]
    [InlineData("at-start")]
    [InlineData("before-start")]
    [InlineData("future")]
    public void NonQualifyingFailureKeepsExistingHoldExactlyAndNeverRequests(string variant)
    {
        var f = FailedFixture();
        var snapshot = f.Kernel.ExportSnapshot();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Tasks = goal.Tasks.Select(task => task with
                {
                    CompletionBuildEvidence = task.CompletionBuildEvidence! with
                    {
                        Phase = variant == "compiler" ? CompletionBuildEvidencePhase.CompilerFailure : CompletionBuildEvidencePhase.ApparatusFailure,
                        Diagnostic = variant == "missing-token" ? "different apparatus failure" : CompletionBuildRelaunchRecoveryQualification.ChannelUnavailable
                    }
                }).ToArray()
            }).ToArray()
        }, f.Clock);
        var start = f.Task.LastDispatch!.DispatchedAt;
        IReadOnlyList<DateTimeOffset> handoffs = variant switch
        {
            "no-handoff" => [], "at-start" => [start], "before-start" => [start.AddSeconds(-1)],
            "future" => [f.Clock.UtcNow.AddSeconds(1)], _ => [start.AddSeconds(1)]
        };
        var requests = 0;
        var context = new CompletionBuildRelaunchRecoveryQualification.Context(() => handoffs, () => f.Clock.UtcNow,
            (_, _, _) => { requests++; throw new InvalidOperationException("must not request"); });
        var expected = Decide(f);

        Assert.False(CompletionBuildRelaunchRecoveryQualification.Qualifies(f.Task, handoffs, f.Clock.UtcNow));
        var actual = Decide(f, context);
        // Compare every value, including Decision.Facts, rather than collection references.
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(0, requests);
        if (variant == "compiler") Assert.Null(actual);
        else Assert.Equal(ConductorHoldOwner.Operator, Assert.IsType<ConductorAdvanceOutcome.Held>(actual).Owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TokenInEitherWorkerOrBuildDiagnosticQualifies(bool workerDiagnostic)
    {
        var f = FailedFixture();
        if (workerDiagnostic)
        {
            var snapshot = f.Kernel.ExportSnapshot();
            f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with
                {
                    Tasks = goal.Tasks.Select(task => task with
                    {
                        CompletionBuildEvidence = task.CompletionBuildEvidence! with { Diagnostic = "apparatus failed" },
                        LastVerification = task.LastVerification! with
                            { StandardError = CompletionBuildRelaunchRecoveryQualification.ChannelUnavailable },
                        VerificationHistory = []
                    }).ToArray()
                }).ToArray()
            }, f.Clock);
        }
        Assert.True(CompletionBuildRelaunchRecoveryQualification.Qualifies(f.Task,
            [f.Task.LastDispatch!.DispatchedAt.AddSeconds(1)], f.Clock.UtcNow));
    }

    [Fact]
    public void FailedRequestRetainsExistingOperatorHold()
    {
        var f = FailedFixture();
        var expected = Decide(f);
        var requests = 0;
        var context = Context(f, _ => { requests++; throw new IOException("injected persistence failure"); });
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(Decide(f, context)));
        Assert.Equal(1, requests);
        Assert.Null(f.Task.CompletionBuildEvidence!.RecoversOperationId);
    }

    [Fact]
    public void LedgerUsesActualHandoffIncludingRotationAndFailsClosedForMissingLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "relaunch-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "conduct-events.jsonl");
            var at = DateTimeOffset.Parse("2026-11-09T01:27:00Z");
            string Row(string detail) => JsonSerializer.Serialize(new { timestamp = at, detail });
            File.WriteAllLines(path,
                [Row("LOOP_RELAUNCH_SCHEDULED reason=upgrade"), Row("LOOP_RELAUNCH_DRAIN admitting=false"), "{malformed"]);
            var ledger = new ConductorRelaunchHandoffLedger(path);
            Assert.Empty(ledger.Read());
            File.WriteAllLines(Path.Combine(root, "conduct-events-001.jsonl"),
                [Row("LOOP_HANDOFF attempt=1 stagedSourceCommit=abc repositoryHead=abc runDir=owned")]);
            Assert.Equal(at, Assert.Single(ledger.Read()));
            File.Delete(Path.Combine(root, "conduct-events-001.jsonl"));
            File.Delete(path);
            Assert.Empty(ledger.Read());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RecoveryIdentityRejectsUnrelatedCandidatesAndSecondRecoveries()
    {
        var f = FailedFixture();
        var original = f.Task.CompletionBuildEvidence!;
        var recovery = f.Coordinator().RequestRelaunchRecovery(f.Kernel, f.GoalId, f.TaskId,
            CompletionBuildEvidenceTarget.From(original));
        Assert.True(TaskSpec.IsRelaunchRecoveryOf(original, recovery));
        Assert.False(TaskSpec.IsRelaunchRecoveryOf(original, recovery with { RecoversOperationId = null }));
        Assert.False(TaskSpec.IsRelaunchRecoveryOf(original, recovery with { TreeDigest = "different" }));
        Assert.False(TaskSpec.IsRelaunchRecoveryOf(original, recovery with { DispatchAt = original.DispatchAt.AddSeconds(1) }));
        Assert.False(TaskSpec.IsRelaunchRecoveryOf(original with { Phase = CompletionBuildEvidencePhase.CompilerFailure }, recovery));
        Assert.False(TaskSpec.IsRelaunchRecoveryOf(recovery with
            { Phase = CompletionBuildEvidencePhase.ApparatusFailure, Applied = true, CustodyReleased = true },
            recovery with { Id = Guid.NewGuid().ToString("N"), RecoversOperationId = recovery.Id }));
    }
}
