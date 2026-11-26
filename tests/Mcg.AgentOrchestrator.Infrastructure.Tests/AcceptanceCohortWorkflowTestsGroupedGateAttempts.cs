using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortWorkflowTestsGroupedGateAttempts(CohortCandidateTemplate template)
    : AcceptanceCohortWorkflowTests, IClassFixture<CohortCandidateTemplate>
{
    private const int FirstGeneration = 91001;
    private const int SecondGeneration = 91002;
    private const int FirstChild = 92001;
    private const int SecondChild = 92002;

    [Fact]
    public void OwnedChildUsesConductorBuildStorageRootAfterEnvironmentScrub()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grouped-launch-{Guid.NewGuid():N}");
        var storageRoot = new DotnetBuildStorageRoot(Path.Combine(root, "build-storage"));
        var attempt = new ConductorGroupedGateAttempt(
            "attempt", "cohort", [], new string('a', 40), new string('b', 40),
            "manifest", "identity", DateTimeOffset.UnixEpoch, 0, FirstGeneration,
            Path.Combine(root, "attempt.json"), Path.Combine(root, "result.json"),
            Path.Combine(root, "exit"), Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"), root, ConductorAutonomyPolicy.Permissive.ToJson());

        var startInfo = ConductorGroupedGateAttemptCoordinator.BuildOwnedProcessStartInfo(
            attempt, storageRoot, "dotnet", ["app.dll"]);

        Assert.Equal(storageRoot.RootPath,
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable]);
        Assert.Equal(ConductorGroupedGateAttemptCoordinator.OwnedProcessSubcommandName,
            startInfo.ArgumentList[1]);
        Assert.Equal(attempt.MetadataPath, startInfo.ArgumentList[2]);
    }

    [Fact]
    public void ReconciledLaunchCannotBeClaimedByLateChild()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grouped-claim-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(root, "attempt", "attempt.attempt.json");
            var record = new ConductorGroupedGateAttempt(
                "attempt", "cohort", [], new string('a', 40), new string('b', 40),
                "manifest", "identity", DateTimeOffset.UnixEpoch, 0, FirstGeneration,
                path, path + ".result", path + ".exit", path + ".out", path + ".err",
                root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(record);
            var coordinator = new ConductorGroupedGateAttemptCoordinator(root,
                isProcessAlive: _ => false);
            coordinator.Reconcile(record, "owner-dead");
            Assert.Null(ConductorGroupedGateAttemptCoordinator.TryClaimOwner(
                path, FirstChild, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"));
            Assert.Equal(0, ConductorGroupedGateAttemptCoordinator.Read(path).OwnerProcessId);
        }
        finally { DeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionWritesOwnedAttemptBeforeReturningWithoutRunningVerifier(bool train)
    {
        var (repo, kernel, goals) = CreateReadyMembers(train ? 3 : 2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new SequenceAcceptanceVerifier([]);
            var driver = CreateDriver(kernel, workspace, verifier, cleanup.Hooks);
            var background = new BackgroundGateStartHarness();
            background.Capture(driver);
            var launches = new List<ConductorGroupedGateAttempt>();
            driver.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                attempt =>
                {
                    Assert.True(File.Exists(attempt.MetadataPath));
                    Assert.Equal(0, ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath).OwnerProcessId);
                    launches.Add(attempt);
                    return new ConductorGroupedGateLaunchResult(FirstChild,
                        DateTimeOffset.UnixEpoch, "C:\\dotnet.exe");
                }, pid => pid == FirstChild));

            IReadOnlyList<GateReadyCandidateProjection> expectedMembers;
            string expectedTreeRevision;
            string expectedManifestIdentity;
            string expectedIdentityValue;
            if (train)
            {
                var selection = ProjectTrainSelection(driver, goals);
                var result = driver.RunMergeTrain(selection, goals,
                    ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
                Assert.Contains("outcome=inflight", result.Detail, StringComparison.Ordinal);
                expectedMembers = selection.Members;
                using var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo,
                    expectedMembers[0].MainRevision, selection.BindMembers(id => kernel.GetGoal(id).AuthoritativeBrief.Version), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, cleanup.Hooks);
                expectedTreeRevision = integration.TreeRevision;
                expectedManifestIdentity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path, integration.Members.SelectMany(member => member.LandingPaths)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                expectedIdentityValue = MergeTrainIdentity.Create(integration.Members,
                    expectedMembers[0].MainRevision, expectedTreeRevision, expectedManifestIdentity).Value;
            }
            else
            {
                var selection = ProjectSelection(driver, goals[0], goals[1]);
                var result = driver.RunAcceptanceCohort(selection, goals,
                    ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
                Assert.Contains("outcome=inflight", result.Detail, StringComparison.Ordinal);
                expectedMembers = selection.Members;
                var bindings = selection.BindMembers(id => kernel.GetGoal(id).AuthoritativeBrief.Version);
                using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo,
                    expectedMembers[0].MainRevision, bindings, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, cleanup.Hooks);
                expectedTreeRevision = integration.TreeRevision;
                expectedManifestIdentity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path, bindings.SelectMany(member => member.LandingPaths)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                expectedIdentityValue = AcceptanceCohortIdentity.Create(bindings,
                    expectedMembers[0].MainRevision, expectedTreeRevision, expectedManifestIdentity).Value;
            }

            var launched = Assert.Single(launches);
            var record = ConductorGroupedGateAttemptCoordinator.Read(launched.MetadataPath);
            Assert.Equal(train ? "train" : "cohort", record.Kind);
            Assert.Equal(expectedMembers.Select(member => member.GoalId.Value),
                record.Members.Select(member => member.GoalId));
            Assert.Equal(expectedMembers.Select(member => member.CandidateRevision),
                record.Members.Select(member => member.CandidateRevision));
            Assert.Equal(expectedMembers[0].MainRevision, record.MainRevision);
            Assert.Equal(expectedTreeRevision, record.CombinedTreeRevision);
            Assert.Equal(expectedManifestIdentity, record.ManifestIdentity);
            Assert.Equal(expectedIdentityValue, record.IdentityValue);
            Assert.Equal(FirstChild, record.OwnerProcessId);
            Assert.Equal(FirstGeneration, record.LaunchingGenerationId);
            Assert.Equal(0, background.StartCount);
            Assert.Equal(0, verifier.RunCount);
            Assert.True(File.Exists(record.MetadataPath));
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessorAdoptsMatchingLiveAttemptAndReplaysPublishedReceipt(bool train)
    {
        var (repo, kernel, goals) = CreateReadyMembers(train ? 3 : 2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var first = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            first.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                _ => new ConductorGroupedGateLaunchResult(FirstChild, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"),
                pid => pid == FirstChild));
            var cohort = train ? null : ProjectSelection(first, goals[0], goals[1]);
            var mergeTrain = train ? ProjectTrainSelection(first, goals) : null;
            Run(first, goals, cohort, mergeTrain);
            var record = Assert.Single(Records(workspace));

            var launches = 0;
            var events = new List<string>();
            var alive = true;
            var second = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            second.EnableOwnedGroupedGateAttempts(Coordinator(workspace, SecondGeneration,
                _ => { launches++; return new ConductorGroupedGateLaunchResult(SecondChild, null, null); },
                pid => pid == FirstChild && alive,
                eventSink: events.Add));
            Assert.Equal(goals.Length, second.GetActiveCohortGateMemberGoalIds().Count);
            Assert.Equal(1, second.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
            cohort = train ? null : ProjectSelection(second, goals[0], goals[1]);
            mergeTrain = train ? ProjectTrainSelection(second, goals) : null;
            Run(second, goals, cohort, mergeTrain);
            Run(second, goals, cohort, mergeTrain);
            Assert.Equal(0, launches);
            Assert.Single(events, line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTED ", StringComparison.Ordinal));
            Assert.Equal(goals.Length, second.GetActiveCohortGateMemberGoalIds().Count);
            Assert.Equal(1, second.GetActiveAcceptanceCohortCapacity().ActiveRootCount);

            var landings = new List<ConductorLandingReceipt>();
            second.SuccessfulLandingSink = landings.Add;
            PublishPassingReceipt(record, workspace, repo, id => kernel.GetGoal(id).AuthoritativeBrief.Version);
            File.WriteAllText(record.ResultPath, "passed");
            File.WriteAllText(record.ExitCodePath, "0");
            Assert.Equal(1, second.CountLiveGroupedGateAttempts());
            Assert.Equal(1, second.GetActiveAcceptanceCohortCapacity().ActiveRootCount);
            Assert.Equal(0, Run(second, goals, cohort, mergeTrain));
            Assert.Equal(0, launches);
            alive = false;
            _ = second.GetActiveCohortGateMemberGoalIds();
            var completed = Run(second, goals, cohort, mergeTrain);
            Assert.Equal(goals.Length, completed);
            Assert.Equal(goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
                landings.Select(landing => landing.GoalId).Order(StringComparer.Ordinal));
            if (!train)
            {
                // The direct cohort call lands both members. The conduct loop then records and
                // completes them in its two ordinary post-landing lifecycle steps.
                foreach (var goal in goals)
                {
                    var recorded = Assert.IsType<ConductorAdvanceOutcome.Executed>(
                        second.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
                    Assert.Equal(GoalLifecycleState.Merged, recorded.FromState);
                    var finished = Assert.IsType<ConductorAdvanceOutcome.Executed>(
                        second.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
                    Assert.Equal(GoalLifecycleState.Recorded, finished.FromState);
                }
            }
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            Assert.Equal(0, launches);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdoptedAttemptIsNotReinspectedAfterMainMoves(bool train)
    {
        var (repo, kernel, goals) = CreateReadyMembers(train ? 3 : 2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var first = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            first.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                _ => new ConductorGroupedGateLaunchResult(FirstChild, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"),
                pid => pid == FirstChild));
            Run(first, goals, train ? null : ProjectSelection(first, goals[0], goals[1]),
                train ? ProjectTrainSelection(first, goals) : null);
            var record = Assert.Single(Records(workspace));

            var stops = 0;
            var launches = 0;
            var events = new List<string>();
            var second = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            second.EnableOwnedGroupedGateAttempts(Coordinator(workspace, SecondGeneration,
                _ => { launches++; return new ConductorGroupedGateLaunchResult(SecondChild, null, null); },
                pid => pid == FirstChild,
                _ => { stops++; return true; }, events.Add));
            Assert.Equal(goals.Length, second.GetActiveCohortGateMemberGoalIds().Count);
            Assert.Equal(SecondGeneration,
                ConductorGroupedGateAttemptCoordinator.Read(record.MetadataPath).AdoptedByGenerationId);

            File.WriteAllText(Path.Combine(repo, "unrelated-main-change.txt"), "new main revision");
            RunGit(repo, "add", "unrelated-main-change.txt");
            RunGit(repo, "commit", "-m", "Move main after grouped gate adoption");
            Assert.Equal(goals.Length, second.GetActiveCohortGateMemberGoalIds().Count);
            Assert.Equal(goals.Length, second.GetActiveCohortGateMemberGoalIds().Count);
            Assert.Equal(0, stops);
            Assert.Equal(0, launches);
            Assert.Single(events, line => line.StartsWith("ACCEPTANCE_COHORT_ADOPTED ", StringComparison.Ordinal));
            Assert.Null(ConductorGroupedGateAttemptCoordinator.Read(record.MetadataPath).ReconciledAt);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData("members")]
    [InlineData("candidate")]
    [InlineData("main")]
    [InlineData("tree")]
    public void ChangedOrphanIdentityIsStoppedBeforeOneFreshLaunch(string field)
    {
        var (repo, kernel, goals) = CreateReadyMembers(2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var first = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            first.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                _ => new ConductorGroupedGateLaunchResult(FirstChild, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"),
                pid => pid == FirstChild));
            var selection = ProjectSelection(first, goals[0], goals[1]);
            Run(first, goals, selection, null);
            var original = Assert.Single(Records(workspace));
            var changed = field switch
            {
                "members" => original with { Members = original.Members.Select((member, index) =>
                    index == 0 ? member with { GoalId = new string('f', 32) } : member).ToArray() },
                "candidate" => original with { Members = original.Members.Select((member, index) =>
                    index == 0 ? member with { CandidateRevision = new string('f', 40) } : member).ToArray() },
                "main" => original with { MainRevision = new string('f', 40) },
                _ => original with { CombinedTreeRevision = new string('f', 40) }
            };
            ConductorGroupedGateAttemptCoordinator.Save(changed);
            var stops = 0;
            var launches = 0;
            var events = new List<string>();
            var second = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            second.EnableOwnedGroupedGateAttempts(Coordinator(workspace, SecondGeneration,
                _ => { launches++; return new ConductorGroupedGateLaunchResult(SecondChild, null, null); },
                pid => pid == FirstChild || pid == SecondChild,
                _ => { stops++; return true; }, events.Add));
            Run(second, goals, ProjectSelection(second, goals[0], goals[1]), null);
            Assert.Equal(1, stops);
            Assert.Equal(1, launches);
            Assert.Single(events, line => line.Contains($"ADOPTION_REFUSED") && line.EndsWith($"field={field}"));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void DeadChildWithoutReceiptRecordsOneIndeterminateOutcomeAndDoesNotRelaunch()
    {
        var (repo, kernel, goals) = CreateReadyMembers(2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var first = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            first.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                _ => new ConductorGroupedGateLaunchResult(FirstChild, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"),
                pid => pid == FirstChild));
            Run(first, goals, ProjectSelection(first, goals[0], goals[1]), null);
            var record = Assert.Single(Records(workspace));
            var launches = 0;
            var second = CreateDriver(kernel, workspace, new SequenceAcceptanceVerifier([]), cleanup.Hooks);
            second.EnableOwnedGroupedGateAttempts(Coordinator(workspace, SecondGeneration,
                _ => { launches++; return new ConductorGroupedGateLaunchResult(SecondChild, null, null); },
                _ => false));
            var selection = ProjectSelection(second, goals[0], goals[1]);
            Run(second, goals, selection, null);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory,
                "cohort-acceptance.db"));
            var firstReceipt = Assert.IsType<AcceptanceCohortReceipt>(store.TryReadReceipt(record.IdentityValue));
            Run(second, goals, selection, null);
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(store.TryReadReceipt(record.IdentityValue));
            Assert.Equal(firstReceipt.ReceiptId, receipt.ReceiptId);
            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, receipt.Outcome);
            Assert.Equal(AcceptanceCohortInvalidationReason.InfrastructureRetryExhausted,
                receipt.Invalidation?.Reason);
            Assert.Equal(0, launches);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedGateResolvesRelativePathFromLaneWorktreeAndPersistsDeferral(bool train)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows bare command resolution.");
        var (repo, kernel, goals) = CreateReadyMembers(train ? 3 : 2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new SequenceAcceptanceVerifier([]);
            var driver = CreateDriver(kernel, workspace, verifier, cleanup.Hooks);
            var launches = 0;
            var ownerAlive = true;
            driver.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                _ => { launches++; return new ConductorGroupedGateLaunchResult(Environment.ProcessId, null, null); },
                _ => ownerAlive));
            var cohort = train ? null : ProjectSelection(driver, goals[0], goals[1]);
            var trainSelection = train ? ProjectTrainSelection(driver, goals) : null;
            Run(driver, goals, cohort, trainSelection);
            var attempt = Assert.Single(Records(workspace));

            var environment = GateToolResolutionPreflightTests.CreateToolEnvironment(workspace.OrchestratorDirectory);
            var powershell = Path.Combine(environment["SystemRoot"]!, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var relativeTools = Path.Combine(".orchestrator", "relative-gate-tools");
            var conductorTools = Path.Combine(attempt.ExecutionDirectory, relativeTools);
            Directory.CreateDirectory(conductorTools);
            File.Move(powershell, Path.Combine(conductorTools, "powershell.exe"));
            environment["PATH"] += $";{relativeTools}";
            Assert.Empty(GateToolResolutionPreflight.Evaluate(environment, attempt.ExecutionDirectory)!.MissingTools);

            Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(attempt.MetadataPath,
                child => driver.RunGroupedGateAttemptBody(child, () => environment), (_, _) => Stream.Null));
            using var result = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
            Assert.Equal("completed", result.RootElement.GetProperty("Status").GetString());
            Assert.Equal("infrastructure-failure", result.RootElement.GetProperty("Verdict").GetString());
            var receiptId = result.RootElement.GetProperty("ReceiptId").GetString();
            Assert.Equal(0, verifier.RunCount);

            ownerAlive = false;
            Run(driver, goals, cohort, trainSelection);
            Assert.Equal(1, launches);
            if (train)
            {
                var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
                var receipt = Assert.IsType<MergeTrainReceipt>(store.TryReadReceipt(attempt.IdentityValue));
                Assert.Equal(MergeTrainGateOutcome.InfrastructureFailure, receipt.Outcome);
                Assert.Equal(receiptId, receipt.ReceiptId);
                var failure = Assert.Single(receipt.FailedChecks);
                Assert.StartsWith("infrastructure:gate-tool-resolution-missing:", failure);
                Assert.Contains("Missing gate tools: powershell.", failure);
                Assert.Contains($"PATH length: {environment["PATH"]!.Length}.", failure);
            }
            else
            {
                var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
                var receipt = Assert.IsType<AcceptanceCohortReceipt>(store.TryReadReceipt(attempt.IdentityValue));
                Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, receipt.Outcome);
                Assert.Equal(receiptId, receipt.ReceiptId);
                Assert.Equal("gate-tool-resolution-missing", receipt.InfrastructureReasonCode);
                Assert.Contains("Missing gate tools: powershell.", receipt.InfrastructureDetail);
                Assert.Contains($"PATH length: {environment["PATH"]!.Length}.", receipt.InfrastructureDetail);
            }
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedAttributionLaneReceiptsRetainGateToolResolution(bool train)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows bare command resolution.");
        var (repo, kernel, goals) = CreateReadyMembers(train ? 3 : 2);
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var environment = GateToolResolutionPreflightTests.CreateToolEnvironment(workspace.OrchestratorDirectory);
            var failure = FailedVerification(repo, "grouped-attribution-red.trx", "combined");
            if (train)
            {
                failure = failure with
                {
                    TestResultPaths = [MergeTrainFocusedRecheckTests.WriteTrx(repo, "train-attribution-red.trx",
                        [(MergeTrainPrefixBisectionTests.TestName, "Failed")])]
                };
            }
            var passingPartition = new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("partition", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, "grouped-partition.trx")]);
            var verifier = new AttributionReplayVerifier(failure, passingPartition,
                Path.Combine(workspace.OrchestratorDirectory, "tools", "dotnet.exe"));
            var driver = CreateDriver(kernel, workspace, verifier, cleanup.Hooks);
            driver.EnableOwnedGroupedGateAttempts(Coordinator(workspace, FirstGeneration,
                _ => new ConductorGroupedGateLaunchResult(Environment.ProcessId, null, null), _ => true));
            var cohort = train ? null : ProjectSelection(driver, goals[0], goals[1]);
            var trainSelection = train ? ProjectTrainSelection(driver, goals) : null;
            Run(driver, goals, cohort, trainSelection);
            var attempt = Assert.Single(Records(workspace));
            var preflightCount = 0;

            Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(attempt.MetadataPath,
                child => driver.RunGroupedGateAttemptBody(child, () =>
                {
                    preflightCount++;
                    return new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
                }), (_, _) => Stream.Null));

            Assert.Equal(1, preflightCount);
            Assert.Equal(train ? 1 : 2, verifier.AttributionReceipts.Count);
            Assert.All(verifier.AttributionReceipts, receipt =>
            {
                Assert.Equal(attempt.MainRevision, receipt.ObservedMainSha);
                Assert.Equal(environment["PATH"]!.Length, receipt.PathLength);
                Assert.True(receipt.SystemDirectoryOnPath);
                Assert.True(receipt.WindowsPowerShellDirectoryOnPath);
                Assert.True(receipt.GitDirectoryOnPath);
                Assert.True(receipt.DotnetDirectoryOnPath);
            });
            Assert.Null(GateToolResolutionPreflight.Current);
        }
        finally { DeleteDirectory(repo); }
    }

    private sealed class AttributionReplayVerifier(
        AcceptanceVerificationResult failure, AcceptanceVerificationResult passing, string executable)
        : IGoalAcceptanceVerifier
    {
        private bool _ranCombined;

        internal List<GateReplayReceipt> AttributionReceipts { get; } = [];

        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            if (!_ranCombined)
            {
                _ranCombined = true;
                return Task.FromResult(failure);
            }
            var owner = Assert.IsType<AcceptanceAttemptExecutionOwner>(executionOwner);
            Capture(worktreePath, GateReplayReceiptTests.CreateContext(worktreePath) with { AttemptIdentity = owner.Identity });
            return Task.FromResult(passing);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            var owner = Assert.IsType<AcceptanceFocusedVerificationOwner>(executionOwner);
            // The focused verifier runs checks under an arm view after entering the arm.
            using var arm = GateToolResolutionPreflight.EnterFocusedArm(worktreePath);
            var view = new AcceptanceRunExecutionContextView(owner, owner.ResultsPrefix + "-candidate");
            Capture(worktreePath, GateReplayReceiptTests.CreateContext(worktreePath) with
            {
                AttemptIdentity = null,
                FocusedReplayIdentity = GateReplayReceipt.ResolveFocusedLaunchIdentity(view)
            });
            // One prefix invocation is enough to observe its launch record.
            return Task.FromResult(new FocusedEvidenceRunResult(request, false, false, "probe complete", []));
        }

        private void Capture(string worktreePath, GateHeartbeatContext heartbeat)
        {
            var startInfo = new ProcessStartInfo(executable) { WorkingDirectory = worktreePath };
            startInfo.Environment.Clear();
            var launch = Assert.IsType<GateReplayReceipt.Launch>(GateReplayReceipt.CaptureLaunch(
                new AcceptanceProcessRequest { HeartbeatContext = heartbeat }, startInfo, true, [executable]));
            AttributionReceipts.Add(Assert.IsType<GateReplayReceipt>(
                launch.Bind(heartbeat, Path.Combine(worktreePath, "stdout"), Path.Combine(worktreePath, "stderr"))));
        }
    }

    private static ConductorDriver CreateDriver(AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace, IGoalAcceptanceVerifier verifier,
        GoalWorktreeCleanupHooks cleanup) => new(kernel, workspace, verifier,
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup);

    private static ConductorGroupedGateAttemptCoordinator Coordinator(
        OrchestratorWorkspace workspace, int generation,
        Func<ConductorGroupedGateAttempt, ConductorGroupedGateLaunchResult> launch,
        Func<int, bool> alive,
        Func<ConductorGroupedGateAttempt, bool>? stop = null,
        Action<string>? eventSink = null) => new(
            Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"),
            generationId: generation, launch: launch, isProcessAlive: alive,
            stop: stop, eventSink: eventSink);

    private static IReadOnlyList<ConductorGroupedGateAttempt> Records(OrchestratorWorkspace workspace) =>
        new ConductorGroupedGateAttemptCoordinator(Path.Combine(workspace.OrchestratorDirectory,
            "grouped-gate-attempts")).ReadAll().ToArray();

    private static int Run(ConductorDriver driver, Goal[] goals,
        ConductorAcceptanceCohortSelection? cohort, ConductorMergeTrainSelection? train)
    {
        if (cohort is not null)
            return driver.RunAcceptanceCohort(cohort, goals,
                ConductorAutonomyPolicy.Permissive, runGateInBackground: true).MemberResults.Count;
        return driver.RunMergeTrain(train!, goals,
            ConductorAutonomyPolicy.Permissive, runGateInBackground: true).MemberResults.Count;
    }

    private static void PublishPassingReceipt(ConductorGroupedGateAttempt attempt,
        OrchestratorWorkspace workspace, string repo, Func<GoalId, int?> briefVersion)
    {
        var trx = WritePassingTrx(repo, "grouped-gate-adopted.trx");
        if (attempt.Kind == "cohort")
        {
            var identity = AcceptanceCohortIdentity.Create(
                new ConductorAcceptanceCohortSelection(attempt.Members
                    .Select(member => member.ToProjection(attempt.MainRevision)).ToArray(), []).BindMembers(briefVersion),
                attempt.MainRevision, attempt.CombinedTreeRevision, attempt.ManifestIdentity);
            Assert.Equal(attempt.IdentityValue, identity.Value);
            new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory,
                "cohort-acceptance.db")).SaveGateReceipt(new AcceptanceCohortReceipt(
                "grouped-adopted", identity, AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow, 1, [], 0, [trx], ValidForLanding: true));
        }
        else
        {
            // The train store persists the same identity that the admitting call materialized.
            using var materialized = GoalWorktrees.CreateMergeTrainWorkspace(repo,
                attempt.MainRevision, new ConductorMergeTrainSelection(attempt.Members
                    .Select(member => member.ToProjection(attempt.MainRevision)).ToArray()).BindMembers(briefVersion), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);
            var identity = MergeTrainIdentity.Create(materialized.Members,
                attempt.MainRevision, materialized.TreeRevision, attempt.ManifestIdentity);
            Assert.Equal(attempt.IdentityValue, identity.Value);
            new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory,
                "merge-train-acceptance.db")).SaveGateReceipt(new MergeTrainReceipt(
                "grouped-adopted", identity, MergeTrainGateOutcome.Passed,
                DateTimeOffset.UtcNow, 1, [], 0, [trx], ValidForLanding: true));
        }
    }

    private (string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals) CreateReadyMembers(int count)
    {
        var paths = new[]
        {
            "tests/Mcg.AgentOrchestrator.Core.Tests/First.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Second.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Third.cs"
        };
        var copy = template.Copy($"grouped-{count}", count, (preparedRepo, ids) =>
        {
            AddAcceptanceManifest(preparedRepo);
            for (var index = 0; index < ids.Length; index++)
                CreateWorktreeCandidate(preparedRepo, ids[index], paths[index], $"member-{index}");
        });
        var repo = copy.Repo;
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, count)
            .Select(index => copy.CreateCompletedGoal(kernel, index, $"Grouped gate member {index}")).ToArray();
        for (var index = 0; index < count; index++)
        {
            var revision = copy.Revisions[index];
            kernel.RecordGoalRefinement(goals[index].Id, new RefinedSpec(goals[index].Objective,
                ["The acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
            { AcceptanceGateOwnedAcceptanceCriteria = ["The acceptance gate passes"] });
            kernel.MapCriterionEvidenceOwner(goals[index].Id, 0, 1,
                CriterionEvidenceOwner.Acceptance, "test",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: revision);
        }
        return (repo, kernel, goals);
    }
}
