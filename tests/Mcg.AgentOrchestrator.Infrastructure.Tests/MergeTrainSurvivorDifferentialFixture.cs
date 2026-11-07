using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each invocation owns its repository, worktrees, kernels, database and cleanup hooks.
internal sealed class MergeTrainSurvivorDifferentialFixture(CohortCandidateTemplate template)
    : AcceptanceCohortWorkflowTests
{
    internal const string Test = "Ns.UnownedTests.Fails";
    internal static string Message => "differential failure\n" + new string('x', 2100) + "UNBOUNDED_TAIL";

    internal void WithTrain(Action<Scenario> assert, string mode = "fatal", bool readOnlyChild = false)
    {
        string[] paths = ["tests/Mcg.AgentOrchestrator.Core.Tests/FirstTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SecondTests.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/NewestTests.cs"];
        var copy = template.Copy("survivor-" + (mode == "attributed" ? "owned" : "unowned"), 3, (repo, ids) =>
        {
            AddAcceptanceManifest(repo);
            for (var index = 0; index < ids.Length; index++)
                CreateWorktreeCandidate(repo, ids[index], paths[index], $"member {index}");
            if (mode == "attributed")
                foreach (var name in new[] { "AlphaReachTests", "ZebraReachTests" })
                    CreateWorktreeCandidate(repo, ids[0], $"tests/Mcg.AgentOrchestrator.Core.Tests/{name}.cs",
                        $"public class {name} {{ [Xunit.Fact] public void Fails() {{ }} }}");
        });
        var repo = copy.Repo;
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = kernel.CreateGoal(copy.GoalIds[index], $"Differential member {index}",
                    [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
                kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                ConductorDriverTests.DispatchTask(kernel, goal, goal.Tasks.Single(), workingDirectory: repo);
                kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id,
                    new TaskVerificationRecord("fixture", repo, 0, "ok", "", DateTimeOffset.UtcNow, HasCommittedChanges: true));
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                Assert.Equal(GoalStatus.Verified, goal.Status);
                return goal;
            }).ToArray();
            var names = mode switch
            {
                "guard" => new[] { "WorkflowDecisionCoverageRatchetTests.EveryUndecidedSite_IsAllowListedByFileAndMember" },
                "attributed" => new[] { "AlphaReachTests.Fails", "ZebraReachTests.Fails" },
                "theory" => new[] { "UnownedTests.Fails(a: 1, b: 2)" },
                _ => new[] { "UnownedTests.Fails", "OtherUnownedTests.Fails" }
            };
            var message = mode == "apparatus" ? "DotnetBuildSlotsBusyException: Stable dotnet build slots busy" : Message;
            var redPaths = MergeTrainReachAttributionTests.Trx(repo, names, message);
            if (mode == "unreadable") File.WriteAllText(redPaths[0], "malformed TRX");
            var red = new AcceptanceVerificationResult(false, false, 1, "red",
                Checks: [new AcceptanceCheckResult("train", false, 1, "red")], TestResultPaths: redPaths);
            var passing = new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("survivors", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, "passing.trx")]);
            var verifier = new SequenceAcceptanceVerifier([red, mode == "survivors-red" ? red : passing]);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = ProjectTrainSelection(driver, goals);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            AgentOrchestratorKernel executingKernel = kernel;
            if (readOnlyChild)
            {
                repository.SaveAsync(kernel).GetAwaiter().GetResult();
                executingKernel = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .LoadGoalsAsync(goals.Select(goal => goal.Id).ToArray()).GetAwaiter().GetResult();
            }
            var executingDriver = readOnlyChild
                ? new ConductorDriver(executingKernel, workspace, verifier,
                    AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks)
                : driver;
            var result = executingDriver.RunMergeTrain(selection, goals.Select(goal => executingKernel.GetGoal(goal.Id)).ToArray(),
                ConductorAutonomyPolicy.Permissive, gateOnly: true);
            var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            var receipts = store.ReadReceiptsSince(DateTimeOffset.MinValue);
            var first = Assert.Single(receipts, receipt => receipt.Identity.Members.Count == 3);
            assert(new Scenario(repo, workspace, kernel, executingKernel, goals, driver, selection, result,
                first, verifier, () => verifier.RunCount, store, repository, cleanup.Hooks));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    internal sealed record Scenario(string Repo, OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel,
        AgentOrchestratorKernel ExecutingKernel, Goal[] Goals, ConductorDriver Driver,
        ConductorMergeTrainSelection Selection, ConductorMergeTrainRunResult Result, MergeTrainReceipt Red,
        IGoalAcceptanceVerifier Verifier, Func<int> Runs, MergeTrainAcceptanceStore Store, SqliteOrchestratorStateRepository Repository,
        GoalWorktreeCleanupHooks Hooks)
    {
        internal Goal Dropped => Goals[2];
        internal void Replay() => Driver.RunMergeTrain(Selection, Goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
        internal IReadOnlySet<GoalId> Reconcile() => Driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive);
        internal ConductorDriver Restart(out AgentOrchestratorKernel restarted)
        {
            Repository.SaveAsync(Kernel).GetAwaiter().GetResult();
            restarted = Repository.LoadAsync().GetAwaiter().GetResult();
            return new ConductorDriver(restarted, Workspace, Verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: Hooks);
        }

        internal string Advance(Goal goal, string source)
        {
            var index = Array.IndexOf(Goals, goal);
            var harness = new AttributedFailureDeveloperRouteTests.RouteHarness(Kernel, Selection.Members[index].CandidateRevision);
            var routing = harness.Configure();
            routing.BeginTick(Kernel, 1);
            var result = routing.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive with { AutomaticRetryOperatorRoutingHoldSeconds = 0 });
            Assert.False(result.WasEscalated);
            Assert.Empty(harness.Escalations);
            Assert.Equal(goal.Tasks.Single().Id, Assert.Single(harness.Dispatches));
            var retry = Assert.Single(AttributedFailureDeveloperRouteTests.MarkerRetries(goal));
            Assert.Contains("source=" + source, retry.Message);
            Assert.Equal(AgentRole.Developer, goal.Tasks.Single().RequiredRole);
            return retry.Message;
        }

        internal ProgressEvent[] Routes(Goal? goal = null) => (goal ?? Dropped).Timeline.Where(record =>
            record.Kind == ProgressKind.GoalPolicyDecision &&
            record.Message.StartsWith("merge train RED attributed (", StringComparison.Ordinal)).ToArray();

        internal string ReviseDropped() => CreateWorktreeCandidate(Repo, Dropped.Id, Selection.Members[2].LandingPaths[0], "revised");
    }
}
