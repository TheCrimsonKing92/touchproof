using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class PreReviewEvidenceRelaunchProvenanceLaunchPathTests
{
    [Theory]
    [InlineData("reconciled-fallthrough", false, false, "Failed", "test-failure")]
    [InlineData("reconciled-fallthrough", true, false, "Failed", "test-failure")]
    [InlineData("regate-supersession", false, false, "StaleCandidate", "did-not-run")]
    [InlineData("terminal-without-run-replaced", true, false, "Cancelled", "did-not-run")]
    [InlineData("stale-replaced", true, false, "Failed", "test-failure")]
    [InlineData("stale-replaced", false, true, "Failed", "test-failure")]
    [InlineData("did-not-run-retry", false, false, "Cancelled", "did-not-run")]
    public void RelaunchPath_StoresCausalPredecessor(
        string trigger, bool newHead, bool newRequest, string outcome, string resultClass)
    {
        using var harness = new Harness();
        var first = harness.Start();
        Assert.Null(first.RelaunchProvenance);
        harness.Complete(first, cancelled: trigger is "terminal-without-run-replaced" or "did-not-run-retry");
        if (trigger == "regate-supersession")
            Assert.True(harness.Coordinator.InvalidateCurrent(first.GoalId, "operator retry"));
        else if (trigger is "reconciled-fallthrough" or "did-not-run-retry")
            harness.Coordinator.MarkReconciled(harness.Latest);

        var predecessor = harness.Latest;
        Assert.Equal(outcome, predecessor.Outcome.ToString());
        var candidate = newHead
            ? ConductorParallelAcceptanceCandidate.Create(harness.Goal, 0, [], "branch-2", "main-1")
            : harness.Candidate;
        var next = harness.Start(candidate, newRequest ? "different tests" : Harness.Request);
        Assert.NotEqual(first.AttemptId, next.AttemptId);
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(next.MetadataPath)!, "*.attempt.json").Length);
        using var stored = JsonDocument.Parse(File.ReadAllText(next.MetadataPath));
        var provenance = stored.RootElement.GetProperty("relaunchProvenance");
        Assert.Equal(trigger, provenance.GetProperty("trigger").GetString());
        Assert.Equal(first.AttemptId, provenance.GetProperty("priorAttemptId").GetString());
        Assert.Equal((int)predecessor.Outcome, provenance.GetProperty("priorAttemptOutcome").GetInt32());
        Assert.Equal("branch-1", provenance.GetProperty("priorBranchHeadSha").GetString());
        Assert.Equal(resultClass, provenance.GetProperty("priorResultClass").GetString());
        Assert.Equal(!newHead, provenance.GetProperty("sameBranchHead").GetBoolean());
        Assert.Equal(!newRequest, provenance.GetProperty("sameFocusedEvidenceRequest").GetBoolean());
        Assert.Equal(next.RelaunchProvenance, harness.Latest.RelaunchProvenance);
    }

    [Theory]
    [InlineData("apparatus", "apparatus-failure")]
    [InlineData("timeout", "timeout")]
    [InlineData("test", "test-failure")]
    [InlineData("timeout-with-identity", "test-failure")]
    [InlineData("apparatus-with-identity", "apparatus-failure")]
    [InlineData("mixed-timeout", "other")]
    [InlineData("empty", "other")]
    [InlineData("unaccepted", "other")]
    [InlineData("passed", "passed")]
    public void StoredResult_RelaunchUsesEvidenceStagePrecedence(string scenario, string expected)
    {
        using var harness = new Harness();
        var first = harness.Start();
        harness.Complete(first, evidence: Evidence(scenario));
        harness.Coordinator.MarkReconciled(harness.Latest);
        var next = harness.Start();
        using var stored = JsonDocument.Parse(File.ReadAllText(next.MetadataPath));
        Assert.Equal(expected, stored.RootElement.GetProperty("relaunchProvenance")
            .GetProperty("priorResultClass").GetString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("non-focused")]
    public void UnreadablePriorResult_RelaunchStoresOther(string artifact)
    {
        using var harness = new Harness();
        var first = harness.Start();
        harness.Complete(first);
        harness.Coordinator.MarkReconciled(harness.Latest);
        if (artifact == "missing")
            File.Delete(first.ResultPath);
        else
            File.WriteAllText(first.ResultPath, artifact == "malformed" ? "{" : "{\"kind\":\"acceptance\"}");
        var next = harness.Start();
        Assert.Equal(PreReviewEvidencePriorResultClass.Other, next.RelaunchProvenance!.PriorResultClass);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestContext_RelaunchUsesExistingSelectionEquality(bool changePaths)
    {
        using var harness = new Harness();
        var context = new ConductorFocusedEvidenceRequestContext("round-1", "batch-1", [], RevertPaths: ["src/one.cs"]);
        var first = harness.Start(context: context);
        harness.Complete(first);
        harness.Coordinator.MarkReconciled(harness.Latest);
        var next = harness.Start(context: context with
        {
            FindingRoundFingerprint = "round-2",
            RevertPaths = changePaths ? ["src/two.cs"] : ["src/one.cs"]
        });
        Assert.True(next.RelaunchProvenance!.SameBranchHead);
        Assert.Equal(!changePaths, next.RelaunchProvenance.SameFocusedEvidenceRequest);
    }

    [Fact]
    public void PassedWithoutResult_RelaunchPreservesStoredPassedClass()
    {
        using var harness = new Harness();
        var first = harness.Start();
        harness.Complete(first, evidence: Evidence("passed"));
        harness.Coordinator.MarkReconciled(harness.Latest);
        File.Delete(first.ResultPath);
        var next = harness.Start();
        Assert.Equal(PreReviewEvidencePriorResultClass.Passed, next.RelaunchProvenance!.PriorResultClass);
    }

    [Fact]
    public void Relaunch_AppendsOneEventWithRequiredFields()
    {
        using var harness = new Harness();
        var first = harness.Start();
        Assert.Empty(harness.ProvenanceEvents());
        harness.Complete(first);
        harness.Coordinator.MarkReconciled(harness.Latest);
        var next = harness.Start();
        var line = Assert.Single(harness.ProvenanceEvents());
        Assert.Equal(first.GoalId, line.GetProperty("goalId").GetString());
        Assert.Equal($"evidence:{next.AttemptId}:relaunch-provenance", line.GetProperty("event_id").GetString());
        Assert.Equal($"EVIDENCE_RELAUNCH_PROVENANCE goal={first.GoalId} attempt={next.AttemptId} " +
            $"prior_attempt={first.AttemptId} trigger=reconciled-fallthrough same_head=true prior_result_class=test-failure",
            line.GetProperty("detail").GetString());
        var observed = harness.Start();
        Assert.Equal(next.AttemptId, observed.AttemptId);
        Assert.Single(harness.ProvenanceEvents());
    }

    [Fact]
    public void GateRelaunch_StoresNoProvenanceOrEvent()
    {
        using var harness = new Harness();
        var first = harness.Coordinator.Evaluate(harness.Candidate, ConductorAutonomyPolicy.Permissive,
            (candidate, _, _, _, _) => ConductorParallelAcceptanceRunResult.Fault(candidate, new InvalidOperationException("failed")));
        harness.Coordinator.RunAttemptForTests(first.Attempt, harness.Candidate, ConductorAutonomyPolicy.Permissive,
            (candidate, _, _, _, _) => ConductorParallelAcceptanceRunResult.Fault(candidate, new InvalidOperationException("failed")));
        harness.Coordinator.MarkReconciled(harness.Latest);
        var next = harness.Coordinator.Evaluate(harness.Candidate, ConductorAutonomyPolicy.Permissive,
            (candidate, _, _, _, _) => ConductorParallelAcceptanceRunResult.Fault(candidate, new InvalidOperationException("failed")));
        Assert.NotEqual(first.Attempt.AttemptId, next.Attempt.AttemptId);
        using var stored = JsonDocument.Parse(File.ReadAllText(next.Attempt.MetadataPath));
        Assert.False(stored.RootElement.TryGetProperty("relaunchProvenance", out _));
        Assert.Null(harness.Latest.RelaunchProvenance);
        Assert.Empty(harness.ProvenanceEvents());
    }

    private static FocusedEvidenceRunResult Evidence(string scenario)
    {
        var identity = scenario is "test" or "timeout-with-identity" or "apparatus-with-identity";
        var timeout = scenario is "timeout" or "timeout-with-identity" or "mixed-timeout";
        AcceptanceCheckResult[] checks = scenario == "empty" ? [] :
            [new(timeout ? "acceptance-check-timeout:focused" : "focused-tests", scenario == "passed", 1, "result",
                FailingTestIdentities: identity ? ["ExampleTests.Fails"] : null)];
        if (scenario == "mixed-timeout")
            checks = [.. checks, new("other-failure", false, 1, "failed")];
        return new(Harness.Request, scenario != "unaccepted", scenario == "passed", "evidence", checks,
            OutcomeReason: scenario.StartsWith("apparatus", StringComparison.Ordinal)
                ? FindingEvidenceOutcomeReason.ApparatusFailure : null);
    }

    internal sealed class Harness : IDisposable
    {
        internal const string Request = "focused tests";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "relaunch-provenance-" + Guid.NewGuid().ToString("N"));
        private readonly string _logPath;
        internal Goal Goal { get; }
        internal ConductorParallelAcceptanceCandidate Candidate { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal ConductorParallelAcceptanceAttempt Latest => Coordinator.TryReadLatest(Goal.Id.Value)!;

        internal Harness()
        {
            Directory.CreateDirectory(_root);
            _logPath = Path.Combine(_root, "conduct.jsonl");
            Goal = new AgentOrchestratorKernel().CreateGoal("Record evidence relaunch provenance");
            Candidate = ConductorParallelAcceptanceCandidate.Create(Goal, 0, [], "branch-1", "main-1");
            Coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(_root, "attempts"), TrunkBranchName.Default,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7111),
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(_logPath));
        }

        internal ConductorParallelAcceptanceAttempt Start(
            ConductorParallelAcceptanceCandidate? candidate = null, string request = Request,
            ConductorFocusedEvidenceRequestContext? context = null) =>
            Coordinator.EvaluateFocusedEvidence(candidate ?? Candidate, ConductorAutonomyPolicy.Permissive,
                request, (_, value, _, _) => new(value, true, true, "unused", []), context).Attempt;

        internal void Complete(ConductorParallelAcceptanceAttempt attempt, bool cancelled = false,
            FocusedEvidenceRunResult? evidence = null) =>
            Coordinator.RunAttemptForTests(attempt, Candidate, ConductorAutonomyPolicy.Permissive,
                (candidate, _, _, _, _) => cancelled
                    ? ConductorParallelAcceptanceRunResult.Fault(candidate, new OperationCanceledException("cancelled"))
                    : ConductorParallelAcceptanceRunResult.Focused(candidate, evidence ?? Evidence("test")));

        internal JsonElement[] ProvenanceEvents()
        {
            if (!File.Exists(_logPath))
                return [];

            return File.ReadLines(_logPath)
                .Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                })
                .Where(item => item.GetProperty("eventKind").GetString() == "evidence-relaunch-provenance").ToArray();
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
