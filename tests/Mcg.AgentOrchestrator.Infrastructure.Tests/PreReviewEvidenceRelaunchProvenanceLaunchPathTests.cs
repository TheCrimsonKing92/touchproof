using static ConductorDriverTests;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class PreReviewEvidenceRelaunchProvenanceLaunchPathTests
{
    [Theory]
    [InlineData("pre-review-stage")]
    [InlineData("finding-round-1")]
    [InlineData("pre-tester-deferred")]
    public void FocusedCompletion_CapturesOriginalReceiptOnlyForOwningStage(string fingerprint)
    {
        using var harness = new RetainedHarness();
        harness.Context = harness.Context with
        {
            FindingRoundFingerprint = fingerprint,
            ReceiptId = "focused-receipt-1"
        };
        if (fingerprint == "pre-tester-deferred")
        {
            harness.Evidence = harness.Evidence with
            {
                Arms = [new FocusedEvidenceArmRunResult(FindingEvidenceArm.Candidate, "branch-1",
                    FindingEvidenceArmDisposition.Red, true, false, harness.Evidence.Summary, harness.Evidence.Checks)]
            };
        }
        var completed = harness.CompleteFirst();

        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, completed.Outcome);
        if (fingerprint == "finding-round-1")
        {
            Assert.Null(completed.OriginalFocusedReceipt);
            return;
        }

        var receipt = Assert.IsType<FindingEvidenceReceipt>(completed.OriginalFocusedReceipt);
        Assert.Equal("focused-receipt-1", receipt.ReceiptId);
        Assert.Equal(fingerprint, receipt.FindingRoundFingerprint);
        Assert.False(receipt.Passed);
        var arm = Assert.Single(receipt.Arms!);
        Assert.Equal(FindingEvidenceArmDisposition.Red, arm.Disposition);
        Assert.Contains(arm.ReceiptArtifacts!, artifact => artifact.Kind == "trx" &&
            artifact.Path == harness.TrxPath && artifact.Length > 0 && !string.IsNullOrWhiteSpace(artifact.Sha256));
        if (fingerprint == "pre-tester-deferred")
        {
            var disposition = Assert.Single(receipt.RequestDispositions!);
            Assert.Equal(["focused-receipt-1"], disposition.SourceReceiptIds);
        }
        else
        {
            Assert.Empty(receipt.RequestDispositions!);
        }
    }

    [Fact]
    public void ReconciledFailure_Reload_ReusesFailedResultWithoutExecution()
    {
        using var harness = new RetainedHarness();
        var original = harness.CompleteFirst();
        var metadata = File.ReadAllText(original.MetadataPath);
        var artifact = File.ReadAllText(original.ResultPath);
        var decision = harness.Reload().EvaluateFocusedEvidence(harness.Candidate,
            ConductorAutonomyPolicy.Permissive, RetainedHarness.Request, harness.Run, harness.Context);

        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
        Assert.Equal(original.AttemptId, decision.Attempt.AttemptId);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, decision.Attempt.Outcome);
        var arm = Assert.Single(decision.Attempt.OriginalFocusedReceipt!.Arms!);
        Assert.Equal(FindingEvidenceArmDisposition.Red, arm.Disposition);
        Assert.False(arm.Passed);
        Assert.Equal(["ExampleTests.Fails"], arm.FailingTestIdentities);
        var evidence = Assert.IsType<FocusedEvidenceRunResult>(decision.Run!.FocusedEvidence);
        Assert.True(evidence.Accepted);
        Assert.False(evidence.Passed);
        Assert.Equal(harness.Evidence.Summary, evidence.Summary);
        var check = Assert.Single(evidence.Checks);
        Assert.False(check.Passed);
        Assert.Equal(["ExampleTests.Fails"], check.FailingTestIdentities);
        Assert.Equal([harness.TrxPath], check.TestResultPaths);
        Assert.Equal(1, harness.Executions);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(original.MetadataPath)!, "*.attempt.json"));
        Assert.Equal(metadata, File.ReadAllText(original.MetadataPath));
        Assert.Equal(artifact, File.ReadAllText(original.ResultPath));
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("request")]
    [InlineData("negative-control")]
    [InlineData("revert-paths")]
    [InlineData("mutation")]
    [InlineData("repository")]
    [InlineData("round")]
    [InlineData("basis")]
    [InlineData("current-basis-missing")]
    [InlineData("recorded-basis-missing")]
    [InlineData("batch")]
    [InlineData("baseline-arm")]
    public void ReconciledFailure_OneChangedBinding_ExecutesAgain(string difference)
    {
        using var harness = new RetainedHarness();
        if (difference == "recorded-basis-missing")
            harness.Context = harness.Context with { ExecutionBasisIdentity = null };
        var first = harness.CompleteFirst();
        var context = harness.Context;
        var candidate = harness.Candidate;
        var request = RetainedHarness.Request;
        switch (difference)
        {
            case "candidate": candidate = ConductorParallelAcceptanceCandidate.Create(
                harness.Goal, 0, [], "branch-2", "main-1"); break;
            case "request": request = "Infrastructure.Tests: FullyQualifiedName~OtherTests"; break;
            case "negative-control": context = context with { NegativeControl = FindingEvidenceNegativeControl.RevertSrc }; break;
            case "revert-paths": context = context with { RevertPaths = ["src/one.cs"] }; break;
            case "mutation": context = context with { Mutation = new("src/one.cs", "before", "after") }; break;
            case "repository": context = context with { RepositoryTarget = new("other", harness.Root,
                harness.Root, "main", new AcceptanceGateEngineSettings(), "branch-1") }; break;
            case "round": context = context with { FindingRoundFingerprint = "round-2" }; break;
            case "basis": context = context with { ExecutionBasisIdentity = "basis-2" }; break;
            case "current-basis-missing": context = context with { ExecutionBasisIdentity = null }; break;
            case "recorded-basis-missing": context = context with { ExecutionBasisIdentity = "basis-1" }; break;
            case "batch": context = context with { BatchId = "batch-2" }; break;
            case "baseline-arm": context = context with { RunBaselineArm = true }; break;
            default: throw new ArgumentOutOfRangeException(nameof(difference));
        }
        harness.AssertExecutesAgain(first, candidate, request, context);
    }

    [Theory]
    [InlineData("apparatus")]
    [InlineData("zero-executed")]
    [InlineData("no-identities")]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    [InlineData("passed")]
    [InlineData("result-missing")]
    [InlineData("result-malformed")]
    [InlineData("result-fingerprint")]
    [InlineData("trx-missing")]
    [InlineData("trx-truncated")]
    [InlineData("trx-altered-same-length")]
    [InlineData("recorded-hash-missing")]
    [InlineData("retained-path-set")]
    [InlineData("executed-count-mismatch")]
    [InlineData("class-mismatch")]
    [InlineData("identity-mismatch")]
    [InlineData("no-trx-failures")]
    public void ReconciledFailure_OneIneligibleEvidenceCondition_ExecutesAgain(string difference)
    {
        using var harness = new RetainedHarness();
        var evidence = harness.Evidence;
        var check = Assert.Single(evidence.Checks);
        switch (difference)
        {
            case "apparatus": evidence = evidence with { OutcomeReason = FindingEvidenceOutcomeReason.ApparatusFailure }; break;
            case "zero-executed": evidence = evidence with { Checks = [check with { ExecutedTestCount = 0 }] }; break;
            case "no-identities": evidence = evidence with { Checks = [check with { FailingTestIdentities = [] }] }; break;
            case "timeout": evidence = evidence with { Checks = [check with { Name = "acceptance-check-timeout:focused" }] }; break;
            case "passed": evidence = evidence with { Passed = true, Checks = [check with { Passed = true }] }; break;
            case "executed-count-mismatch": evidence = evidence with { Checks = [check with { ExecutedTestCount = 2 }] }; break;
            case "identity-mismatch": evidence = evidence with { Checks = [check with { FailingTestIdentities = ["ExampleTests.Other"] }] }; break;
            case "class-mismatch":
                evidence = FailedTrxEvidence(RetainedHarness.Request, harness.TrxPath, "OtherTests"); break;
            case "no-trx-failures":
                var document = XDocument.Load(harness.TrxPath);
                var ns = document.Root!.Name.Namespace;
                document.Root.Element(ns + "Results")!.Element(ns + "UnitTestResult")!.SetAttributeValue("outcome", "Passed");
                var summary = document.Root.Element(ns + "ResultSummary")!;
                summary.SetAttributeValue("outcome", "Passed");
                summary.Element(ns + "Counters")!.SetAttributeValue("passed", 1);
                summary.Element(ns + "Counters")!.SetAttributeValue("failed", 0);
                document.Save(harness.TrxPath); break;
        }
        harness.Evidence = evidence;
        var first = harness.CompleteFirst(cancelled: difference == "cancelled");
        switch (difference)
        {
            case "result-missing": File.Delete(first.ResultPath); break;
            case "result-malformed": File.WriteAllText(first.ResultPath, "{"); break;
            case "result-fingerprint":
                var result = JsonNode.Parse(File.ReadAllText(first.ResultPath))!;
                result["focusedEvidence"]!["summary"] = "altered retained result";
                File.WriteAllText(first.ResultPath, result.ToJsonString()); break;
            case "trx-missing": File.Delete(harness.TrxPath); break;
            case "trx-truncated": File.WriteAllText(harness.TrxPath, "<TestRun"); break;
            case "trx-altered-same-length":
                var before = File.ReadAllText(harness.TrxPath);
                var after = before.Replace("Fails", "Other", StringComparison.Ordinal);
                Assert.NotEqual(before, after);
                Assert.Equal(before.Length, after.Length);
                File.WriteAllText(harness.TrxPath, after); break;
            case "recorded-hash-missing":
                var metadata = JsonNode.Parse(File.ReadAllText(first.MetadataPath))!;
                metadata["originalFocusedReceipt"]!["arms"]![0]!["receiptArtifacts"]![0]!["sha256"] = "";
                File.WriteAllText(first.MetadataPath, metadata.ToJsonString()); break;
            case "retained-path-set":
                var paths = JsonNode.Parse(File.ReadAllText(first.MetadataPath))!;
                paths["testResultPaths"] = new JsonArray(Path.Combine(harness.Root, "different.trx"));
                File.WriteAllText(first.MetadataPath, paths.ToJsonString()); break;
        }
        harness.AssertExecutesAgain(first, harness.Candidate, RetainedHarness.Request, harness.Context);
    }

    [Fact]
    public void ReconciledFailure_WriterLeaseBusy_ThrowsWithoutChangingAttempt()
    {
        using var harness = new RetainedHarness();
        var first = harness.CompleteFirst();
        var before = File.ReadAllText(first.MetadataPath);
        var coordinator = harness.Reload(writerBusy: true);
        Assert.Throws<AcceptanceArtifactWriterLeaseBusyException>(() => coordinator.EvaluateFocusedEvidence(
            harness.Candidate, ConductorAutonomyPolicy.Permissive,
            RetainedHarness.Request, harness.Run, harness.Context));
        Assert.Equal(1, harness.Executions);
        Assert.Equal(before, File.ReadAllText(first.MetadataPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first.MetadataPath)!, "*.attempt.json"));
    }

    internal static class DriverRestartScenarios
    {
        private static string CreateTempDirectory() => ConductorDriverTests.CreateTempDirectory();

        internal static void AssertRejectedCheckpointRecovery(bool receiptPersisted)
        {
            var root = CreateTempDirectory();
            try
            {
                var (kernel, goal) = SoftwareGoal();
                var reviewerId = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer).Id;
                var developerId = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Id;
                foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewerId))
                    PassVerification(kernel, goal, task);
                var authoritative = kernel.ExportGoalSnapshot(goal.Id);
                var context = FocusedPreReviewContext("retained-red-sha");
                var trx = Path.Combine(root, "failed.trx");
                var failed = PreReviewEvidenceRelaunchProvenanceLaunchPathTests.FailedTrxEvidence(
                    context.FocusedRequest!, trx, "ConductorDriverTests");
                var settings = new AcceptanceGateEngineSettings();
                var executions = 0;
                var checkpointRejections = 0;
                var dispatches = 0;
                var completion = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
                var attemptRoot = Path.Combine(root, "attempts");
                var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot,
                    TrunkBranchName.Default, isProcessAlive: _ => false,
                    attemptCompletionGateForTests: completion, acquireStableSlotLease: (_, _) => null);
                FocusedEvidenceRunResult Run(Goal _, string request)
                {
                    executions++;
                    return failed with { Request = request };
                }
                var startingDriver = MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getPreReviewEvidenceContext: _ => context,
                    getFindingEvidenceEngineSettings: _ => settings,
                    runFocusedEvidence: Run,
                    focusedEvidenceAttemptCoordinator: coordinator,
                    recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    {
                        kernel.RecordPreReviewEvidence(goalId, taskId, receipt);
                        // Model both recovery windows: neither effect persisted, or only Red persisted.
                        if (receiptPersisted) authoritative = kernel.ExportGoalSnapshot(goalId);
                    },
                    retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                        kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
                    dispatchAndStart: _ =>
                    {
                        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, developerId).Status);
                        Assert.NotNull(coordinator.TryReadLatest(goal.Id.Value)!.ReconciledAt);
                        checkpointRejections++;
                        kernel.ReplaceGoalWithSnapshot(authoritative);
                        return DispatchStartOutcome.Deferred(
                            "Critical dispatch checkpoint rejected stale state; worker start was aborted.");
                    });
                startingDriver._conductorTickKernel = kernel;

                Assert.IsType<ConductorAdvanceOutcome.Held>(startingDriver.AdvanceOnce(
                    goal, ConductorAutonomyPolicy.Permissive).Outcome);
                completion.RequiredHandleForTests(goal.Id.Value).CompleteForTests();
                Assert.Equal(1, executions);
                var first = coordinator.TryReadLatest(goal.Id.Value)!;
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, first.Outcome);
                Assert.Null(first.ReconciledAt);
                var rejected = startingDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
                Assert.Contains("Critical dispatch checkpoint rejected", Assert.IsType<ConductorAdvanceOutcome.Held>(rejected.Outcome).Reason,
                    StringComparison.Ordinal);
                Assert.Equal(1, checkpointRejections);
                Assert.Equal(WorkTaskStatus.Completed, kernel.GetTask(goal.Id, developerId).Status);
                Assert.Equal(receiptPersisted ? 1 : 0, kernel.GetTask(goal.Id, reviewerId).PreReviewEvidenceHistory.Count);
                Assert.DoesNotContain(kernel.Goals.Single().Timeline,
                    item => item.Kind == ProgressKind.TaskRetried && item.TaskId == developerId);
                var reconciled = coordinator.TryReadLatest(goal.Id.Value)!;
                Assert.NotNull(reconciled.ReconciledAt);

                // Inline completion makes the negative control execute twice instead of merely queuing a rerun.
                var restarted = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot,
                    TrunkBranchName.Default, isProcessAlive: _ => false, runInline: true,
                    acquireStableSlotLease: (_, _) => null);
                var replayRecordCalls = 0;
                var replayDriver = MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getPreReviewEvidenceContext: _ => context,
                    getFindingEvidenceEngineSettings: _ => settings,
                    runFocusedEvidence: Run,
                    focusedEvidenceAttemptCoordinator: restarted,
                    recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    {
                        replayRecordCalls++;
                        kernel.RecordPreReviewEvidence(goalId, taskId, receipt);
                    },
                    retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                        kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
                    dispatchAndStart: _ =>
                    {
                        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, developerId).Status);
                        dispatches++;
                        return DispatchStartOutcome.Started();
                    });
                replayDriver._conductorTickKernel = kernel;
                var recovered = replayDriver.AdvanceOnce(kernel.Goals.Single(), ConductorAutonomyPolicy.Permissive);

                Assert.IsType<ConductorAdvanceOutcome.Executed>(recovered.Outcome);
                Assert.Equal(1, executions);
                Assert.Equal(1, dispatches);
                Assert.Equal(receiptPersisted ? 0 : 1, replayRecordCalls);
                var current = kernel.Goals.Single();
                var receipt = Assert.Single(current.Tasks.Single(task => task.Id == reviewerId).PreReviewEvidenceHistory);
                Assert.Equal(PreReviewEvidenceDisposition.Red, receipt.Disposition);
                Assert.Equal(["ConductorDriverTests.Fails"], receipt.FailingTestIdentities);
                Assert.Equal(0, receipt.PassedCheckCount);
                Assert.Equal(1, receipt.FailedCheckCount);
                Assert.Contains(trx, receipt.EvidencePointer!, StringComparison.Ordinal);
                Assert.Equal([trx], Assert.Single(receipt.Checks).TestResultPaths);
                Assert.Single(current.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried && item.TaskId == developerId));
                Assert.Single(current.Timeline.Where(item => item.Kind == ProgressKind.PreReviewEvidenceRecorded && item.TaskId == reviewerId));
                var retained = restarted.TryReadLatest(goal.Id.Value)!;
                Assert.Equal(first.AttemptId, retained.AttemptId);
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, retained.Outcome);
                Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first.MetadataPath)!, "*.attempt.json"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        internal static void AssertInFlightCompletionAfterRestart()
        {
            var root = CreateTempDirectory();
            try
            {
                var (kernel, goal) = SoftwareGoal();
                var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
                foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
                {
                    PassVerification(kernel, goal, task);
                }

                var completionGate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
                var attemptRoot = Path.Combine(root, "pre-review-evidence-attempts");
                var startingCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                    isProcessAlive: _ => true,
                    attemptCompletionGateForTests: completionGate,
                    acquireStableSlotLease: (_, _) => null);
                var focusedRuns = 0;
                var dispatches = 0;
                FocusedEvidenceRunResult RunEvidence(Goal _, string request)
                {
                    focusedRuns++;
                    return PassingPreReviewEvidence(request);
                }

                var startingDriver = MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getPreReviewEvidenceContext: _ => FocusedPreReviewContext("async-sha"),
                    runFocusedEvidence: RunEvidence,
                    recordPreReviewEvidence: (goalId, taskId, receipt) =>
                        kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                    dispatchAndStart: _ =>
                    {
                        dispatches++;
                        return DispatchStartOutcome.Started();
                    },
                    focusedEvidenceAttemptCoordinator: startingCoordinator);

                var scheduled = startingDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

                var held = Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
                var heldAttempt = completionGate.RequiredHandleForTests(goal.Id.Value);
                Assert.Equal($"pre-review-evidence:{heldAttempt.Attempt.AttemptId}", held.StableIdentity);
                Assert.Equal(0, focusedRuns);
                Assert.Equal(0, dispatches);
                Assert.Equal(1, completionGate.HeldCount);

                heldAttempt.CompleteForTests();
                Assert.Equal(1, focusedRuns);

                var restartedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                    isProcessAlive: _ => false,
                    launchOwnedProcess: _ => throw new InvalidOperationException("completed attempt must be reconciled"),
                    acquireStableSlotLease: (_, _) => null);
                var restartedDriver = MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getPreReviewEvidenceContext: _ => FocusedPreReviewContext("async-sha"),
                    runFocusedEvidence: RunEvidence,
                    recordPreReviewEvidence: (goalId, taskId, receipt) =>
                        kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                    dispatchAndStart: _ =>
                    {
                        dispatches++;
                        return DispatchStartOutcome.Started();
                    },
                    focusedEvidenceAttemptCoordinator: restartedCoordinator);

                var reconciled = restartedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

                Assert.IsType<ConductorAdvanceOutcome.Executed>(reconciled.Outcome);
                Assert.Equal(1, focusedRuns);
                Assert.Equal(1, dispatches);
                Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    internal static FocusedEvidenceRunResult FailedTrxEvidence(
        string request, string path, string testClass = "ExampleTests")
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "TestDefinitions", new XElement(ns + "UnitTest", new XAttribute("id", "test-1"),
                new XAttribute("name", $"{testClass}.Fails"), new XElement(ns + "TestMethod",
                    new XAttribute("className", testClass), new XAttribute("name", "Fails")))),
            new XElement(ns + "Results", new XElement(ns + "UnitTestResult", new XAttribute("testId", "test-1"),
                new XAttribute("testName", $"{testClass}.Fails"), new XAttribute("outcome", "Failed"))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Failed"),
                new XElement(ns + "Counters", new XAttribute("total", 1), new XAttribute("executed", 1),
                    new XAttribute("passed", 0), new XAttribute("failed", 1))))).Save(path);
        return new(request, true, false, "retained executed test failure",
            [new("focused-tests", false, 1, "typed failure", TestResultPaths: [path],
                FailingTestIdentities: [$"{testClass}.Fails"], ExecutedTestCount: 1)]);
    }

    // All files and coordination state are per-instance; process completion is explicitly driven.
    private sealed class RetainedHarness : IDisposable
    {
        internal const string Request = "Infrastructure.Tests: FullyQualifiedName~ExampleTests";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "retained-red-" + Guid.NewGuid().ToString("N"));
        internal string TrxPath => Path.Combine(Root, "result.trx");
        internal Goal Goal { get; } = new AgentOrchestratorKernel().CreateGoal("Reuse retained Red evidence");
        internal ConductorParallelAcceptanceCandidate Candidate { get; }
        internal ConductorFocusedEvidenceRequestContext Context { get; set; } = new("pre-review-stage", "batch-1", [], ExecutionBasisIdentity: "basis-1");
        internal FocusedEvidenceRunResult Evidence { get; set; }
        internal int Executions { get; private set; }
        private readonly ConductorParallelAcceptanceAttemptCompletionGateForTests _completion = new();

        internal RetainedHarness()
        {
            Directory.CreateDirectory(Root);
            Candidate = ConductorParallelAcceptanceCandidate.Create(Goal, 0, [], "branch-1", "main-1");
            Evidence = FailedTrxEvidence(Request, TrxPath);
        }

        internal FocusedEvidenceRunResult Run(Goal _, string request, DotnetBuildEnvironmentLease? lease, CancellationToken token)
        {
            Executions++;
            return Evidence with { Request = request };
        }

        internal ConductorParallelAcceptanceAttemptCoordinator Reload(bool writerBusy = false) => new(
            Path.Combine(Root, "attempts"), TrunkBranchName.Default, isProcessAlive: _ => false,
            attemptCompletionGateForTests: _completion, acquireStableSlotLease: (_, _) => null,
            tryAcquireAttemptWriterLease: writerBusy ? (_, _) => null : null);

        internal ConductorParallelAcceptanceAttempt CompleteFirst(bool cancelled = false)
        {
            var coordinator = Reload();
            FocusedEvidenceRunResult RunFirst(Goal goal, string request, DotnetBuildEnvironmentLease? lease, CancellationToken token)
            {
                if (!cancelled) return Run(goal, request, lease, token);
                Executions++;
                throw new OperationCanceledException("cancelled attempt");
            }
            var started = coordinator.EvaluateFocusedEvidence(Candidate, ConductorAutonomyPolicy.Permissive, Request, RunFirst, Context);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            _completion.RequiredHandleForTests(Goal.Id.Value).CompleteForTests();
            coordinator.MarkReconciled(coordinator.TryReadLatest(Goal.Id.Value)!);
            var completed = coordinator.TryReadLatest(Goal.Id.Value)!;
            Assert.NotNull(completed.ReconciledAt);
            Assert.Equal(1, Executions);
            return completed;
        }

        internal void AssertExecutesAgain(ConductorParallelAcceptanceAttempt first,
            ConductorParallelAcceptanceCandidate candidate, string request, ConductorFocusedEvidenceRequestContext context)
        {
            var coordinator = Reload();
            var next = coordinator.EvaluateFocusedEvidence(candidate, ConductorAutonomyPolicy.Permissive, request, Run, context);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, next.Kind);
            Assert.NotEqual(first.AttemptId, next.Attempt.AttemptId);
            Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(first.MetadataPath)!, "*.attempt.json").Length);
            _completion.RequiredHandleForTests(Goal.Id.Value).CompleteForTests();
            Assert.Equal(2, Executions);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

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
