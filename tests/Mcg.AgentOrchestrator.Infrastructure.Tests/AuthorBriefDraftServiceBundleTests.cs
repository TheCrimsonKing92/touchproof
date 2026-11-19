using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its backlog and uses fake repository and process seams.
public sealed class AuthorBriefDraftServiceBundleTests
{
    private const string SinglePart = "Wanted:\n1. Add a parser";
    private const string Bundle = SinglePart + "\n2) Persist the result\n(3) Show the result";
    private const string Diagnostic = "draft would be split";
    private const string PromptSection = "Detected Wanted parts:";

    [Fact]
    public async Task UnjustifiedBundle_ReportsEachPartAndLimitsFirstPrompt()
    {
        var (output, prompt) = await CaptureDraftAsync(Bundle, "");

        var diagnostic = Assert.Single(output.Split('\n').Where(line =>
            line.Contains(Diagnostic, StringComparison.Ordinal)));
        // The item body already contains the parts. Check the additional prompt section specifically.
        var bundleSectionStart = prompt.IndexOf(PromptSection, StringComparison.Ordinal);
        Assert.True(bundleSectionStart >= 0, "The first process request has no bundle section.");
        var bundleSection = prompt[bundleSectionStart..];
        foreach (var part in new[] { "Add a parser", "Persist the result", "Show the result" })
        {
            Assert.Contains(part, diagnostic);
            Assert.Contains(part, bundleSection);
        }
        Assert.Contains("Draft only the first slice", bundleSection);
    }

    [Theory]
    [InlineData(SinglePart, "")]
    [InlineData(Bundle, "All parts must ship together")]
    public async Task SinglePartOrJustifiedBundle_SkipsBundleDiagnostic(string body, string reason)
    {
        var (output, prompt) = await CaptureDraftAsync(body, reason);

        Assert.DoesNotContain(Diagnostic, output);
        Assert.DoesNotContain(PromptSection, prompt);
        Assert.Contains("Add a parser", prompt);
    }

    private static async Task<(string Output, string Prompt)> CaptureDraftAsync(string body, string reason)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var item = await fixture.Store.AddAsync("Bundle drafting example", body, bundledReason: reason);
        var requests = new List<WorkerProcessRunRequest>();

        AuthorBriefDraftService.Run(item.Id, fixture.Workspace, new((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new WorkerProcessRunResult(0, "not JSON", ""));
            }, fixture.Repository),
            fixture.Output, fixture.Error);

        var first = Assert.Single(requests);
        Assert.NotNull(first.StandardInput);
        return (fixture.Output.ToString(), first.StandardInput);
    }
}
