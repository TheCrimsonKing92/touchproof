using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fake process/repository seams and a unique backlog/receipt root per test.
public sealed class AuthorStaleRetirementTests
{
    [Fact]
    public async Task Stale_result_appends_reason_and_ordered_evidence_without_changing_output()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        await fixture.Store.UpdateAsync(fixture.Item.Id, new(Tags: "existing-tag"));
        var before = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        const string reason = "The requested capability already exists.";
        string[] evidence = ["docs/state.md:3", "docs/second.md:7", "docs/state.md:3"];

        var code = fixture.Run(JsonSerializer.Serialize(new { kind = "stale", reason, evidenceReferences = evidence }));

        Assert.Equal(2, code);
        Assert.Equal(reason + fixture.Output.NewLine, fixture.Output.ToString());
        Assert.Empty(fixture.Error.ToString());
        var current = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        Assert.Equal(before.Notes.Count + 1, current.Notes.Count);
        Assert.All(before.Notes, note => Assert.Contains(note, current.Notes));
        var appended = Assert.Single(current.Notes.Except(before.Notes));
        Assert.Equal(reason + Environment.NewLine + "Evidence:" + Environment.NewLine +
            string.Join(Environment.NewLine, evidence.Select(reference => "- " + reference)), appended.Text);
        Assert.Equal(BacklogItemStatus.Open, current.Status);
        Assert.Equal("existing-tag,author-refused", current.Tags);
        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
        using var receipt = fixture.Receipt();
        Assert.Equal("stale", receipt.RootElement.GetProperty("kind").GetString());
        Assert.Equal(reason, receipt.RootElement.GetProperty("staleReason").GetString());
        Assert.Equal(evidence, receipt.RootElement.GetProperty("evidenceReferences").EnumerateArray()
            .Select(reference => reference.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("failure").ValueKind);
    }
}
