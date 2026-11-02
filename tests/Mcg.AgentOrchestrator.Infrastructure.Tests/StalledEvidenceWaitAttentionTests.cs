using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its kernel and real database in a unique temporary directory.
public sealed class StalledEvidenceWaitAttentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_reviewer_raises_item_with_newest_matching_skip(bool reviewerNewest)
    {
        using var fixture = new Fixture();
        var first = reviewerNewest ? "ROLE_REPORT" : "REVIEWER_VERDICT";
        var last = reviewerNewest ? "REVIEWER_VERDICT" : "ROLE_REPORT";
        fixture.Note($"{first}_EVIDENCE_SKIPPED request={fixture.Wait.Id.Value[..8]} reason=older-reason");
        var newest = $"{last}_EVIDENCE_SKIPPED request={fixture.Wait.Id.Value[..8]} reason=owner-candidate-differs";
        fixture.Note(newest);
        fixture.Note($"{last}_EVIDENCE_SKIPPED request=unrelated reason=wrong-request");

        fixture.Reconcile();

        var item = Assert.Single(await fixture.Open());
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Equal(fixture.Goal.Id.Value, item.GoalId);
        Assert.Equal(StalledEvidenceWaitAttention.Prefix + fixture.Goal.Id.Value, item.CorrelationKey);
        Assert.Equal("evidence wait open after reviewer completed", item.Subject);
        Assert.Contains($"Wait: {fixture.Wait.Id.Value}", item.Body);
        Assert.Contains("Owner: reviewer", item.Body);
        Assert.Contains($"Owner task: {fixture.OwnerTask.Id.Value}", item.Body);
        Assert.Contains($"Last closure attempt: {newest}", item.Body);
        Assert.Contains($"attention answer {fixture.Goal.Id.Value[..8]} {fixture.Wait.Id.Value[..8]} --text-file <path>", item.Body);
        Assert.DoesNotContain("older-reason", item.Body);
        Assert.DoesNotContain("wrong-request", item.Body);
    }

    [Fact]
    public async Task Repeated_reconciliation_keeps_one_open_item()
    {
        using var fixture = new Fixture();
        fixture.Reconcile();
        var first = Assert.Single(await fixture.Open());

        fixture.Reconcile();

        Assert.Equal(first.Id, Assert.Single(await fixture.Open()).Id);
        Assert.Single(await fixture.Store.ListAsync());
    }

    [Fact]
    public async Task Answered_wait_resolves_item_even_while_goal_is_verified()
    {
        using var fixture = new Fixture();
        fixture.Reconcile();
        var first = Assert.Single(await fixture.Open());

        fixture.Kernel.SubmitHumanInput(fixture.Wait.Id, "Reviewer evidence supplied.");
        fixture.SetGoalStatus(GoalStatus.Verified);
        Assert.Empty(fixture.Pending());
        fixture.Reconcile();

        Assert.Empty(await fixture.Open());
        var resolved = Assert.Single(await fixture.Store.ListAsync());
        Assert.Equal(first.Id, resolved.Id);
        Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
    }

    [Fact]
    public async Task Running_reviewer_raises_nothing()
    {
        using var fixture = new Fixture(ownerStatus: WorkTaskStatus.Running);
        Assert.Equal(WorkTaskStatus.Running, fixture.OwnerTask.Status);
        Assert.Same(fixture.Wait, GroupedAdmissionLandingHold.FindBlockingWait(fixture.Pending(), fixture.Goal));

        fixture.Reconcile();

        Assert.Empty(await fixture.Store.ListAsync());
    }

    [Fact]
    public async Task Acceptance_gate_owned_blocking_wait_raises_nothing()
    {
        using var fixture = new Fixture(owner: "acceptance-gate");
        Assert.Same(fixture.Wait, GroupedAdmissionLandingHold.FindBlockingWait(fixture.Pending(), fixture.Goal));

        fixture.Reconcile();

        Assert.Empty(await fixture.Store.ListAsync());
    }

    [Fact]
    public async Task Active_goal_raises_nothing()
    {
        using var fixture = new Fixture();
        fixture.SetGoalStatus(GoalStatus.Active);

        fixture.Reconcile();

        Assert.Empty(await fixture.Store.ListAsync());
    }

    [Theory]
    [InlineData("developer")]
    [InlineData("tester")]
    [InlineData(" REVIEWER ")]
    public async Task Completed_worker_owner_raises_with_normalized_owner(string owner)
    {
        using var fixture = new Fixture(owner: owner);

        fixture.Reconcile();

        var item = Assert.Single(await fixture.Open());
        Assert.Equal($"evidence wait open after {owner.Trim().ToLowerInvariant()} completed", item.Subject);
        Assert.Contains(fixture.OwnerTask.Id.Value, item.Body);
        Assert.Contains("no closure attempt recorded", item.Body);
    }

    [Theory]
    [InlineData("reviewer", "pre-implementation", true)]
    [InlineData("worker", "post-implementation", true)]
    [InlineData("reviewer", "post-implementation", false)]
    public async Task Nonqualifying_owner_availability_or_dispatch_raises_nothing(
        string owner, string availability, bool dispatch)
    {
        using var fixture = new Fixture(owner: owner, availability: availability, dispatch: dispatch);

        fixture.Reconcile();

        Assert.Empty(await fixture.Store.ListAsync());
    }

    [Fact]
    public async Task Newer_running_reviewer_prevents_older_completion_from_raising()
    {
        using var fixture = new Fixture(newerReviewer: true);
        var reviewers = fixture.Goal.Tasks.Where(task => task.RequiredRole == AgentRole.Reviewer).ToArray();
        Assert.Equal(WorkTaskStatus.Completed, reviewers[0].Status);
        Assert.Equal(WorkTaskStatus.Running, reviewers[1].Status);
        Assert.True(reviewers[1].LastDispatch!.DispatchedAt > reviewers[0].LastDispatch!.DispatchedAt);

        fixture.Reconcile();

        Assert.Empty(await fixture.Store.ListAsync());
    }

    [Fact]
    public async Task ReconcileAll_raises_and_clears_after_goal_leaves_verified()
    {
        using var fixture = new Fixture();
        var attention = new StalledEvidenceWaitAttention(fixture.Store, _ => fixture.Pending());
        attention.ReconcileAll([fixture.Goal]);
        var first = Assert.Single(await fixture.Open());

        fixture.SetGoalStatus(GoalStatus.Failed);
        attention.ReconcileAll([fixture.Goal]);

        Assert.Empty(await fixture.Open());
        Assert.Equal(CollaborationItemStatus.Resolved, Assert.Single(await fixture.Store.ListAsync()).Status);
        fixture.SetGoalStatus(GoalStatus.Verified);
        attention.ReconcileAll([fixture.Goal]);
        Assert.NotEqual(first.Id, Assert.Single(await fixture.Open()).Id);
    }

    [Fact]
    public async Task Owner_no_longer_completed_resolves_item()
    {
        using var fixture = new Fixture();
        fixture.Reconcile();
        Assert.Single(await fixture.Open());

        fixture.Kernel.ReportWorkerTaskProgress(fixture.Goal.Id, fixture.OwnerTask.Id,
            WorkTaskStatus.Running, "Owner resumed.");
        fixture.SetGoalStatus(GoalStatus.Verified);
        fixture.Reconcile();

        Assert.Empty(await fixture.Open());
        Assert.Equal(CollaborationItemStatus.Resolved, Assert.Single(await fixture.Store.ListAsync()).Status);
    }

    [Fact]
    public async Task Changed_wait_and_skip_note_refresh_existing_item()
    {
        using var fixture = new Fixture();
        fixture.Reconcile();
        var first = Assert.Single(await fixture.Open());
        var oldWait = fixture.Wait.Id.Value;

        fixture.Kernel.SubmitHumanInput(fixture.Wait.Id, "First evidence supplied.");
        var current = fixture.CreateWait("reviewer", key: "replacement");
        fixture.SetGoalStatus(GoalStatus.Verified);
        var note = $"ROLE_REPORT_EVIDENCE_SKIPPED request={current.Id.Value[..8]} reason=new-reason";
        fixture.Note(note);
        fixture.Reconcile();

        var refreshed = Assert.Single(await fixture.Open());
        Assert.Equal(first.Id, refreshed.Id);
        Assert.Contains(current.Id.Value, refreshed.Body);
        Assert.Contains(note, refreshed.Body);
        Assert.DoesNotContain(oldWait, refreshed.Body);
    }

    [Fact]
    public async Task First_nonqualifying_blocker_is_not_skipped_for_worker_wait()
    {
        using var fixture = new Fixture(owner: "acceptance-gate");
        fixture.CreateWait("reviewer", key: "later-reviewer");
        fixture.SetGoalStatus(GoalStatus.Verified);
        Assert.Equal(fixture.Wait.Id, GroupedAdmissionLandingHold.FindBlockingWait(fixture.Pending(), fixture.Goal)!.Id);

        fixture.Reconcile();

        Assert.Empty(await fixture.Store.ListAsync());
    }

    [Fact]
    public async Task Missing_kernel_keeps_existing_item_open()
    {
        using var fixture = new Fixture();
        fixture.Reconcile();
        var first = Assert.Single(await fixture.Open());

        new StalledEvidenceWaitAttention(fixture.Store, _ => null).Reconcile(fixture.Goal);

        Assert.Equal(first.Id, Assert.Single(await fixture.Open()).Id);
    }

    [Fact]
    public async Task Console_shows_nonanswerable_hold_only_for_verified_goal()
    {
        using var fixture = new Fixture();
        fixture.Reconcile();
        var item = Assert.Single(await fixture.Open());
        var source = new OwnerQuestionReadModel(new OwnerConsoleHarness.FakeState(fixture.Kernel), fixture.Root);

        var question = Assert.Single((await source.ListOpenAsync(CancellationToken.None))
            .Where(question => question.ItemId == item.Id));

        Assert.Equal(OwnerQuestionKind.PassedGateHold, question.Kind);
        Assert.False(question.CanAnswer);
        Assert.Equal(item.Body, question.Text);
        fixture.SetGoalStatus(GoalStatus.Active);
        Assert.DoesNotContain(await source.ListOpenAsync(CancellationToken.None), question => question.ItemId == item.Id);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly DateTimeOffset DispatchedAt = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; private set; }
        internal CollaborationItemStore Store { get; }
        internal TaskSpec OwnerTask => Goal.Tasks.First(task => task.RequiredRole == _role);
        internal HumanInputRequest Wait => Kernel.HumanInputRequests.Single(request => request.Id == _waitId);
        private readonly AgentRole _role;
        private readonly HumanInputRequestId _waitId;

        internal Fixture(string owner = "reviewer", WorkTaskStatus ownerStatus = WorkTaskStatus.Completed,
            string availability = "post-implementation", bool dispatch = true, bool newerReviewer = false)
        {
            _role = owner.Trim().ToLowerInvariant() switch
            {
                "developer" => AgentRole.Developer,
                "tester" => AgentRole.Tester,
                _ => AgentRole.Reviewer
            };
            var roles = new List<AgentRole> { AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer };
            if (newerReviewer) roles.Add(AgentRole.Reviewer);
            Goal = Kernel.CreateGoal("Stalled evidence visibility", roles.Select(role =>
                new TaskSpec(TaskId.New(), $"{role} work", role)).ToArray());
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            if (dispatch)
                Kernel.RecordTaskDispatch(Goal.Id, OwnerTask.Id,
                    new TaskDispatchRecord("codex-cli", "inspect", Root, DispatchedAt));
            Kernel.ReportWorkerTaskProgress(Goal.Id, OwnerTask.Id, ownerStatus, "Owner round state.");
            if (newerReviewer)
                Kernel.RecordTaskDispatch(Goal.Id, Goal.Tasks.Last().Id,
                    new TaskDispatchRecord("codex-cli", "inspect", Root, DispatchedAt.AddSeconds(1)));
            _waitId = CreateWait(owner, availability).Id;
            SetGoalStatus(GoalStatus.Verified);
            Store = CollaborationItemStore.ForDirectory(Root);
            Assert.Equal(GoalStatus.Verified, Goal.Status);
            Assert.False(Wait.IsCompleted);
        }

        internal HumanInputRequest CreateWait(string owner, string availability = "post-implementation", string key = "evidence")
        {
            var planner = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Planner);
            return Kernel.RequestHumanInputDeduplicated(Goal.Id, planner.Id, $"Provide {key}",
                kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
                plannerIdentity: new PlannerEvidenceIdentity(Goal.Id.Value, planner.Id.Value,
                    Goal.AuthoritativeBrief.Version, 1, key),
                plannerEvidence: new PlannerEvidencePayload(1, key, availability, owner,
                    "Role evidence", "Role owns", null, null, null)).Request;
        }

        internal void SetGoalStatus(GoalStatus status)
        {
            var snapshot = Kernel.ExportSnapshot();
            Kernel.ReplaceWithSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with { Status = status }).ToArray()
            });
            Goal = Kernel.GetGoal(Goal.Id);
        }

        internal void Note(string message) => Kernel.RecordTaskNote(Goal.Id, OwnerTask.Id, message);
        internal IReadOnlyList<HumanInputRequest> Pending() => Kernel.GetPendingHumanInput(Goal.Id);
        internal void Reconcile() => StalledEvidenceWaitAttention.Reconcile(Store, Goal, Pending());
        internal async Task<IReadOnlyList<CollaborationItem>> Open() =>
            (await Store.GetAttentionQueueAsync()).Where(item =>
                item.CorrelationKey == StalledEvidenceWaitAttention.Prefix + Goal.Id.Value).ToArray();
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
