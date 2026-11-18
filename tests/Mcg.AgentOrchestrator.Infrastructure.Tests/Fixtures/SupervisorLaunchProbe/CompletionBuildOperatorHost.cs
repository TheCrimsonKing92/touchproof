using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static CompletionBuildOperatorRepository;

internal static class CompletionBuildOperatorHost
{
    internal static int Run(string root, string source, string action)
    {
        var f = Load(root);
        Require(f.Source == source, "Source repository changed across hosts.");
        if (action == "successor") ConductorLoopHandoff.WaitForAuthorityTransferIfRequested();
        using var lease = ConductorLoopLeaseController.Acquire(root);
        var kernel = f.LoadKernel();
        var task = kernel.GetTask(f.GoalId, f.TaskId);
        var coordinator = new CompletionBuildEvidenceCoordinator(f.State, root,
            remoteBuildBudget: _ => TimeSpan.Zero, storageRoot: new(f.Storage));
        var runner = new BackgroundDispatchRunner(completionBuildEvidence: coordinator,
            resolveWorkerBuildReceiptPath: goal => Path.Combine(
                DotnetBuildStorageLayout.GoalArtifactsPath(goal, new(f.Storage)), "worker-build-receipt.json"));
        var store = new SqliteOperatorIntentStore(Path.Combine(root, "operator-intents.db"), root);
        var intents = new OperatorIntentCoordinator(store) { CancelLatestProcess = runner.CancelLatestProcess };

        if (action == "lease-successor")
        {
            // CAS transfer through the real lease owner; keep a real successor alive while the
            // old operation settles, so unconditional release would be caught independently.
            var operation = task.CompletionBuildEvidence!;
            var successor = new GoalEvidenceOperationOwner("operator-scenario-successor", Environment.ProcessId, Guid.NewGuid().ToString("N"));
            var remediation = new ReconcileSweepRemediationStore(f.State);
            Require(remediation.TryReplaceAcceptanceLease(f.Goal, operation.LeaseOwner!, successor.Serialize()), "Lease transfer lost its expected owner.");
            using var scope = new GoalEvidenceOperationScope(remediation, root, kernel.GetGoal(f.GoalId), successor,
                successor.Serialize(), null, () => DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(root, "successor.json"), JsonSerializer.Serialize(new { owner = successor.Serialize(), pid = Environment.ProcessId }));
            using var ready = EventWaitHandle.OpenExisting(f.GateToken + "-successor-ready");
            using var release = EventWaitHandle.OpenExisting(f.GateToken + "-successor-release");
            // The successor is a lease owner, not a conductor. Release the conductor lock so
            // the original dispatch can be settled by another real reconciliation host.
            lease.Release();
            ready.Set();
            Require(release.WaitOne(TimeSpan.FromMinutes(5)), "Successor release signal absent.");
            scope.Complete("Independent operator released the successor lease.");
            return 0;
        }

        switch (action)
        {
            case "progress":
                var id = Guid.NewGuid().ToString("N");
                store.EnqueueAsync(new(id, id, OperatorIntentVerbs.GoalPriority, f.DisjointGoal, null,
                    "{\"level\":\"critical\",\"rationale\":\"operator intent during held compilation\"}", [],
                    "isolated-operator", "cli", "local-process", DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(root, "intent-id.txt"), id);
                break;
            case "cancel":
                var process = task.LastProcess!;
                var cancelId = Guid.NewGuid().ToString("N");
                var payload = new CancelDispatchOperatorIntentPayload(process.ProcessId, process.StartedAt,
                    task.CompletionBuildEvidence!.DispatchId);
                store.EnqueueAsync(new(cancelId, cancelId, OperatorIntentVerbs.CancelDispatch, f.Goal, f.Task,
                    JsonSerializer.Serialize(payload), [], "isolated-operator", "cli", "local-process", DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
                break;
            case "stale-candidate":
                File.AppendAllText(Path.Combine(task.LastProcess!.WorkingDirectory, "src", "Feature", "Feature.cs"), "\npublic class LaterCandidate { }\n");
                break;
            case "reset":
                kernel.RequeueInterruptedDispatch(f.GoalId, f.TaskId, "Operator resets originating dispatch", RetryCause.EnvironmentApparatusFailure);
                f.Save(kernel);
                var original = task.CompletionBuildEvidence!;
                var rejected = false;
                try { kernel.RecordTaskDispatch(f.GoalId, f.TaskId,
                    new("fixture", original.WorkerProcess.Command, original.WorktreeRoot, DateTimeOffset.UtcNow)); }
                catch (InvalidOperationException ex) when (ex.Message == "A pending completion build must be cancelled before another dispatch." ||
                    ex.Message == "Completion build custody must be released before another dispatch.")
                { rejected = true; }
                Require(rejected, "Replacement dispatch was not fenced by unresolved child custody.");
                f.Record("dispatch-replacement-fenced", new { original.Id, original.DispatchId, original.ChildPid });
                return 0;
            case "settle":
                coordinator.ReconcileAttached(kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(task.CompletionBuildEvidence!));
                Require(task.LastVerification is null && task.DispatchHistory.All(d => d.ResultCommit is null), "Historical operation authorized completion.");
                f.Save(kernel);
                Record(f, kernel, action);
                return 0;
            case "cancel-attached":
                coordinator.CancelAttached(kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(task.CompletionBuildEvidence!));
                f.Save(kernel);
                Record(f, kernel, action);
                return 0;
            case "replace":
                Require(task.CompletionBuildEvidence is { CustodyReleased: true }, "Old child custody is unresolved.");
                var worktree = task.CompletionBuildEvidence.WorktreeRoot;
                SeedDispatch(kernel, kernel.GetGoal(f.GoalId), task, worktree, f.InitialHead, "src/Feature/Feature.cs");
                WorkerBuildProjectScope.Prepare(worktree, f.Goal, f.Task, "main");
                CaptureScope(kernel, f.GoalId, task, worktree, f.InitialHead);
                f.Save(kernel);
                break;
            case "obsolete-cancel":
                var old = JsonSerializer.Deserialize<CompletionBuildEvidenceOperation>(File.ReadAllText(Path.Combine(root, "original-operation.json")))!;
                var denied = false;
                try { coordinator.CancelAttached(kernel, f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(old)); }
                catch (InvalidOperationException ex) when (ex.Message == "Completion build operation selector mismatch.") { denied = true; }
                Require(denied && CompletionBuildEvidenceChildIdentity.Probe(task.CompletionBuildEvidence!) == CompletionBuildChildLiveness.Live,
                    "Obsolete selector affected the replacement child.");
                Record(f, kernel, action);
                return 0;
            case "stop": File.WriteAllText(f.Stop, "Independent operator stop"); break;
        }

        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => throw Forbidden(), dispatchAndStart: _ => throw Forbidden(),
            startRecordedDispatches: null, buildServerShutdown: null,
            runAcceptanceVerification: _ => throw Forbidden(), runAdvisorySemanticAcceptance: null,
            retryTask: null, recordTaskNote: null, recordCriterionRetryFeedback: null, clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => throw Forbidden(), land: (_, _) => throw Forbidden(), afterSuccessfulLanding: null,
            record: _ => { }, cleanup: _ => throw Forbidden(), writeEscalation: (_, _, _) => { }, classifyChangeRisk: _ => null,
            executionDirectory: root);
        var transitions = ConductorAutonomyPolicy.Manual.TransitionMap.ToDictionary(p => p.Key, p => p.Value);
        transitions[GoalLifecycleState.Running] = ConductorTransitionDecision.Auto;
        var policy = ConductorAutonomyPolicy.Manual with { Name = "IsolatedCompletionBuildOperator", TransitionMap = transitions };
        var operations = new GoalDispatchOperations();
        var renewal = action == "renewal" ? ConductorLoopHandoff.Create(new(
            ["completion-build-host", root, source, "successor"], root, root, Path.Combine(root, "handoff-logs"),
            Path.Combine(root, "run-events.db"), f.Stop, 0, 1, lease.Release,
            VerificationTimeout: TimeSpan.FromSeconds(60), VerificationHardTimeout: TimeSpan.FromSeconds(90),
            ReacquireCurrentLease: lease.Reacquire,
            SuccessorCommandPrefix: [Environment.ProcessPath!, typeof(CompletionBuildOperatorHost).Assembly.Location]),
            verify: (successor, _) =>
            {
                using var successorProcess = Process.GetProcessById(successor.ProcessId);
                Require(new DateTimeOffset(successorProcess.StartTime.ToUniversalTime()) == successor.StartedAt,
                    "Renewal successor process identity changed.");
                using var signal = EventWaitHandle.OpenExisting(f.GateToken + "-renewed");
                var observed = signal.WaitOne(TimeSpan.FromSeconds(60));
                var exited = successorProcess.WaitForExit(60000);
                var passed = observed && exited && successorProcess.ExitCode == 0;
                return new(!exited, File.Exists(successor.StdoutPath), passed,
                    "real successor completed production tick and settled its conductor lease after authority transfer");
            }) : null;
        var started = Stopwatch.GetTimestamp();
        var summary = new ConductorBatchLoop(operatorIntents: intents,
            refreshGoalDispatchesBeforeAdvance: (k, g) => operations.RefreshDispatches(k, g, runner),
            reapGoalRunningDispatches: (k, g) => runner.CancelRunningProcessesForGoal(k, g.Id),
            detachGoalRunningDispatches: (k, g) => runner.DetachRunningProcessesForGoal(k, g.Id),
            handoffOnMaxDuration: renewal)
            .Run(kernel, driver, policy, f.Stop, maxIterations: action == "renewal" ? null : 1,
                maxDuration: action == "renewal" ? TimeSpan.Zero : null,
                onlyGoalId: action == "start" ? f.Goal : null,
                persistGoalTick: (k, _) => f.Save(k), quiet: true,
                onTick: tick => f.Record("tick", new { action, tick.Tick, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }));
        // A renewed successor has checkpointed newer state; the predecessor must not overwrite it.
        if (action != "renewal") f.Save(kernel);
        f.Record("loop-exit", new { action, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, summary });
        if (action == "renewal") Require(summary.Handoff?.Started == true, "Production renewal failed to transfer authority.");
        else Record(f, kernel, action);
        if (action == "successor")
        {
            using var renewed = EventWaitHandle.OpenExisting(f.GateToken + "-renewed");
            renewed.Set();
        }
        return 0;
    }

    private static Exception Forbidden() => new InvalidOperationException("Scenario tried to dispatch a worker, gate, rebase or landing.");
    private static void Record(CompletionBuildOperatorRepository f, AgentOrchestratorKernel kernel, string action)
    {
        var task = kernel.GetTask(f.GoalId, f.TaskId);
        f.Record("state", new { action, operation = task.CompletionBuildEvidence, status = task.Status.ToString(),
            verification = task.LastVerification?.Succeeded, commit = task.LastDispatch?.ResultCommit,
            disjointStatus = kernel.GetTask(new(f.DisjointGoal), new(f.DisjointTask)).Status.ToString(),
            disjointPriority = kernel.GetGoal(new(f.DisjointGoal)).Priority.ToString() });
    }
}
