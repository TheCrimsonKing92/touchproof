using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: pure rendering and validation with private in-memory goals.
public sealed class PullRequestBodyPathRedactionTests
{
    [Fact]
    public void Render_Pr14Criteria_PreservesRepositoryFileAndLayout()
    {
        var goal = CreateGoal("""
            # Combat math coverage

            ## Acceptance criteria
            1. The committed file `src/game/combat-math.test.ts` covers combat math.
            2. The diff adds only `src/game/combat-math.test.ts`.
            """);

        var rendered = PullRequestBodyRenderer.Render(goal, "gate-attempt-1", ["unit", "lint"]);

        Assert.Equal("Combat math coverage", rendered.Title);
        Assert.Equal(string.Join(Environment.NewLine, new[]
        {
            "Combat math coverage",
            "",
            "## Acceptance criteria",
            "",
            "1. The committed file `src/game/combat-math.test.ts` covers combat math.",
            "2. The diff adds only `src/game/combat-math.test.ts`.",
            "",
            "## Gate",
            "",
            "acceptance gate passed, attempt gate-attempt-1, checks: unit, lint"
        }), rendered.Body);
    }

    [Theory]
    [InlineData("tests/Foo/Bar.cs")]
    [InlineData("docs/x.md")]
    [InlineData(@"src\game\combat-math.test.ts")]
    [InlineData("./src/x.cs")]
    [InlineData("../src/x.cs")]
    [InlineData("/var/lib/x")]
    [InlineData("https://github.com/owner/repo/pull/14")]
    public void Render_AllowedPath_PreservesVerbatim(string path)
    {
        var rendered = PullRequestBodyRenderer.Render(CriterionGoal($"Inspect `{path}`."),
            "gate-attempt-1", ["unit"]);

        Assert.Contains($"1. Inspect `{path}`.", rendered.Body);
        Assert.DoesNotContain("[redacted]", rendered.Body);
    }

    [Theory]
    [InlineData(@"C:\Users\someone\repo\x.cs")]
    [InlineData("C:/Users/someone/repo/x.cs")]
    [InlineData("/Users/someone/x")]
    [InlineData("/home/someone/x")]
    [InlineData("/c/Users/someone/x")]
    [InlineData("/HOME/someone/x")]
    [InlineData("/Documents and Settings/someone/x")]
    [InlineData("~/x")]
    [InlineData(@"\\host\share\x")]
    [InlineData("file:///C:/x")]
    public void Render_MachineLocalPath_RedactsEntirePath(string path)
    {
        var rendered = PullRequestBodyRenderer.Render(CriterionGoal($"Inspect `{path}`."),
            "gate-attempt-1", ["unit"]);

        Assert.Contains("1. Inspect `[redacted]`.", rendered.Body);
        Assert.DoesNotContain(path, rendered.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("someone", rendered.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_UserDirectorySource_RedactsNameInAllApprovedFields()
    {
        var goal = CreateGoal("""
            Summary for Casey
            # Casey heading

            ## Acceptance criteria
            1. Verify CASEY output and docs/Casey/x.md.
            """);

        var rendered = PullRequestBodyRenderer.Render(goal, "gate-attempt-1", ["unit Casey"],
            "/home/Casey/private");

        Assert.Equal("Summary for [redacted]", rendered.Title);
        Assert.Contains("[redacted] heading", rendered.Body);
        Assert.Contains("1. Verify [redacted] output and docs/[redacted]/x.md.", rendered.Body);
        Assert.Contains("checks: unit [redacted]", rendered.Body);
        Assert.DoesNotContain("Casey", rendered.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".orchestrator/goal-events/x.jsonl", "1. Inspect [redacted]")]
    [InlineData("docs/.orchestrator/goal-events/x.jsonl", "1. Inspect [redacted]")]
    [InlineData("ghp_example_access_string", "1. Inspect `[redacted]`.")]
    public void Render_ProhibitedWord_RedactsEntireWord(string word, string expectedCriterion)
    {
        var rendered = PullRequestBodyRenderer.Render(CriterionGoal($"Inspect `{word}`."),
            "gate-attempt-1", ["unit"]);

        // Orchestrator words include surrounding punctuation; forge tokens do not.
        Assert.Contains(expectedCriterion, rendered.Body.Split(Environment.NewLine));
        Assert.DoesNotContain(word, rendered.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Users\someone\repo\x.cs")]
    [InlineData("/Users/someone/x")]
    [InlineData("/home/someone/x")]
    [InlineData("/c/Users/someone/x")]
    [InlineData("~/x")]
    [InlineData(@"\\host\share\x")]
    [InlineData("file:///C:/x")]
    [InlineData(".orchestrator/goal-events/x.jsonl")]
    public void ValidateContent_ProhibitedContentRemains_Throws(string content)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PullRequestBodyRenderer.ValidateContent("Safe title", $"Inspect `{content}`.", []));

        Assert.Equal("Pull request content could not be rendered safely.", error.Message);
    }

    [Fact]
    public void ValidateContent_KnownUserRemains_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PullRequestBodyRenderer.ValidateContent("Safe title", "Verify SOMEONE output.", ["someone"]));
    }

    [Fact]
    public void ValidateContent_RepositoryPathRemains_AcceptsBody()
    {
        PullRequestBodyRenderer.ValidateContent("Safe title", "Inspect `src/game/combat-math.test.ts`.", []);
    }

    [Fact]
    public void Render_ScrubbingExposesMachineLocalPath_Throws()
    {
        // Name redaction exposes a drive path that initially followed a word character.
        var goal = CriterionGoal("Inspect CaseyC:/private/x.cs");

        Assert.Throws<InvalidOperationException>(() =>
            PullRequestBodyRenderer.Render(goal, "gate-attempt-1", ["unit"], "/home/Casey/private"));
    }

    private static Goal CriterionGoal(string criterion) => CreateGoal($"""
        # Safe title

        ## Acceptance criteria
        1. {criterion}
        """);

    private static Goal CreateGoal(string objective) => new AgentOrchestratorKernel().CreateGoal(objective,
        [new TaskSpec(TaskId.New(), "Render pull request content", AgentRole.Developer)]);
}
