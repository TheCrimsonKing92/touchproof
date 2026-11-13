using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: parser and prompt cases use only local immutable inputs.
public sealed class AuthorStaleResultContractTests
{
    [Theory]
    [InlineData("delivered", "[\"abcdef0\"]")]
    [InlineData("delivered", "[\"ABCDEF0\",\"0123456789abcdef0123456789abcdef01234567\"]")]
    [InlineData("refused", null)]
    [InlineData("refused", "[]")]
    [InlineData("refused", "[\"ABCDEF0\"]")]
    public void Valid_stale_shape_preserves_structured_fields(string staleKind, string? commits)
    {
        var json = Stale(new Dictionary<string, object?>
        {
            ["staleKind"] = staleKind
        }, commits);

        var result = AuthorBriefDraftResultParser.Parse(json);

        Assert.NotNull(result);
        Assert.Equal("stale", result.Kind);
        Assert.Null(result.Markdown);
        Assert.Equal("Cannot draft now", result.Reason);
        Assert.Equal(new[] { "seed.txt:1" }, result.EvidenceReferences);
        Assert.Equal(staleKind, result.StaleKind);
        string[] expected = commits is null ? [] : JsonSerializer.Deserialize<string[]>(commits)!;
        Assert.Equal(expected, result.ImplementingCommits);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"Delivered\"")]
    [InlineData("\" refused\"")]
    [InlineData("\"refused \"")]
    [InlineData("\"other\"")]
    public void Missing_or_inexact_stale_kind_is_invalid(string value)
    {
        var fields = new Dictionary<string, object?>();
        if (value != "missing") fields["staleKind"] = JsonSerializer.Deserialize<JsonElement>(value);

        Assert.Null(AuthorBriefDraftResultParser.Parse(Stale(fields, "[\"abcdef0\"]")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    public void Delivered_requires_nonempty_commit_list(string? commits)
    {
        Assert.Null(AuthorBriefDraftResultParser.Parse(Stale(new Dictionary<string, object?>
        {
            ["staleKind"] = "delivered"
        }, commits)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"abcdef0\"")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    [InlineData("[\"\"]")]
    [InlineData("[\" \"]")]
    [InlineData("[\"main\"]")]
    [InlineData("[\"HEAD\"]")]
    [InlineData("[\"main~1\"]")]
    [InlineData("[\"abcdef\"]")]
    [InlineData("[\"0123456789abcdef0123456789abcdef01234567890\"]")]
    [InlineData("[\"abcdef0^\"]")]
    [InlineData("[\" abcdef0\"]")]
    [InlineData("[\"abcdef0 \"]")]
    [InlineData("[\"abcdef0\\n\"]")]
    [InlineData("[\"abcdef0\",\"main\"]")]
    public void Supplied_commit_list_requires_hex_ids_for_either_kind(string commits)
    {
        foreach (var staleKind in new[] { "delivered", "refused" })
            Assert.Null(AuthorBriefDraftResultParser.Parse(Stale(new Dictionary<string, object?>
            {
                ["staleKind"] = staleKind
            }, commits)));
    }

    [Fact]
    public void Draft_shape_remains_unchanged()
    {
        const string markdown = "# Draft\nKeep {braces} and content unchanged.";

        var result = AuthorBriefDraftResultParser.Parse(JsonSerializer.Serialize(new { kind = "draft", markdown }));

        Assert.NotNull(result);
        Assert.Equal("draft", result.Kind);
        Assert.Equal(markdown, result.Markdown);
        Assert.Null(result.Reason);
        Assert.Empty(result.EvidenceReferences);
        Assert.Null(result.StaleKind);
        Assert.Empty(result.ImplementingCommits);
        Assert.Empty(result.DependsOnGoals);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"ABCdef\",\"0123456789ABCDEF0123456789abcdef\",\"ABCdef\"]")]
    public void Draft_dependency_array_preserves_order_duplicates_and_casing(string goals)
    {
        var result = AuthorBriefDraftResultParser.Parse(
            "{\"kind\":\"draft\",\"markdown\":\"# Draft\",\"dependsOnGoals\":" + goals + "}");

        Assert.NotNull(result);
        Assert.Equal(JsonSerializer.Deserialize<string[]>(goals), result.DependsOnGoals);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"abcdef\"")]
    [InlineData("{}")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"abcde\"]")]
    [InlineData("[\"0123456789ABCDEF0123456789abcdef0\"]")]
    [InlineData("[\"abcdeg\"]")]
    [InlineData("[\" abcdef\"]")]
    [InlineData("[\"abcdef \"]")]
    [InlineData("[\"abcdef\\n\"]")]
    [InlineData("[\"abcdef\",false]")]
    public void Draft_malformed_dependency_array_is_unparseable(string goals)
    {
        Assert.Null(AuthorBriefDraftResultParser.Parse(
            "{\"kind\":\"draft\",\"markdown\":\"# Draft\",\"dependsOnGoals\":" + goals + "}"));
    }

    [Fact]
    public void Stale_ignores_draft_only_dependency_field()
    {
        var result = AuthorBriefDraftResultParser.Parse(Stale(new Dictionary<string, object?>
        {
            ["staleKind"] = "refused",
            ["dependsOnGoals"] = 42
        }, null));

        Assert.NotNull(result);
        Assert.Equal("stale", result.Kind);
        Assert.Empty(result.DependsOnGoals);
    }

    [Fact]
    public void Prompt_defines_stale_kinds_commit_ids_and_head_read_rule()
    {
        var item = new BacklogItem("item", "Draft title", "Draft body", BacklogItemStatus.Open,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
        var prompt = AuthorBriefDraftPrompt.Render(item, ConductorLessonSelection.Empty,
            CliAuthorDraftCommandTests.Fixture.MainSha);
        var lines = prompt.ReplaceLineEndings("\n").Split('\n');

        Assert.Contains("If the item cannot be drafted, return kind stale with a staleKind, a reason and evidenceReferences.", lines);
        Assert.Contains("staleKind delivered means every part of the item is already on main; name the implementing commits in implementingCommits and cite the path:line of each part in evidenceReferences.", lines);
        Assert.Contains("staleKind refused means the item cannot be drafted now; the reason says why.", lines);
        Assert.Contains("The main HEAD you read is never an implementing commit.", lines);
        Assert.Contains("List implementing commits as hexadecimal commit IDs of 7 to 40 characters, never branch names or revision expressions.", lines);
        Assert.Contains("implementingCommits is required and non-empty for delivered; it may be omitted for refused.", lines);
        Assert.Contains("or {\"kind\":\"stale\",\"staleKind\":\"<delivered|refused>\",\"reason\":\"<reason>\",\"implementingCommits\":[\"<commit ID>\"],\"evidenceReferences\":[\"<path:line>\"]}.", lines);
    }

    private static string Stale(Dictionary<string, object?> fields, string? commits)
    {
        fields["kind"] = "stale";
        fields["reason"] = "Cannot draft now";
        fields["evidenceReferences"] = new[] { "seed.txt:1" };
        if (commits is not null) fields["implementingCommits"] = JsonSerializer.Deserialize<JsonElement>(commits);
        return JsonSerializer.Serialize(fields);
    }
}
