using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BoardFillReadyItemSelectorTests
{
    internal static readonly DateTimeOffset Now = new(2030, 1, 2, 0, 0, 0, TimeSpan.Zero);
    internal static BacklogItem Item(char id = 'a') => new(new string(id, 32), "Ready", "Build the receipt",
        BacklogItemStatus.Open, Now, Now, null);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public void Missing_epic_skips_item_until_membership_appears()
    {
        var item = Item();
        var skipped = new List<BacklogItem>();
        Assert.Null(BoardFillReadyItemSelector.Select([item], [], None, Ready,
            epicMembers: None, epicMissing: skipped.Add));
        Assert.Same(item, Assert.Single(skipped));
        skipped.Clear();
        Assert.Same(item, BoardFillReadyItemSelector.Select([item], [], None, Ready,
            epicMembers: new HashSet<string> { item.Id }, epicMissing: skipped.Add));
        Assert.Empty(skipped);
    }

    [Fact]
    public void Epic_question_only_covers_otherwise_eligible_items()
    {
        var ready = Item();
        var gated = Item('b') with { Tags = "owner-gated" };
        var blocked = Item('c');
        var skipped = new List<BacklogItem>();
        Assert.Null(BoardFillReadyItemSelector.Select([ready, gated, blocked], [], None,
            item => new(item.Id != blocked.Id, false, ""), epicMembers: None, epicMissing: skipped.Add));
        Assert.Same(ready, Assert.Single(skipped));
    }

    [Theory]
    [InlineData(BacklogItemStatus.Open, true)]
    [InlineData(BacklogItemStatus.Done, false)]
    [InlineData(BacklogItemStatus.Superseded, false)]
    public void Only_open_items_are_eligible(BacklogItemStatus status, bool eligible)
    {
        var item = Item() with { Status = status };
        Assert.Equal(eligible, Select([item]) is not null);
    }

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Superseded)]
    public void Terminal_links_allow_selection_but_live_links_block(GoalStatus status)
    {
        var item = Item();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Already linked");
        kernel.SetGoalSourceBacklogItemLink(goal.Id, item.Id, SourceBacklogCoverage.Slice);
        Assert.Null(Select([item], kernel.Goals.ToArray()));
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = snapshot.Goals.Select(row => row with { Status = status }).ToArray() });
        Assert.Same(item, Select([item], kernel.Goals.ToArray()));
        Assert.Null(Select([item with { Tags = "owner-gated" }], kernel.Goals.ToArray()));
        Assert.Null(Select([item with { Tags = "author-refused" }], kernel.Goals.ToArray()));
        var live = kernel.CreateGoal("Live sibling");
        kernel.SetGoalSourceBacklogItemLink(live.Id, item.Id, SourceBacklogCoverage.Slice);
        Assert.Null(Select([item], kernel.Goals.ToArray()));
    }

    [Theory]
    [InlineData(BacklogDependencyTargetKind.Backlog)]
    [InlineData(BacklogDependencyTargetKind.Goal)]
    public void Unsatisfied_dependencies_of_both_kinds_block(BacklogDependencyTargetKind kind)
    {
        var prerequisite = Item('b');
        var goal = new AgentOrchestratorKernel().CreateGoal("Prerequisite");
        var item = Item() with { Dependencies = [new(Item().Id,
            kind == BacklogDependencyTargetKind.Backlog ? prerequisite.Id : goal.Id.Value, kind, Now)] };
        var readiness = BacklogDependencyReadiness.Evaluate(item, _ => goal, _ => prerequisite,
            _ => new(goal), _ => "Running");
        Assert.Null(BoardFillReadyItemSelector.Select([item], [goal], None, _ => readiness));
        Assert.Same(item, BoardFillReadyItemSelector.Select([item], [goal], None, _ =>
            BacklogDependencyReadiness.Evaluate(item, _ => goal, _ => prerequisite, _ => new(goal), _ => "Merged")));
    }

    [Fact]
    public void Owner_gated_is_a_case_insensitive_tag_token()
    {
        Assert.Null(Select([Item() with { Tags = "ready, OWNER-GATED\tother" }]));
        Assert.NotNull(Select([Item() with { Tags = "not-owner-gated" }]));
    }

    [Theory]
    [InlineData("author-refused")]
    [InlineData("ready, AUTHOR-REFUSED\tother")]
    public void Refusal_tag_blocks_newer_stamp_until_removed(string tags)
    {
        // Parallel-safe: private SQLite history and fixed timestamps, no shared state.
        var root = Path.Combine(Path.GetTempPath(), "board-fill-refused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = Item();
            var store = new ConductorBoardFillDraftStore(Path.Combine(root, "drafts.db"));
            var round = store.Begin(original, Now);
            store.Finish(round, new("stale", 2, new string('c', 40), null, null, []), Now.AddMinutes(1));
            var item = original with { Tags = tags, Notes = [new(Now.AddMinutes(2), "Author refusal note")] };
            var rounds = store.ReadAll();
            Assert.True(BoardFillReadyItemSelector.ChangeStamp(item) > Assert.Single(rounds).ChangeStamp);
            Assert.Empty(store.AlreadyDrafted([item]));
            Assert.Null(BoardFillReadyItemSelector.Select([item], [], store.AlreadyDrafted([item]), Ready, rounds));

            item = item with { Tags = "ready,not-author-refused" };
            Assert.Same(item, BoardFillReadyItemSelector.Select([item], [], store.AlreadyDrafted([item]), Ready, rounds));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("OwNeR DeCiSiOn")]
    [InlineData("OwNeR ApPrOvAl")]
    [InlineData("OwNeR RuLiNg")]
    [InlineData("ApPrOvE-PoLiCy-ChAnGe")]
    [InlineData("DeCiSiOn FoR YoU")]
    public void Each_owner_marker_blocks_body_and_notes(string marker)
    {
        Assert.Null(Select([Item() with { Body = "Needs " + marker + " here" }]));
        Assert.Null(Select([Item() with { Notes = [new(Now, "Needs " + marker + " here")] }]));
        Assert.NotNull(Select([Item()]));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("stale")]
    public void Unchanged_verdict_suppresses_until_body_or_note_changes(string kind)
    {
        // Unique SQLite file, independent of every other test and the live stores.
        var root = Path.Combine(Path.GetTempPath(), "board-fill-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var item = Item() with { Notes = [new(Now.AddMinutes(1), "Original note")] };
            var store = new ConductorBoardFillDraftStore(Path.Combine(root, "drafts.db"));
            var round = store.Begin(item, Now);
            store.Finish(round, new(kind, kind == "stale" ? 2 : 0, new string('c', 40), null, null, []), Now.AddMinutes(2));
            Assert.Null(BoardFillReadyItemSelector.Select([item], [], store.AlreadyDrafted([item]), Ready));
            var edited = item with { UpdatedAt = Now.AddMinutes(3) };
            Assert.Same(edited, BoardFillReadyItemSelector.Select([edited], [], store.AlreadyDrafted([edited]), Ready));
            var noted = item with { Notes = [.. item.Notes, new(Now.AddMinutes(3), "New note")] };
            Assert.Same(noted, BoardFillReadyItemSelector.Select([noted], [], store.AlreadyDrafted([noted]), Ready));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Ordering_applies_priority_landed_dependency_recency_then_ordinal_id()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("Landed dependency");
        var landed = Item('c') with { Priority = "normal", Dependencies = [new(Item('c').Id, goal.Id.Value,
            BacklogDependencyTargetKind.Goal, Now)] };
        var tie = landed with { Id = new string('b', 32) };
        var older = landed with { Id = new string('a', 32), UpdatedAt = Now.AddDays(-1) };
        var noDependency = Item('d') with { Priority = "high", UpdatedAt = Now.AddDays(1) };
        var noPriority = landed with { Id = new string('e', 32), Priority = null, UpdatedAt = Now.AddDays(2) };
        var blankPriority = noPriority with { Id = new string('f', 32), Priority = " " };
        var remaining = new List<BacklogItem> { blankPriority, noPriority, noDependency, older, landed, tie };
        foreach (var expected in new[] { tie, landed, older, noDependency, noPriority, blankPriority })
        {
            var selected = BoardFillReadyItemSelector.Select(remaining, [goal], None,
                item => BacklogDependencyReadiness.Evaluate(item, _ => goal, _ => null, _ => new(null), _ => "Recorded"));
            Assert.Same(expected, selected);
            remaining.Remove(expected);
        }
        Assert.Null(Select([]));
    }

    [Fact]
    public async Task Marker_store_write_preserves_stamp_and_round_currency()
    {
        // Parallel-safe: private real stores exercise the note's UpdatedAt side effect.
        var root = Path.Combine(Path.GetTempPath(), "board-fill-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var backlog = new BacklogStore(Path.Combine(root, "backlog.db"));
            var item = await backlog.AddAsync("Ready", "Build the receipt");
            var stamp = BoardFillReadyItemSelector.ChangeStamp(item);
            var drafts = new ConductorBoardFillDraftStore(Path.Combine(root, "drafts.db"));
            var round = drafts.Begin(item, Now);
            drafts.Finish(round, new("draft", 0, BoardFillAssessmentTestFixture.Head, null, null, []), Now);
            await backlog.AppendNoteAsync(item.Id, Marker(stamp));
            var current = Assert.IsType<BacklogItem>(await backlog.GetByExactIdAsync(item.Id));

            Assert.Equal(Assert.Single(current.Notes).CreatedAt, current.UpdatedAt);
            Assert.Equal(stamp, BoardFillReadyItemSelector.ChangeStamp(current));
            Assert.Contains(item.Id, drafts.AlreadyDrafted([current]));
            var assessment = BoardFillFileability.Evaluate(Assert.Single(drafts.ReadAll()),
                BoardFillAssessmentTestFixture.Brief, BoardFillAssessmentTestFixture.Verified,
                BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief),
                BoardFillAssessmentTestFixture.NoDepends, current, [], Ready);
            Assert.DoesNotContain("backlog-changed", assessment.FailingReasons);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Marker_note_does_not_advance_change_stamp()
    {
        var item = HeldItem();
        Assert.Equal(Now, BoardFillReadyItemSelector.ChangeStamp(item));
        var withPriorNote = item with { Notes = [new(Now.AddSeconds(30), "Operator context"), .. item.Notes] };
        Assert.Equal(Now.AddSeconds(30), BoardFillReadyItemSelector.ChangeStamp(withPriorNote));
    }

    [Fact]
    public void Active_marker_blocks_unchanged_item_before_readiness()
    {
        var item = HeldItem();
        Assert.Null(BoardFillReadyItemSelector.Select([item], [], None,
            _ => throw new InvalidOperationException("Held items must not reach readiness"),
            currentMainHead: BoardFillAssessmentTestFixture.Head));
    }

    [Fact]
    public void Item_edit_after_marker_releases_hold()
    {
        var item = HeldItem() with { Body = "Corrected claim", UpdatedAt = Now.AddMinutes(2) };
        Assert.Same(item, Select([item]));
        Assert.Equal(item.UpdatedAt, BoardFillReadyItemSelector.ChangeStamp(item));
    }

    [Fact]
    public void Plain_note_after_marker_releases_hold_and_advances_stamp()
    {
        var held = HeldItem();
        var item = held with { Notes = [.. held.Notes, new(Now.AddMinutes(2), "Corrected by operator")] };
        Assert.Equal(Now.AddMinutes(2), BoardFillReadyItemSelector.ChangeStamp(item));
        Assert.Same(item, Select([item]));
    }

    [Fact]
    public void Changed_cited_file_releases_hold()
    {
        var item = HeldItem();
        var calls = 0;
        Assert.Same(item, BoardFillReadyItemSelector.Select([item], [], None, Ready,
            currentMainHead: new string('d', 40), changedFiles: (from, to) =>
            {
                calls++;
                Assert.Equal(BoardFillAssessmentTestFixture.Head, from);
                Assert.Equal(new string('d', 40), to);
                return ["SRC\\Feature\\File.cs"];
            }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Changed_uncited_file_keeps_hold()
    {
        var calls = 0;
        Assert.Null(BoardFillReadyItemSelector.Select([HeldItem()], [], None, Ready,
            currentMainHead: new string('d', 40), changedFiles: (_, _) =>
            {
                calls++;
                return ["src/Other.cs"];
            }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Unavailable_changed_file_list_keeps_hold()
    {
        var calls = 0;
        Assert.Null(BoardFillReadyItemSelector.Select([HeldItem()], [], None, Ready,
            currentMainHead: new string('d', 40), changedFiles: (_, _) => { calls++; return null; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Throwing_changed_files_keeps_hold_without_escaping()
    {
        var calls = 0;
        Assert.Null(BoardFillReadyItemSelector.Select([HeldItem()], [], None, Ready,
            currentMainHead: new string('d', 40), changedFiles: (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("diff failed");
            }));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(BoardFillAssessmentTestFixture.Head)]
    public void Unknown_or_unchanged_head_keeps_hold_without_diff(string? head)
    {
        var calls = 0;
        Assert.Null(BoardFillReadyItemSelector.Select([HeldItem()], [], None, Ready,
            currentMainHead: head, changedFiles: (_, _) => { calls++; return ["src/Feature/File.cs"]; }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Newest_marker_supersedes_older_release()
    {
        var held = HeldItem();
        var item = held with { UpdatedAt = Now.AddMinutes(3), Notes = [.. held.Notes,
            new(Now.AddMinutes(2), "Operator correction"),
            new(Now.AddMinutes(3), Marker(Now.AddMinutes(2)))] };
        Assert.Equal(Now.AddMinutes(2), BoardFillReadyItemSelector.ChangeStamp(item));
        Assert.Null(Select([item]));
    }

    [Fact]
    public void Malformed_marker_holds_until_plain_note()
    {
        var held = Item() with { UpdatedAt = Now.AddMinutes(1),
            Notes = [new(Now.AddMinutes(1), BoardFillContradictionHold.Prefix + "\nbroken")] };
        Assert.Null(BoardFillReadyItemSelector.Select([held], [], None, Ready,
            currentMainHead: new string('d', 40), changedFiles: (_, _) => ["src/Feature/File.cs"]));
        var released = held with { Notes = [.. held.Notes, new(Now.AddMinutes(2), "Operator correction")] };
        Assert.Same(released, Select([released]));
    }

    [Fact]
    public void Owner_phrase_in_marker_claim_does_not_owner_gate()
    {
        var held = HeldItem();
        var note = Assert.Single(held.Notes);
        var item = held with { Notes = [note with { Text = note.Text + "\nNeeds owner decision" }] };
        Assert.False(BoardFillReadyItemSelector.IsOwnerGated(item));
        Assert.Same(item, BoardFillReadyItemSelector.Select([item], [], None, Ready,
            currentMainHead: new string('d', 40), changedFiles: (_, _) => ["src/Feature/File.cs"]));
    }

    private static string Marker(DateTimeOffset stamp) => BoardFillContradictionHold.Format(
        BoardFillAssessmentTestFixture.Head, stamp,
        BoardFillVerifierContract.Premise(BoardFillAssessmentTestFixture.Brief),
        [new(1, "contradicted", "src/Feature/File.cs:17")]);

    private static BacklogItem HeldItem() => Item() with { UpdatedAt = Now.AddMinutes(1),
        Notes = [new(Now.AddMinutes(1), Marker(Now))] };

    private static BacklogReadiness Ready(BacklogItem item) =>
        BacklogDependencyReadiness.Evaluate(item, _ => null, _ => null, _ => new(null), _ => "Running");
    private static BacklogItem? Select(IReadOnlyList<BacklogItem> items, IReadOnlyList<Goal>? goals = null) =>
        BoardFillReadyItemSelector.Select(items, goals ?? [], None, Ready);
}
