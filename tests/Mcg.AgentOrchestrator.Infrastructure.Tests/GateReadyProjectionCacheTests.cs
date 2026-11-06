using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private caches/kernels/stores, injected refs and projectors; no git or processes.
public sealed class GateReadyProjectionCacheTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    private static readonly GateReadyProjectionCache.Heads Heads = new(new string('b', 40), new string('a', 40), new string('a', 40));

    [Fact]
    public void HeldGoalObservations_DoNotInvalidateDeterministicExclusion()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Held pair member");
        var cache = new GateReadyProjectionCache();
        GateReadyProjectionCache.Key Key() => GateReadyProjectionCache.CreateKey(goal,
            ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None);
        var key = Key();
        var calls = 0;
        GateReadyCandidateProjectionResult Project()
        {
            calls++;
            return new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.MergeConflict);
        }
        var first = cache.GetOrProject(goal.Id, key, Project, Key);
        kernel.RecordGoalPolicyDecision(goal.Id, "Approval hold observed again");
        goal.ObserveLandingBlocker(new("approval", "Waiting for owner", "same-candidate",
            DateTimeOffset.Parse("2026-11-05T18:30:00Z"), "owner", "approve-policy-change"));
        Assert.Equal(key, Key());
        Assert.Same(first, cache.GetOrProject(goal.Id, Key(), Project, Key));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Driver_UnchangedKey_ReusesEntireInnerProjectionAcrossCalls()
    {
        var goal = VerifiedGoal();
        var gateCalls = 0;
        var riskCalls = 0;
        var scopeCalls = 0;
        var driver = MakeDriver(isVerificationGateSatisfied: _ => { gateCalls++; return true; },
            classifyRisk: _ => { riskCalls++; return ChangeRiskTier.Behavior; },
            gateReadyCandidateProjector: Projector(_ => { scopeCalls++; return ["scripts/example.ps1"]; }));
        driver.GateReadyProjectionHeadsReader = _ => Heads;

        var first = driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, null);
        var second = driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, null);

        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(first);
        Assert.Same(first, second);
        Assert.Equal(1, gateCalls);
        Assert.Equal(1, riskCalls);
        Assert.Equal(1, scopeCalls);
        Assert.Equal(1, driver.ProjectionCache.Count);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("main")]
    [InlineData("execution-head")]
    [InlineData("state")]
    [InlineData("policy")]
    [InlineData("mutated-policy")]
    [InlineData("facts")]
    public void Key_ChangedDependency_RecomputesAndReplacesLatestEntry(string change)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Key dependencies");
        var policyMap = new Dictionary<GoalLifecycleState, ConductorTransitionDecision>(ConductorAutonomyPolicy.Permissive.TransitionMap);
        var policy = ConductorAutonomyPolicy.Permissive with { TransitionMap = policyMap, AutoPromoteRiskThreshold = null };
        var heads = Heads;
        var facts = GoalLifecycleFacts.None;
        var cache = new GateReadyProjectionCache();
        var key = GateReadyProjectionCache.CreateKey(goal, policy, heads, facts);
        var calls = 0;
        GateReadyCandidateProjectionResult Project() { calls++; return Ready(goal.Id, heads); }
        cache.GetOrProject(goal.Id, key, Project, () => key);
        switch (change)
        {
            case "branch": heads = heads with { BranchRevision = new string('c', 40) }; break;
            case "main": heads = heads with { MainRevision = new string('d', 40) }; break;
            case "execution-head": heads = heads with { ExecutionHeadRevision = new string('e', 40) }; break;
            case "state": kernel.CancelGoal(goal.Id, "State version changes"); break;
            case "policy": policy = ConductorAutonomyPolicy.Manual; break;
            case "mutated-policy": policyMap[GoalLifecycleState.Merged] = ConductorTransitionDecision.Escalate; break;
            case "facts": facts = facts with { HasOpenClarification = true }; break;
        }
        var changed = GateReadyProjectionCache.CreateKey(goal, policy, heads, facts);
        Assert.NotEqual(key, changed);
        key = changed;
        cache.GetOrProject(goal.Id, key, Project, () => key);
        cache.GetOrProject(goal.Id, key, Project, () => key);
        Assert.Equal(2, calls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Driver_LiveLifecycleFacts_InvalidateWithoutGoalOrHeadChange()
    {
        var goal = VerifiedGoal();
        var facts = GoalLifecycleFacts.None;
        var driver = MakeDriver(getFacts: _ => facts, classifyRisk: _ => ChangeRiskTier.Behavior,
            isVerificationGateSatisfied: _ => true, gateReadyCandidateProjector: Projector(_ => ["scripts/example.ps1"]));
        driver.GateReadyProjectionHeadsReader = _ => Heads;
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, null));
        facts = facts with { HasOpenClarification = true };
        var excluded = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
            driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, null));
        Assert.Equal(GateReadyCandidateExclusionReason.LifecycleNotReady, excluded.Reason);
        Assert.Equal(0, driver.ProjectionCache.Count);
    }

    [Fact]
    public void PendingHumanInput_BetweenTicks_ChangesOuterResultOnHit()
    {
        var goal = VerifiedGoal();
        IReadOnlyList<HumanInputRequest> pending = [];
        var cache = new GateReadyProjectionCache();
        var key = GateReadyProjectionCache.CreateKey(goal, ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None);
        var calls = 0;
        GateReadyCandidateProjectionResult Project()
        {
            var inner = cache.GetOrProject(goal.Id, key, () => { calls++; return Ready(goal.Id, Heads); }, () => key);
            return GroupedAdmissionCandidateProjection.Project(inner, goal, pending, null);
        }
        var firstTick = new ConductorTickStepLedger(() => TimeSpan.Zero);
        using (firstTick.Activate()) Assert.IsType<GateReadyCandidateProjectionResult.Ready>(Project());
        var stateBeforeWait = GateReadyProjectionCache.CreateKey(goal, ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None);
        var wait = new HumanInputRequest(HumanInputRequestId.New(), goal.Id, null, "Owner evidence needed",
            DateTimeOffset.Parse("2026-10-01T12:00:00Z"), kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: "operator");
        pending = [wait];
        // Pending evidence lives outside the goal snapshot and must not be hidden by a hit.
        Assert.Equal(stateBeforeWait, GateReadyProjectionCache.CreateKey(goal, ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None));
        var secondTick = new ConductorTickStepLedger(() => TimeSpan.Zero);
        using (secondTick.Activate())
        {
            var held = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(Project());
            Assert.Equal(GateReadyCandidateExclusionReason.LandingHeld, held.Reason);
            Assert.Equal(wait.Id.Value, held.HoldRequestId);
        }
        Assert.Equal(1, calls);
        Assert.Contains("prep_projection_cache=hits:0,misses:1", firstTick.FormatAdmission(TimeSpan.Zero));
        Assert.Contains("prep_projection_cache=hits:1,misses:0", secondTick.FormatAdmission(TimeSpan.Zero));
    }

    [Fact]
    public void OwnerReview_JournalAndApprovalRemainLiveOnWouldBeHit()
    {
        var root = CreateTempDirectory("projection-owner-hold");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Owner review");
            string[] checks = ["owner-protected configuration"];
            kernel.RecordAcceptanceFailure(goal.Id, checks, Heads.BranchRevision, Heads.MainRevision);
            var decisions = CollaborationItemStore.ForDirectory(Path.Combine(root, ".orchestrator"));
            var calls = 0;
            var driver = MakeDriver(executionDirectory: root, classifyRisk: _ => ChangeRiskTier.Behavior,
                isVerificationGateSatisfied: _ => true, resolveAcceptanceHeads: _ => (Heads.BranchRevision, Heads.MainRevision),
                gateReadyCandidateProjector: Projector(_ => { calls++; return []; }));
            driver.OverrideOwnerReviewHoldForTests(decisions, (_, _) => { }, (_, _) => null);
            driver.GateReadyProjectionHeadsReader = _ => Heads;
            var before = driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, null);
            Assert.Equal(GateReadyCandidateExclusionReason.ScopeEmpty, Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(before).Reason);
            var key = GateReadyProjectionCache.CreateKey(goal, ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None);
            GoalOperationJournal.OwnerReviewHoldEntered(root, goal, Heads.BranchRevision, Heads.MainRevision,
                new OwnerReviewHoldReceipt(null, goal.LatestAcceptanceFailure!.OccurredAt, "Owner must approve"), checks);
            Assert.Equal(GateReadyCandidateExclusionReason.OwnerReviewHold, Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
                driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, null)).Reason);
            var now = DateTimeOffset.Parse("2026-10-01T12:01:00Z");
            var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                OperatorIntentVerbs.ApprovePolicyChange, goal.Id.Value, null, "{}", [], "owner", "cli", "local-process", now,
                ActorKind: OperatorActorKind.Human);
            OperatorIntentPolicyApproval.Apply(decisions, goal, intent,
                new ApprovePolicyChangeOperatorIntentPayload(Heads.BranchRevision, "Reviewed"), now, null);
            Assert.Equal(key, GateReadyProjectionCache.CreateKey(goal, ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None));
            Assert.Same(before, driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, null));
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Retain_EvictsIneligibleGoal_AndReentryRecomputesWithoutReordering()
    {
        var first = VerifiedGoal();
        var second = VerifiedGoal();
        var cache = new GateReadyProjectionCache();
        var calls = 0;
        void Project(Goal goal)
        {
            var key = GateReadyProjectionCache.CreateKey(goal, ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None);
            cache.GetOrProject(goal.Id, key, () => { calls++; return Ready(goal.Id, Heads); }, () => key);
        }
        Project(first);
        Project(second);
        Assert.Equal(2, cache.Count);
        Goal[] ordered = [second];
        Assert.Same(ordered, cache.Retain([second], ordered));
        Assert.Equal(1, cache.Count);
        Project(second);
        Project(first);
        Assert.Equal(3, calls);
        Assert.Empty(cache.Retain([], Array.Empty<Goal>()));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void RecordedGoalsAndHeads_CachedAdmissionEqualsUncachedInSameOrder()
    {
        var goals = Enumerable.Range(1, 4).Select(_ => VerifiedGoal()).ToArray();
        var revisions = goals.Select((goal, index) => (goal.Id, Heads with { BranchRevision = (index + 10).ToString("x40") }))
            .ToDictionary(pair => pair.Id, pair => pair.Item2);
        var driver = MakeDriver(classifyRisk: _ => ChangeRiskTier.Behavior, isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: new GateReadyCandidateProjector(
                id => new(revisions[id].BranchRevision, revisions[id].MainRevision),
                id => new(true, id == goals[2].Id ? [] : id == goals[0].Id ? ["scripts/example.ps1"] : ["config/example.json"]),
                (id, _, _) => new(id != goals[3].Id, id == goals[3].Id ? ["src/conflict.cs"] : [])));
        driver.GateReadyProjectionHeadsReader = goal => revisions[goal.Id];
        var baseline = goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id,
            driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive))).ToArray();
        ConductorSpeculativeAcceptanceCandidate[] Cached() => goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id,
            driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, null))).ToArray();
        Cached();
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
        ConductorSpeculativeAcceptanceCandidate[] cached;
        using (ledger.Activate()) cached = Cached();
        Assert.Equal(baseline.Select(Render), cached.Select(Render));
        Assert.Equal(ConductorSpeculativeAcceptanceCohortPlanner.Plan(baseline).FormatReceipt(1),
            ConductorSpeculativeAcceptanceCohortPlanner.Plan(cached).FormatReceipt(1));
        Assert.Equal(ConductorAcceptanceCohortSelector.Select(baseline).Selection!.Members.Select(member => member.GoalId),
            ConductorAcceptanceCohortSelector.Select(cached).Selection!.Members.Select(member => member.GoalId));
        Assert.Contains("prep_projection_cache=hits:4,misses:0", ledger.FormatAdmission(TimeSpan.Zero));

        static string Render(ConductorSpeculativeAcceptanceCandidate candidate) =>
            JsonSerializer.Serialize(candidate.ProjectionResult, candidate.ProjectionResult.GetType());
    }

    [Fact]
    public void ProjectionMovingDuringCompute_IsReturnedButNeverStoredUnderOldKey()
    {
        var goal = VerifiedGoal();
        var cache = new GateReadyProjectionCache();
        var key = GateReadyProjectionCache.CreateKey(goal, ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None);
        var changed = key with { Revisions = Heads with { ExecutionHeadRevision = new string('c', 40) } };
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(cache.GetOrProject(goal.Id, key,
            () => Ready(goal.Id, Heads), () => changed));
        Assert.Equal(0, cache.Count);
    }

    [Theory]
    [InlineData("RevisionUnknown")]
    [InlineData("RevisionStale")]
    [InlineData("ScopeResolutionFailed")]
    [InlineData("MergeIndeterminate")]
    [InlineData("RiskUnknown")]
    [InlineData("GateNotReady")]
    [InlineData("LifecycleNotReady")]
    public void TransientExclusion_IsRetried(string reasonName)
    {
        var reason = Enum.Parse<GateReadyCandidateExclusionReason>(reasonName);
        var goal = VerifiedGoal();
        var cache = new GateReadyProjectionCache();
        var key = GateReadyProjectionCache.CreateKey(goal, ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None);
        cache.GetOrProject(goal.Id, key, () => new GateReadyCandidateProjectionResult.Excluded(reason), () => key);
        Assert.Equal(0, cache.Count);
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(cache.GetOrProject(goal.Id, key, () => Ready(goal.Id, Heads), () => key));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void AdmissionPhaseLine_EmitsCacheCounts_AndPrunesGoalsOutsideEligibility()
    {
        var (kernel, goal) = SimpleGoal();
        var driver = MakeDriver();
        var key = GateReadyProjectionCache.CreateKey(goal, ConductorAutonomyPolicy.Permissive, Heads, GoalLifecycleFacts.None);
        driver.ProjectionCache.GetOrProject(goal.Id, key, () => Ready(goal.Id, Heads), () => key);
        kernel.CancelGoal(goal.Id, "Leave the eligible set");
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Keep admission ticking");
        var loop = new ConductorBatchLoop(processCpuTime: () => TimeSpan.Zero, janitorialTimestamp: () => 0);
        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        var line = ConductorBatchLoopTestsStepLedger.Phase(tick, "acceptance-admission");
        Assert.Contains(" prep_projection_cache=hits:0,misses:0", line);
        Assert.Equal(0, driver.ProjectionCache.Count);
    }

    private static Goal VerifiedGoal() => CreateVerifiedSimpleGoal(new AgentOrchestratorKernel(), "Recorded projection candidate");

    private static GateReadyCandidateProjector Projector(Func<GoalId, IReadOnlyList<string>> paths) => new(
        _ => new(Heads.BranchRevision, Heads.MainRevision), id => new(true, paths(id)), (_, _, _) => new(true));

    private static GateReadyCandidateProjectionResult.Ready Ready(GoalId id, GateReadyProjectionCache.Heads heads) => new(new(
        id, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, [$"src/{id.Value}.cs"], [$"resource:{id.Value}"],
        new GateReadyMergeEvidence(heads.BranchRevision, heads.MainRevision, GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected)));
}
