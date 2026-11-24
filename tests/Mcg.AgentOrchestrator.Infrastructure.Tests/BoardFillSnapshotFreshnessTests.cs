using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static BoardFillSnapshotTestDatabase;

public sealed class BoardFillSnapshotFreshnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scoped_decision_matches_full_read_and_keeps_only_required_closed_facts(bool legacy)
    {
        using var database = BoardFillSchedulingComparison.Create(32, 9);
        if (legacy)
        {
            using var connection = database.Open();
            BoardFillSnapshotTestDatabase.RemoveSnapshotProof(connection);
            BoardFillSnapshotTestDatabase.Execute(connection, "ALTER TABLE backlog DROP COLUMN pinned_at");
        }

        var fileBefore = File.ReadAllBytes(database.Path);
        var initial = Compare();
        Assert.Equal(BoardFillSchedulingComparison.Dependent, initial.Selected);
        Assert.Equal([BoardFillSchedulingComparison.Dependent, BoardFillSchedulingComparison.Alternate], initial.Preferred);
        Assert.Equal(BoardFillSchedulingComparison.Historical, Assert.Single(initial.Duplicates).Reference.Id);
        Assert.NotNull(Assert.Single(initial.Duplicates).Annotation);
        Assert.Contains(initial.Holds, hold => hold.Id == BoardFillSchedulingComparison.Held && hold.Contradicted);
        Assert.Contains(initial.Candidates, row => row.Id == "item-000006" &&
            row.Readiness.Suffix == " [Blocked: waiting on open prerequisite missing-]");
        Assert.Contains("item-000007", initial.MissingMembers);
        Assert.Equal(fileBefore, File.ReadAllBytes(database.Path));

        var snapshot = BoardFillSchedulingComparison.ReadProjection(database.Path);
        Assert.All(snapshot.Candidates, item => Assert.Equal(BacklogItemStatus.Open, item.Status));
        Assert.Equal(9, snapshot.Candidates.Count);
        Assert.Equal(BacklogItemStatus.Done, snapshot.Statuses[BoardFillSchedulingComparison.Prerequisite]);
        Assert.Equal(BacklogItemStatus.Superseded, snapshot.Statuses[BoardFillSchedulingComparison.ClosedPlanSource]);
        Assert.False(snapshot.Statuses.ContainsKey(BoardFillSchedulingComparison.Missing));
        Assert.False(snapshot.Statuses.ContainsKey(BoardFillSchedulingComparison.Irrelevant));
        Assert.Equal(BoardFillSchedulingComparison.Historical, Assert.Single(snapshot.RecentSources).Id);

        BoardFillSchedulingComparison.SetNote(database, BoardFillSchedulingComparison.Duplicate,
            Assert.Single(initial.Duplicates).Annotation!);
        Assert.Null(Assert.Single(Compare().Duplicates).Annotation);

        database.Run($"UPDATE backlog SET status = 'Superseded' WHERE id = '{BoardFillSchedulingComparison.Prerequisite}'");
        var changedPrerequisite = Compare();
        Assert.Equal(BoardFillSchedulingComparison.Alternate, changedPrerequisite.Selected);
        Assert.False(Assert.Single(changedPrerequisite.Candidates,
            row => row.Id == BoardFillSchedulingComparison.Dependent).Readiness.Eligible);

        BoardFillSchedulingComparison.SetNote(database, BoardFillSchedulingComparison.Held, "Updated marker evidence");
        Assert.Contains(Compare().Holds, hold => hold.Id == BoardFillSchedulingComparison.Held && !hold.Contradicted);
        database.Run($"UPDATE backlog SET body = 'See src/Other.cs' WHERE id = '{BoardFillSchedulingComparison.Historical}'");
        Assert.Empty(Compare().Duplicates);

        BoardFillSchedulingComparison.Decision Compare()
        {
            var full = BoardFillSchedulingComparison.DecideFull(BoardFillBacklogSnapshot.Read(database.Path));
            var projection = BoardFillSchedulingComparison.DecideProjected(BoardFillSchedulingComparison.ReadProjection(database.Path));
            Assert.Equal(full.Receipt(), projection.Receipt());
            return projection;
        }
    }

    public static bool IsWindows => OperatingSystem.IsWindows();
    private const string WindowsFileIdentity = "Verified snapshot hits require the Windows SQLite VFS.";

    [Theory(Skip = WindowsFileIdentity, SkipUnless = nameof(IsWindows))]
    [InlineData("eligibility")]
    [InlineData("goals")]
    [InlineData("epic")]
    [InlineData("deadline")]
    [InlineData("policy")]
    public async Task Each_live_input_can_change_the_draft_decision_on_a_verified_hit(string input)
    {
        using var database = new BoardFillSnapshotTestDatabase();
        var baseline = await Exercise(false);
        var reuse = await Exercise(true);
        Assert.Equal(new[] { ItemId }, baseline);
        Assert.Equal(baseline, reuse);

        async Task<string[]> Exercise(bool reuse)
        {
            using var drafts = new BoardFillAssessmentTestFixture();
            using var cache = new BoardFillBacklogSnapshotCache(database.Path);
            var raw = cache.Read();
            var rawReads = 0;
            var eligibility = input != "eligibility";
            var policy = ConductorAutonomyPolicy.Permissive with
            {
                BoardFillMode = ConductorBoardFillMode.Shadow,
                BoardFillTargetActiveGoals = input == "policy" ? 0 : 10,
                BoardFillMaxDraftsPerDay = 1
            };
            var kernel = drafts.Kernel;
            if (input == "goals")
            {
                var goal = kernel.CreateGoal("Active source");
                kernel.SetGoalSourceBacklogItemLink(goal.Id, ItemId, SourceBacklogCoverage.Slice);
            }
            if (input == "deadline")
            {
                var seeded = drafts.Finished();
                drafts.Store.Assess(seeded.Id, new(BoardFillAssessmentTestFixture.Verified, [],
                    BoardFillAssessmentTestFixture.NoDepends, false, ["fixture"]));
            }
            var portfolioPath = database.At($"portfolio-{reuse}.db");
            var actions = new List<string>();
            var host = new ConductorBoardFillHost(drafts.Store, (id, _) =>
            {
                actions.Add(id);
                return new("failed", 1, null, null, null, [], "recorded fixture draft");
            }, Read, (_, _) => item => new(eligibility && item.Id == ItemId, false, "fixture eligibility"), () => policy,
                new(drafts.EventPath), items => input == "epic"
                    ? BoardFillEpicMissingQuestion.ReadMemberships(portfolioPath, items)
                    : new HashSet<string> { ItemId }, () => drafts.Clock.UtcNow);
            try
            {
                host.ServiceTick(kernel);
                Assert.Null(host.CurrentRound);
                Assert.Empty(actions);
                var firstReads = rawReads;
                Assert.True(firstReads > 0);

                switch (input)
                {
                    case "eligibility": eligibility = true; break;
                    case "goals": kernel = new AgentOrchestratorKernel(drafts.Clock); break;
                    case "deadline": drafts.Clock.UtcNow = drafts.Clock.UtcNow.AddDays(1); break;
                    case "policy": policy = policy with { BoardFillTargetActiveGoals = 10 }; break;
                    case "epic":
                        var portfolio = new PortfolioStore(portfolioPath);
                        var epic = await portfolio.AddEpicAsync("Fresh membership");
                        await portfolio.AssignBacklogItemToEpicAsync(ItemId, epic.Id);
                        break;
                }

                host.ServiceTick(kernel);
                Assert.True(rawReads > firstReads);
                Assert.Equal(ItemId, Assert.Single(drafts.Store.ReadAll(), round => round.Outcome is null).BacklogItemId);
                await PanelTestHarness.Signal(host.CurrentRound!, "draft after live input changed");
                return actions.ToArray();
            }
            finally { host.Stop(); }

            IReadOnlyList<BacklogItem> Read()
            {
                rawReads++;
                if (!reuse) return BoardFillBacklogSnapshot.Read(database.Path);
                var hit = cache.Read();
                Assert.Same(raw, hit);
                return hit;
            }
        }
    }

    [Fact(Skip = WindowsFileIdentity, SkipUnless = nameof(IsWindows))]
    public async Task A_same_tick_note_commit_is_visible_to_draft_selection()
    {
        var baseline = await Exercise("full");
        var candidate = await Exercise("cache");
        var projection = await Exercise("projection");
        Assert.Equal(ItemId, Assert.Single(baseline.Actions));
        Assert.Equal(baseline.Actions, candidate.Actions);
        Assert.Equal(baseline.Acquired, candidate.Acquired);
        Assert.Equal(baseline.Actions, projection.Actions);
        Assert.Equal(baseline.Acquired, projection.Acquired);

        static async Task<(string[] Actions, string[] Acquired)> Exercise(string reader)
        {
            using var database = new BoardFillSnapshotTestDatabase();
            using var drafts = new BoardFillAssessmentTestFixture();
            using var cache = new BoardFillBacklogSnapshotCache(database.Path);
            var head = BoardFillAssessmentTestFixture.Head;
            var oldHead = new string('a', 40);
            var at = DateTimeOffset.Parse(Timestamp, System.Globalization.CultureInfo.InvariantCulture);
            var marker = BoardFillContradictionHold.Format(oldHead, at, "- The cited implementation is absent.",
                [new(1, "contradicted", "src/Feature/File.cs:1")]);
            await Append(ItemId, marker, at.AddMinutes(1));
            var warm = cache.Read();
            Assert.NotNull(BoardFillContradictionHold.ActiveMarker(warm.Single(item => item.Id == ItemId)));
            var captures = new List<string>();
            var actions = new List<string>();
            var host = new ConductorBoardFillHost(drafts.Store, (id, _) =>
            {
                actions.Add(id);
                return new("failed", 1, null, null, null, [], "recorded fixture draft");
            }, () =>
            {
                var result = reader switch
                {
                    "full" => BoardFillBacklogSnapshot.Read(database.Path),
                    "cache" => cache.Read(),
                    "projection" => BoardFillSchedulingSnapshot.Read(database.Path, [], at.AddHours(1), []).Candidates,
                    _ => throw new ArgumentOutOfRangeException(nameof(reader))
                };
                if (reader == "cache" && captures.Count == 0) Assert.Same(warm, result);
                captures.Add(Contents(result));
                return result;
            }, (_, _) => BoardFillAssessmentTestFixture.Ready,
                () => ConductorAutonomyPolicy.Permissive with { BoardFillMode = ConductorBoardFillMode.Shadow },
                new(drafts.EventPath), _ => new HashSet<string> { ItemId }, () => at.AddHours(1),
                mainHead: () => head, changedFiles: (_, _) => ["src/Feature/File.cs"],
                appendBacklogNote: (id, text) => Append(id, text, at.AddMinutes(2)).GetAwaiter().GetResult());
            try
            {
                host.ServiceTick(drafts.Kernel);
                await PanelTestHarness.Signal(host.CurrentRound!, "draft after same-tick note release");
                Assert.Collection(captures, first => Assert.DoesNotContain("retry permitted", first),
                    second => Assert.Contains("retry permitted", second));
                return (actions.ToArray(), captures.ToArray());
            }
            finally { host.Stop(); }

            async Task Append(string id, string text, DateTimeOffset when)
            {
                using var connection = database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO backlog_notes (backlog_item_id, created_at, text) VALUES ($id, $at, $text)";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$at", when.ToString("O"));
                command.Parameters.AddWithValue("$text", text);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [Theory(Skip = WindowsFileIdentity, SkipUnless = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public void Filing_revalidates_the_raw_item_and_matches_the_full_read(bool noteCommitted)
    {
        var baseline = Exercise(false, noteCommitted);
        var candidate = Exercise(true, noteCommitted);
        Assert.Equal(noteCommitted ? "refused" : "filed", baseline.Result);
        Assert.Equal(noteCommitted ? "not-fileable:owner-gated" : "ok", baseline.Reason);
        Assert.Equal(noteCommitted ? null : ItemId, baseline.FiledItem);
        Assert.Equal(baseline, candidate);

        static (string Result, string? Reason, string? FiledItem) Exercise(bool reuse, bool noteCommitted)
        {
            using var database = new BoardFillSnapshotTestDatabase();
            using var drafts = new BoardFillAssessmentTestFixture();
            using var cache = new BoardFillBacklogSnapshotCache(database.Path);
            var warm = cache.Read();
            var round = drafts.Finished(item: warm.Single(item => item.Id == ItemId));
            drafts.Store.Assess(round.Id, new(BoardFillAssessmentTestFixture.Verified,
                BoardFillPreflightChecks.Run(BoardFillAssessmentTestFixture.Brief), BoardFillAssessmentTestFixture.NoDepends,
                true, []));
            if (noteCommitted)
                database.Run($"INSERT INTO backlog_notes (backlog_item_id, created_at, text) VALUES ('{ItemId}', '{Timestamp}', 'owner approval required')");
            var intake = new BoardFillFilingTestFixture.IntakeFake();
            var host = new ConductorBoardFillHost(drafts.Store, (_, _) => throw new InvalidOperationException(),
                () =>
                {
                    if (!reuse) return BoardFillBacklogSnapshot.Read(database.Path);
                    var acquired = cache.Read();
                    if (noteCommitted) Assert.NotSame(warm, acquired);
                    else Assert.Same(warm, acquired);
                    return acquired;
                }, (_, _) => BoardFillAssessmentTestFixture.Ready,
                () => ConductorAutonomyPolicy.Permissive with { BoardFillMode = ConductorBoardFillMode.File },
                new(drafts.EventPath), _ => new HashSet<string> { ItemId }, () => drafts.Clock.UtcNow,
                filing: new(intake, () => new(0, new HashSet<string>()), () => BoardFillAssessmentTestFixture.Head,
                    (_, _) => null));
            try
            {
                var result = host.AttemptFiling(round.Id);
                return (result.Result, result.Reason, intake.Requests.SingleOrDefault()?.BacklogItemId);
            }
            finally { host.Stop(); }
        }
    }
}
