using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BoardFillNearDuplicateTests
{
    private static readonly DateTimeOffset Now = BoardFillReadyItemSelectorTests.Now;
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    private static BacklogItem Item(char id = 'a') => BoardFillReadyItemSelectorTests.Item(id) with
    { Title = "Fix board fill regression", Body = "See `src/BoardFill.cs:35-67`." };
    private static BoardFillDraftRound Round(BacklogItem item, DateTimeOffset started) =>
        new("round-" + item.Id, item.Id, item.UpdatedAt, null, null, "failed", null, null, [], started, started);
    private static BacklogItem? Select(BacklogItem candidate, IReadOnlyList<BacklogItem> items,
        IReadOnlyList<Goal> goals, IReadOnlyList<BoardFillDraftRound> rounds,
        Action<BacklogItem, BoardFillNearDuplicateReference>? skipped = null) =>
        BoardFillReadyItemSelector.Select([candidate], goals, None, _ => new(true, false, "ready"),
            nearDuplicate: BoardFillNearDuplicate.Lookup(items, goals, rounds, Now), nearDuplicateSkipped: skipped);

    [Fact]
    public void Select_Open_goal_match_Drops_candidate_and_reports_reference()
    {
        var candidate = Item();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preamble\n\n# Fix board fill regression\nSee [source](./SRC/BoardFill.cs:12:3)");
        var skipped = new List<(BacklogItem Item, BoardFillNearDuplicateReference Reference)>();
        Assert.Null(Select(candidate, [candidate], kernel.Goals.ToArray(), [],
            (item, reference) => skipped.Add((item, reference))));
        var match = Assert.Single(skipped);
        Assert.Same(candidate, match.Item);
        Assert.Equal(new BoardFillNearDuplicateReference("goal", goal.Id.Value, "Fix board fill regression"), match.Reference);
    }

    [Theory]
    [InlineData(-1380, true)]
    [InlineData(-1440, true)]
    [InlineData(0, true)]
    [InlineData(-1441, false)]
    [InlineData(1, false)]
    public void Select_Draft_window_Uses_inclusive_cutoff_and_excludes_future(int minutes, bool skip)
    {
        var candidate = Item();
        var reference = Item('b');
        var selected = Select(candidate, [candidate, reference], [], [Round(reference, Now.AddMinutes(minutes))]);
        if (skip) Assert.Null(selected);
        else Assert.Same(candidate, selected);
    }

    [Theory]
    [InlineData("See src/Other.cs", "Fix board fill regression")]
    [InlineData("No cited files", "Fix board fill regression")]
    [InlineData("See src/BoardFill.cs", "Unrelated database migration")]
    [InlineData("See src/BoardFill.cs", "the and to")]
    public void Select_Missing_shared_path_or_words_Keeps_candidate(string body, string title)
    {
        var candidate = Item();
        var reference = Item('b') with { Body = body, Title = title };
        Assert.Same(candidate, Select(candidate, [candidate, reference], [], [Round(reference, Now)]));
    }

    [Fact]
    public void Select_Terminal_goal_and_own_round_Keep_candidate()
    {
        var candidate = Item();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("# Fix board fill regression\nSee src/BoardFill.cs");
        kernel.CancelGoal(goal.Id, "Terminal reference fixture");
        Assert.Same(candidate, Select(candidate, [candidate], kernel.Goals.ToArray(), [Round(candidate, Now)]));
        Assert.Null(BoardFillNearDuplicate.Lookup([candidate], [], [Round(Item('z'), Now)], Now)(candidate));
    }

    [Theory]
    [InlineData("Fix board", true)]
    [InlineData("Fix", false)]
    [InlineData("Fix board fill regression unrelated terms", true)]
    public void Lookup_Title_overlap_Uses_distinct_candidate_relative_half(string title, bool match)
    {
        var candidate = Item() with { Title = "The FIX fix board fill regression" };
        var reference = Item('b') with { Title = title };
        Assert.Equal(match, BoardFillNearDuplicate.Lookup([reference], [], [Round(reference, Now)], Now)(candidate) is not null);
        Assert.Null(BoardFillNearDuplicate.Lookup([reference], [], [Round(reference, Now)], Now)(
            candidate with { Title = "the and to" }));
    }

    [Fact]
    public void Lookup_Notes_and_metadata_Do_not_supply_paths_or_title_words()
    {
        var candidate = Item();
        var metadataOnly = Item('b') with
        {
            Body = "No files", Tags = "src/BoardFill.cs",
            Notes = [new(Now, "Fix board fill regression src/BoardFill.cs")]
        };
        Assert.Null(BoardFillNearDuplicate.Lookup([metadataOnly], [], [Round(metadataOnly, Now)], Now)(candidate));
        Assert.Null(BoardFillNearDuplicate.Lookup([candidate], [], [Round(candidate, Now)], Now)(
            metadataOnly with { Id = "other" }));
        var bodyOnly = Item('c') with { Title = "Unrelated database migration", Body = candidate.Title + " src/BoardFill.cs" };
        Assert.Null(BoardFillNearDuplicate.Lookup([bodyOnly], [], [Round(bodyOnly, Now)], Now)(candidate));
    }

    [Fact]
    public void Lookup_Multiple_matches_Prefers_goal_then_ordinal_identity()
    {
        var candidate = Item();
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("# Fix board fill regression\nSee src/BoardFill.cs");
        kernel.CreateGoal("# Fix board fill regression\nSee src/BoardFill.cs");
        var goals = kernel.Goals.OrderByDescending(goal => goal.Id.Value, StringComparer.Ordinal).ToArray();
        var first = Item('b');
        var second = Item('c');
        var rounds = new[] { Round(second, Now), Round(first, Now), Round(first, Now.AddMinutes(-1)) };
        var items = new[] { second, first, candidate };
        var goalMatch = BoardFillNearDuplicate.Lookup(items, goals, rounds, Now)(candidate);
        Assert.Equal(new BoardFillNearDuplicateReference("goal", goals[^1].Id.Value, "Fix board fill regression"), goalMatch);
        var itemMatch = BoardFillNearDuplicate.Lookup(items, [], rounds, Now)(candidate);
        Assert.Equal(new BoardFillNearDuplicateReference("item", first.Id, first.Title), itemMatch);
        Assert.Equal(goalMatch, BoardFillNearDuplicate.Lookup(items.Reverse().ToArray(),
            goals.Reverse().ToArray(), rounds.Reverse().ToArray(), Now)(candidate));
    }

    [Theory]
    [InlineData("[source](./SRC/BoardFill.cs:35-67)")]
    [InlineData("`src\\BoardFill.cs:12:3`")]
    [InlineData("See src/BoardFill.cs.")]
    [InlineData("<src/BoardFill.cs#L12-L15>")]
    [InlineData("src/BoardFill.cs::Select")]
    [InlineData("**src/BoardFill.cs**.")]
    [InlineData("src/BoardFill.cs:")]
    public void CitedPaths_Citation_formats_Normalize_without_filesystem_access(string text)
    {
        var paths = BoardFillNearDuplicate.CitedPaths(text);
        Assert.Single(paths);
        Assert.Contains("src/boardfill.cs", paths, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://example.com/src/BoardFill.cs")]
    [InlineData("file:///src/BoardFill.cs")]
    [InlineData("C:\\repo\\src\\BoardFill.cs:12")]
    [InlineData("/src/BoardFill.cs")]
    [InlineData("\\\\server\\repo\\BoardFill.cs")]
    [InlineData("../src/BoardFill.cs")]
    [InlineData("src/../BoardFill.cs")]
    [InlineData("src/Directory/")]
    public void CitedPaths_Non_repository_reference_Extracts_nothing(string text) =>
        Assert.Empty(BoardFillNearDuplicate.CitedPaths(text));

    [Fact]
    public void Extraction_Root_files_unicode_and_goal_fallback_Are_supported()
    {
        Assert.Contains("readme.md", BoardFillNearDuplicate.CitedPaths("`./README.md:12`"), StringComparer.OrdinalIgnoreCase);
        var words = BoardFillNearDuplicate.TitleWords("RÉGRESSION régression 修復作 and to");
        Assert.Equal(2, words.Count);
        Assert.Contains("régression", words);
        Assert.Contains("修復作", words);
        Assert.Equal("First line", BoardFillNearDuplicate.GoalTitle("\nFirst line\nBody"));
        Assert.Equal("Heading", BoardFillNearDuplicate.GoalTitle("Preamble\n## Heading ##\nBody"));
        Assert.Equal("", BoardFillNearDuplicate.GoalTitle(" \n"));
    }
}
