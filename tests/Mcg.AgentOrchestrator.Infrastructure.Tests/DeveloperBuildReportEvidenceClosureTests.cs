using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;

// Parallel-safe: stored-record kernels only. Git and gate I/O are supplied at their production boundaries.
public sealed class DeveloperBuildReportEvidenceClosureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StoredBuildReport_ClosesAutomaticallyAndPreservesAuthority(bool historical)
    {
        var fixture = new Records(historical);
        var payload = fixture.Wait.PlannerEvidence;
        var identity = fixture.Wait.PlannerIdentity;
        var fingerprint = fixture.Wait.QuestionFingerprint;
        var queries = new List<string>();
        var skipped = fixture.Close(name => { queries.Add(name); return "attempt gate-1"; });

        Assert.Empty(skipped);
        Assert.True(fixture.Wait.IsCompleted);
        Assert.Equal(payload, fixture.Wait.PlannerEvidence);
        Assert.Equal(identity, fixture.Wait.PlannerIdentity);
        Assert.Equal(fingerprint, fixture.Wait.QuestionFingerprint);
        Assert.Equal("developer", fixture.Wait.EvidenceOwner);
        Assert.Equal(HumanInputAnswerOrigin.Worker, Assert.Single(fixture.Wait.AnswerHistory).Origin);
        Assert.Contains($"build_invocations={fixture.Invocation.Id}", fixture.Wait.Answer);
        Assert.Contains($"dispatch={fixture.Invocation.DispatchId}", fixture.Wait.Answer);
        Assert.Contains($"candidate={fixture.Implementation} landing_candidate={fixture.Candidate}", fixture.Wait.Answer);
        Assert.Contains($"implementation_tree={fixture.Invocation.TreeDigest}", fixture.Wait.Answer);
        Assert.Contains($"worker_result_log={fixture.Developer.LastVerification!.StandardOutputPath}", fixture.Wait.Answer);
        Assert.Equal(Records.Classes.Order(StringComparer.Ordinal), queries.Order(StringComparer.Ordinal));
        Assert.All(queries, name => Assert.Contains($"{name}@attempt gate-1", fixture.Wait.Answer));
        Assert.Empty(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
        Assert.Empty(fixture.Close(_ => throw new Exception("Completed request queried evidence again")));
        Assert.Single(fixture.Wait.AnswerHistory);
        Assert.Single(fixture.Goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived));
        Assert.Equal(0, fixture.Kernel.BuildHumanInputWorklist(fixture.Goal.Id).OpenCount);
        if (historical)
        {
            Assert.Equal("a2dc31071353480ca3e840ba5949b412", fixture.Invocation.Id);
            Assert.Equal(DateTimeOffset.Parse("2026-11-03T18:53:04.2883284Z"), fixture.Invocation.Observations[^1].At);
            Assert.Equal(WorkerBuildOutcome.Success, fixture.Invocation.Outcome);
            Assert.Equal(0, fixture.Invocation.CompilerExitCode);
            Assert.Equal(Records.HistoricalReport, fixture.Developer.LastVerification!.AuthoritativeStandardOutput);
            var tester = Records.ReadInvocation(Records.HistoricalTesterJson);
            Assert.Equal("814727958d5a46bab28613a5491069d7", tester.Id);
            Assert.Equal(fixture.Invocation.TreeDigest, tester.TreeDigest);
            tester.RequireValid();
        }
    }

    [Theory]
    [InlineData("missing-receipt", "build-invocation-missing:")]
    [InlineData("stale-round", "build-invocation-round-differs:")]
    [InlineData("wrong-goal", "build-invocation-round-differs:")]
    [InlineData("wrong-task", "build-invocation-round-differs:")]
    [InlineData("wrong-dispatch", "build-invocation-round-differs:")]
    [InlineData("stale-brief", "build-brief-version-differs")]
    [InlineData("stale-wait", "brief-version-differs")]
    [InlineData("stale-report", "build-report-round-differs")]
    [InlineData("stale-tree", "build-implementation-tree-differs:")]
    [InlineData("report-reference", "build-invocation-missing:")]
    [InlineData("missing-reference", "build-invocation-reference-missing")]
    [InlineData("missing-project", "build-project-coverage-missing:")]
    [InlineData("multiple-projects", "build-project-coverage-missing:")]
    [InlineData("missing-class", "class-not-in-passed-gate:")]
    [InlineData("report-class", "build-report-class-coverage-differs")]
    [InlineData("failed-compiler", "build-compiler-not-successful:")]
    [InlineData("invalid-compiler", "build-invocation-invalid:")]
    [InlineData("unproved-candidate", "build-candidate-provenance-missing:")]
    [InlineData("fixture-equality-only", "build-candidate-provenance-missing:")]
    [InlineData("report-only", "build-invocation-missing:")]
    [InlineData("unrelated-gate", "class-not-in-passed-gate:")]
    [InlineData("non-authoritative-report", "authoritative-build-report-missing")]
    public void InsufficientStoredEvidence_RetainsWaitWithSpecificReason(string change, string reason)
    {
        var fixture = new Records(change: change);
        if (change == "non-authoritative-report")
        {
            Assert.Null(fixture.Developer.LastVerification!.AuthoritativeStandardOutput);
            Assert.Contains($"invocation {fixture.Invocation.Id}", fixture.Developer.LastVerification.StandardOutput);
            Assert.NotEmpty(fixture.Developer.VerificationHistory);
            Assert.All(fixture.Developer.VerificationHistory, item => Assert.Null(item.AuthoritativeStandardOutput));
        }
        var result = fixture.Close(name => change is "unrelated-gate" || change == "missing-class" && name == Records.Classes[0]
            ? null : "attempt gate-1");

        Assert.StartsWith(reason, Assert.Single(result).Value);
        Assert.False(fixture.Wait.IsCompleted);
        Assert.Null(fixture.Wait.Answer);
        Assert.Empty(fixture.Wait.AnswerHistory);
        Assert.Same(fixture.Wait, Assert.Single(fixture.Kernel.HumanInputRequests));
        Assert.Equal(change != "stale-wait", fixture.Wait.IsCurrentForBrief(fixture.Goal.AuthoritativeBrief.Version));
        if (change == "stale-wait")
            Assert.Empty(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
        else
            Assert.Same(fixture.Wait, Assert.Single(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id)));
        Assert.Contains(fixture.Goal.Timeline, item => item.Message.Contains("reason=" + reason, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
    }

    [Theory]
    [InlineData("assertion")]
    [InlineData("live")]
    [InlineData("approval")]
    [InlineData("tester")]
    [InlineData("reviewer")]
    [InlineData("worker")]
    [InlineData("metadata")]
    public void Recognition_DoesNotManufactureBuildAuthority(string change)
    {
        var fixture = new Records(change: change);
        fixture.Close(_ => throw new Exception("Ineligible request queried a gate"));
        Assert.False(fixture.Wait.IsCompleted);
        Assert.Empty(fixture.Wait.AnswerHistory);
    }

    [Fact]
    public void BuildReport_AdmissionAllowsGateProductionWithoutAnsweringWait()
    {
        var fixture = new Records();
        Assert.True(DeveloperReportWait.IsReportWait(fixture.Goal, fixture.Wait));
        Assert.Null(GroupedAdmissionLandingHold.FindBlockingWait([fixture.Wait], fixture.Goal));
        Assert.False(fixture.Wait.IsCompleted);
        Assert.Empty(fixture.Wait.AnswerHistory);
    }

    [Fact]
    public void MissingProvenanceAdapter_RetainsPreciseEvidenceRequest()
    {
        var fixture = new Records();
        var skipped = fixture.Kernel.CloseRoleReportEvidence(fixture.Goal.Id, fixture.Candidate, _ => "attempt gate-1");
        Assert.Equal("build-candidate-provenance-missing:dispatch-id-and-implementation-tree-required", Assert.Single(skipped).Value);
        Assert.False(fixture.Wait.IsCompleted);
    }

    internal sealed class Records
    {
        internal const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        internal const string Needed = "Sanctioned Infrastructure test-project build receipt and exact affected class identities";
        internal static readonly string[] Classes = ["ProjectUseLearnedBranchCliTests", "ReviseAfterRefinementFailureTests",
            "LandedGoalBranchRetirementTests", "HostCapacitySlotOnFirstGitTests", "HostCapacityTestBudgetTests"];
        internal const string HistoricalReport = """
        WORKER_RESULT:
        files: tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProjectUseLearnedBranchCliTests.cs, tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ReviseAfterRefinementFailureTests.cs, tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LandedGoalBranchRetirementTests.cs
        commands: source/helper inspection; baseline preservation comparison passed; git diff --check passed; .//scripts//Invoke-RepoScript.ps1 scripts//Invoke-WorkerBuildCheck.ps1 tests//Mcg.AgentOrchestrator.Infrastructure.Tests//Mcg.AgentOrchestrator.Infrastructure.Tests.csproj — exit 0, invocation a2dc31071353480ca3e840ba5949b412, all scoped compiler/build exits succeeded
        tests: deferred - ProjectUseLearnedBranchCliTests, ReviseAfterRefinementFailureTests, LandedGoalBranchRetirementTests, HostCapacitySlotOnFirstGitTests, HostCapacityTestBudgetTests; Acceptance owns execution
        commit: none
        blockers: none
        assigned_scope_complete: true
        criteria_self_check: [{"criterion_index":0,"status":"not-owned","evidence":"Acceptance owns discovery/execution; Developer declarations, preserved source identities and successful sanctioned build supplied."},{"criterion_index":1,"status":"not-owned","evidence":"Acceptance owns lifecycle execution; three overrides bind to unchanged HostCapacityBoundTestBase.InitializeAsync."},{"criterion_index":2,"status":"not-owned","evidence":"Assigned Reviewer owns independent scenario, cleanup, isolation and identity review."}]
        model_fit: OpenAI/gpt-6.1-sol - adequate - bounded fixture tracing and declaration changes - existing seam supports a minimal patch
        skills: verification-before-completion, dotnet-windows-build-hygiene
        confidence: high
        END_WORKER_RESULT
        """;
        internal const string HistoricalInvocationJson = "{\"Id\":\"a2dc31071353480ca3e840ba5949b412\",\"RequestKey\":\"5851d83ccf154423870cd027f599ee87\",\"GoalId\":\"239b9f5e7c314f25800005e78172dab4\",\"TaskId\":\"0d401ab6eb5945aebcf63a909bcbd7b0\",\"DispatchAt\":\"2026-11-03T12:49:16.0477162-06:00\",\"DispatchId\":\"bdc54f014152a991\",\"WorktreeRoot\":\"C://Users//miles//vcs//mcg-agent-orchestrator//.orchestrator-worktrees//239b9f5e\",\"TreeDigest\":\"wbc1:git-tree:445f730202eb65fc9f17c418b5b67c53b92bb5f2\",\"ScopeIdentity\":\"f7e6b404761daadfc7e33009ddcc688f204ce8deef5e6a6e6102c094b22fb95f\",\"Projects\":[\"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj\"],\"Configuration\":\"Debug\",\"Deadline\":\"2026-11-03T13:03:33.1668162-06:00\",\"Host\":{\"Pid\":5320,\"StartedAt\":\"2026-11-03T12:49:20.7949971-06:00\",\"ImagePath\":\"C://Program Files//dotnet//dotnet.exe\"},\"Worker\":{\"Pid\":12452,\"StartedAt\":\"2026-11-03T12:49:35.2533473-06:00\",\"ImagePath\":\"C://Users//miles//AppData//Local//Programs//PowerShell//7//pwsh.exe\"},\"AdmissionStorePath\":\"C://Users//miles//vcs//mcg-agent-orchestrator//.orchestrator//operator-intents.db\",\"AdmissionVersion\":0,\"Version\":14,\"Observations\":[{\"Phase\":\"Requested\",\"At\":\"2026-11-03T12:51:33.1668227-06:00\"},{\"Phase\":\"Authorized\",\"At\":\"2026-11-03T12:51:33.3768918-06:00\"},{\"Phase\":\"WaitingForCustody\",\"At\":\"2026-11-03T12:51:33.4662548-06:00\"},{\"Phase\":\"CustodyAcquired\",\"At\":\"2026-11-03T12:51:38.2335003-06:00\"},{\"Phase\":\"WaitingForBuildSlot\",\"At\":\"2026-11-03T12:51:38.3494257-06:00\"},{\"Phase\":\"BuildSlotAcquired\",\"At\":\"2026-11-03T12:51:38.5479991-06:00\"},{\"Phase\":\"PreparingLaunch\",\"At\":\"2026-11-03T12:51:49.934701-06:00\"},{\"Phase\":\"ChildRegistered\",\"At\":\"2026-11-03T12:51:50.0161094-06:00\"},{\"Phase\":\"CompilerStarted\",\"At\":\"2026-11-03T12:51:52.1129756-06:00\"},{\"Phase\":\"Completed\",\"At\":\"2026-11-03T12:53:04.2883284-06:00\"}],\"Children\":[{\"Pid\":34668,\"StartedAt\":\"2026-11-03T12:51:49.9732589-06:00\",\"ImagePath\":\"C://Program Files//dotnet//dotnet.exe\"}],\"LeaseOwner\":\"goal-evidence:v1:dispatch:worker-build:5320:a2dc31071353480ca3e840ba5949b412\",\"Outcome\":\"Success\",\"CompilerExitCode\":0,\"Consumed\":true,\"CustodyReleased\":true,\"Diagnostic\":\"Host observed all scoped compiler/build exits successfully.\",\"IsTerminal\":true,\"Phase\":\"Completed\"}";
        internal const string HistoricalTesterJson = "{\"Id\":\"814727958d5a46bab28613a5491069d7\",\"RequestKey\":\"4c63232a0f6c4b1d8caf8398728a6b82\",\"GoalId\":\"239b9f5e7c314f25800005e78172dab4\",\"TaskId\":\"13e5223c1bf3451c911dd56eb056b5c6\",\"DispatchAt\":\"2026-11-03T12:58:00.3178685-06:00\",\"DispatchId\":\"d55d7f7df099856c\",\"WorktreeRoot\":\"C://Users//miles//vcs//mcg-agent-orchestrator//.orchestrator-worktrees//239b9f5e\",\"TreeDigest\":\"wbc1:git-tree:445f730202eb65fc9f17c418b5b67c53b92bb5f2\",\"ScopeIdentity\":\"9db25bcc160f495d6d1fc197fab1d66a705bf06f3367437a4be28a9d7aebc735\",\"Projects\":[\"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj\"],\"Configuration\":\"Debug\",\"Deadline\":\"2026-11-03T13:13:01.1775376-06:00\",\"Host\":{\"Pid\":33100,\"StartedAt\":\"2026-11-03T12:58:43.1234127-06:00\",\"ImagePath\":\"C://Program Files//dotnet//dotnet.exe\"},\"Worker\":{\"Pid\":43256,\"StartedAt\":\"2026-11-03T12:58:48.8352925-06:00\",\"ImagePath\":\"C://Users//miles//AppData//Local//Programs//PowerShell//7//pwsh.exe\"},\"AdmissionStorePath\":\"C://Users//miles//vcs//mcg-agent-orchestrator//.orchestrator//operator-intents.db\",\"AdmissionVersion\":0,\"Version\":15,\"Observations\":[{\"Phase\":\"Requested\",\"At\":\"2026-11-03T13:01:01.1775449-06:00\"},{\"Phase\":\"Authorized\",\"At\":\"2026-11-03T13:01:01.5641196-06:00\"},{\"Phase\":\"WaitingForCustody\",\"At\":\"2026-11-03T13:01:01.7208186-06:00\"},{\"Phase\":\"CustodyAcquired\",\"At\":\"2026-11-03T13:01:08.9107256-06:00\"},{\"Phase\":\"WaitingForBuildSlot\",\"At\":\"2026-11-03T13:01:08.9602567-06:00\"},{\"Phase\":\"WaitingForBuildSlot\",\"At\":\"2026-11-03T13:01:09.248162-06:00\"},{\"Phase\":\"BuildSlotAcquired\",\"At\":\"2026-11-03T13:02:08.1212691-06:00\"},{\"Phase\":\"PreparingLaunch\",\"At\":\"2026-11-03T13:02:27.1904798-06:00\"},{\"Phase\":\"ChildRegistered\",\"At\":\"2026-11-03T13:02:27.3992018-06:00\"},{\"Phase\":\"CompilerStarted\",\"At\":\"2026-11-03T13:02:34.7920255-06:00\"},{\"Phase\":\"Completed\",\"At\":\"2026-11-03T13:02:49.0912295-06:00\"}],\"Children\":[{\"Pid\":57976,\"StartedAt\":\"2026-11-03T13:02:27.3253756-06:00\",\"ImagePath\":\"C://Program Files//dotnet//dotnet.exe\"}],\"LeaseOwner\":\"goal-evidence:v1:dispatch:worker-build:33100:814727958d5a46bab28613a5491069d7\",\"Outcome\":\"Success\",\"CompilerExitCode\":0,\"Consumed\":true,\"CustodyReleased\":true,\"Diagnostic\":\"Host observed all scoped compiler/build exits successfully.\",\"IsTerminal\":true,\"Phase\":\"Completed\"}";
        internal const string HistoricalWaitJson = "{\"Id\":\"4a861cf255434d289b368898ff8ddb13\",\"GoalId\":\"239b9f5e7c314f25800005e78172dab4\",\"TaskId\":\"81b3c569faf04c6d9d966d98833247cf\",\"Question\":\"Planner evidence request for criterion 1: Sanctioned Infrastructure test-project build receipt and exact affected class identities. Availability: post-implementation; produced after the candidate exists. Owner: developer. Reason: The candidate declarations do not exist yet; Developer supplies build evidence and Tester evaluates it.\",\"RequestedAt\":\"2026-11-03T12:49:07.1211954-06:00\",\"Kind\":\"ProspectiveAcceptanceEvidence\",\"IsAutoDefaultable\":false,\"IsDismissible\":false,\"IsAnswerRequired\":true,\"IsExternallyBlocked\":false,\"SuggestedDefaultAnswer\":null,\"ResumeCommand\":\"answer 4a861cf2 <answer>\",\"IsCompleted\":true,\"AnsweredAt\":\"2026-11-03T14:44:48.7414841-06:00\",\"WasDismissed\":false,\"QuestionFingerprint\":\"45aa3cc1fa2ab6899281fb64aa115970348bc0122f88c7d5e32416af727ebf9b\",\"BlockerFingerprint\":\"45aa3cc1fa2ab6899281fb64aa115970348bc0122f88c7d5e32416af727ebf9b\",\"SuppressionCount\":0,\"SupersededByRequestId\":null,\"OperatorGates\":null,\"SuppressionAnswerRevision\":1,\"SuppressionRevision\":0,\"EvidenceOwner\":\"developer\",\"StoreReference\":null,\"TestNames\":null,\"PlannerIdentity\":{\"GoalId\":\"239b9f5e7c314f25800005e78172dab4\",\"RequestingTaskId\":\"81b3c569faf04c6d9d966d98833247cf\",\"BriefVersion\":1,\"CriterionIndex\":1,\"EvidenceKey\":\"w3-worker-build\",\"StableId\":\"9b8bfa08856a504b05e94ef7f3c1a6b9be7814a61720ee3d37f5928272d948fb\"},\"PlannerEvidence\":{\"CriterionIndex\":1,\"EvidenceKey\":\"w3-worker-build\",\"Availability\":\"post-implementation\",\"Owner\":\"developer\",\"Needed\":\"Sanctioned Infrastructure test-project build receipt and exact affected class identities\",\"Reason\":\"The candidate declarations do not exist yet; Developer supplies build evidence and Tester evaluates it.\",\"Store\":null,\"StoreReference\":null,\"Selectors\":null},\"BriefSupersession\":null}";
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal => Kernel.Goals.Single();
        internal TaskSpec Developer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        internal HumanInputRequest Wait => Kernel.HumanInputRequests.Single();
        internal WorkerBuildInvocation Invocation { get; }
        internal string Implementation { get; }
        internal string Candidate { get; }
        internal DeveloperBuildCandidateEvidence Proof { get; set; }

        internal Records(bool historical = false, string change = "", string? implementation = null,
            string? candidate = null, string? tree = null, string? root = null)
        {
            // Historical reconstruction at the recorded pre-answer time. The supplied snapshot is now answered.
            // The proof callback models the existing provenance boundary; real Git adapter coverage is separate.
            var started = DateTimeOffset.Parse("2026-11-03T18:49:16.0477162Z");
            Implementation = implementation ?? (historical ? "640565002810cda17193ea99b7b2fd3ed0a5ed08" : new string('a', 40));
            Candidate = candidate ?? (historical ? "cae4ce619d501148545db8c02dfeb4cb4e5bc4e3" : new string('b', 40));
            var digest = "wbc1:git-tree:" + (tree ?? "445f730202eb65fc9f17c418b5b67c53b92bb5f2");
            var id = historical ? "a2dc31071353480ca3e840ba5949b412" : new string('d', 32);
            var dispatchId = historical ? "bdc54f014152a991" : "fixture-dispatch";
            var kernel = new AgentOrchestratorKernel();
            var developer = new TaskSpec(new TaskId(historical ? "0d401ab6eb5945aebcf63a909bcbd7b0" : new string('e', 32)), "Implement", AgentRole.Developer);
            var planner = new TaskSpec(new TaskId("81b3c569faf04c6d9d966d98833247cf"), "Plan", AgentRole.Planner);
            var goal = kernel.CreateGoal("Stored build report reconstruction", [planner, developer]);
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Build report", ["Developer provides the sanctioned build report and exact declared test identities."],
                VerificationClass.TestVerifiable, [], []));
            if (historical)
            {
                kernel = AgentOrchestratorKernel.FromSnapshot(new([kernel.ExportGoalSnapshot(goal.Id) with { Id = "239b9f5e7c314f25800005e78172dab4" }], []));
                goal = kernel.Goals.Single();
                developer = goal.FindTask(developer.Id);
            }
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord("codex-cli", "implement", root ?? "C:\\fixture", started, BaseCommit: new string('c', 40)));
            kernel.RecordDispatchResultCommit(goal.Id, developer.Id, Implementation);
            var output = RoleReportEvidenceClosureTests.DeveloperOutput("deferred - " + string.Join(", ", Classes))
                .Replace("commands: implement", $"commands: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-WorkerBuildCheck.ps1 {Project} - exit 0, invocation {id}, all scoped compiler/build exits succeeded", StringComparison.Ordinal);
            if (historical) output = HistoricalReport;
            if (change == "report-reference") output = output.Replace(id, new string('f', 32), StringComparison.Ordinal);
            if (change == "missing-reference") output = output.Replace("invocation " + id, "build succeeded", StringComparison.Ordinal);
            if (change == "report-class") output = output.Replace(Classes[0] + ", ", "", StringComparison.Ordinal);
            kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord("implement", root ?? "C:\\fixture", 0,
                output, "", started.AddMinutes(5), StandardOutputPath: "stored-developer-report.out.log", WorkerResultPresent: true,
                HasCommittedChanges: true, DispatchStartedAt: started.AddSeconds(4)));
            var owner = change is "tester" or "reviewer" or "worker" ? change : "developer";
            var needed = change switch { "assertion" => "Implementation succeeds", "live" => "Observe live build", "approval" => "Approve build",
                "multiple-projects" => $"Sanctioned build receipt for `{Project}`, `tests/Other/Other.csproj` and exact affected class identities", _ => Needed };
            var wait = kernel.RequestHumanInputDeduplicated(goal.Id, planner.Id, "Build receipt request",
                kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
                plannerIdentity: new PlannerEvidenceIdentity(goal.Id.Value, planner.Id.Value, change == "stale-wait" ? 2 : 1, 1, "w3-worker-build"),
                plannerEvidence: new PlannerEvidencePayload(1, change == "metadata" ? "different-key" : "w3-worker-build", "post-implementation", owner,
                    needed, "Developer supplies build evidence; Acceptance executes", null, null, [Classes[0]])).Request;
            var process = new WorkerBuildProcess(100, started, "dotnet.exe");
            Invocation = new(id, "request-key", goal.Id.Value, developer.Id.Value, started, dispatchId, root ?? "C:\\fixture", digest,
                "scope", [Project], "Debug", started.AddMinutes(10), process, process, "admission.db", 1, 1,
                [new(WorkerBuildPhase.Requested, started.AddSeconds(10)), new(WorkerBuildPhase.CompilerStarted, started.AddSeconds(20)),
                 new(WorkerBuildPhase.Completed, DateTimeOffset.Parse("2026-11-03T18:53:04Z"))], [process],
                Outcome: WorkerBuildOutcome.Success, CompilerExitCode: 0, Consumed: true, CustodyReleased: true);
            if (historical) Invocation = ReadInvocation(HistoricalInvocationJson);
            Invocation = change switch
            {
                "stale-round" => Invocation with { DispatchAt = started.AddSeconds(-1) },
                "wrong-goal" => Invocation with { GoalId = new string('f', 32) },
                "wrong-task" => Invocation with { TaskId = new string('f', 32) },
                "wrong-dispatch" => Invocation with { DispatchId = "old-dispatch" },
                "stale-tree" => Invocation with { TreeDigest = "wbc1:git-tree:" + new string('f', 40) },
                "missing-project" => Invocation with { Projects = ["tests/Other/Other.csproj"] },
                "failed-compiler" => Invocation with { Outcome = WorkerBuildOutcome.CompilerFailure, CompilerExitCode = 1 },
                "invalid-compiler" => Invocation with { CompilerExitCode = 1 },
                _ => Invocation
            };
            var snapshot = kernel.ExportSnapshot();
            var storedGoal = snapshot.Goals.Single();
            var storedTask = storedGoal.Tasks.Single(task => task.Id == developer.Id.Value);
            storedTask = storedTask with { WorkerBuildInvocations = change is "missing-receipt" or "report-only" ? [] : [Invocation],
                LastDispatch = storedTask.LastDispatch! with { BriefVersion = change == "stale-brief" ? 2 : 1 },
                LastVerification = storedTask.LastVerification! with
                { DispatchAttemptAt = change == "stale-report" ? started.AddSeconds(-1) : null,
                  AuthoritativeStandardOutput = change == "non-authoritative-report" ? null : output,
                  AuthoritativeStandardOutputUnavailableReason = change == "non-authoritative-report" ? "unreadable" : null } };
            // Same-round restoration merges LastVerification with its retained history.
            // Make authority unavailable in both copies while preserving the report preview.
            if (change == "non-authoritative-report")
                storedTask = storedTask with
                {
                    VerificationHistory = storedTask.VerificationHistory!.Select(item => item with
                    {
                        AuthoritativeStandardOutput = null,
                        AuthoritativeStandardOutputUnavailableReason = "unreadable"
                    }).ToArray()
                };
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = [storedGoal with { Tasks = storedGoal.Tasks.Select(task => task.Id == developer.Id.Value ? storedTask : task).ToArray() }],
                HumanInputRequests = snapshot.HumanInputRequests.Select(item => historical ?
                    JsonSerializer.Deserialize<HumanInputRequestSnapshot>(HistoricalWaitJson,
                        new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })! with
                        { IsCompleted = false, AnsweredAt = null, Answer = null, AnswerHistory = [], SuppressionAnswerRevision = 0 }
                    : item).ToArray()
            });
            Proof = new(dispatchId, digest, change is not ("unproved-candidate" or "fixture-equality-only"));
            Assert.Equal(WorkTaskStatus.Completed, Developer.Status);
            Assert.False(Wait.IsCompleted);
        }

        internal IReadOnlyDictionary<HumanInputRequestId, string> Close(Func<string, string?>? gate = null) =>
            Kernel.CloseRoleReportEvidence(Goal.Id, Candidate, gate ?? (_ => "attempt gate-1"), buildCandidateEvidence: _ => Proof);

        internal static WorkerBuildInvocation ReadInvocation(string json) => JsonSerializer.Deserialize<WorkerBuildInvocation>(json,
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
    }
}
