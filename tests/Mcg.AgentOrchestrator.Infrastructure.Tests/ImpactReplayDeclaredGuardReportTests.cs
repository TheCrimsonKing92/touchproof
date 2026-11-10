using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: reports and rendered output stay in memory.
public sealed class ImpactReplayDeclaredGuardReportTests
{
    private const string Heading = "## Declared guard selections";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n\t")]
    public void Render_EmptyGuardData_PreservesLegacyMarkdown(string? reason)
    {
        var attempt = Attempt("legacy", new([], reason));
        var legacy = ImpactReplayReportWriter.Render(Report(attempt with { DeclaredGuards = null }));
        var markdown = ImpactReplayReportWriter.Render(Report(attempt));

        Assert.DoesNotContain(Heading, legacy);
        Assert.EndsWith("No unmapped test classes were found." + Environment.NewLine, legacy);
        Assert.Equal(legacy, markdown);
    }

    [Fact]
    public void Render_NoAttempts_OmitsSectionAndWhitespace()
    {
        var markdown = ImpactReplayReportWriter.Render(Report());

        Assert.DoesNotContain(Heading, markdown);
        Assert.EndsWith("No unmapped test classes were found." + Environment.NewLine, markdown);
    }

    [Fact]
    public void Render_SelectedGuards_EmitsOneSectionWithOwnershipAndDiagnostics()
    {
        var guard = new ImpactReplayDeclaredGuard("tests/Foreign.Tests/Foreign.Tests.csproj", "Sample.Guard",
            "live-tree-structural", "tests/Foreign.Tests/Guard.cs", ["src/Input.cs"], "repository tree changed");
        var attempts = new[]
        {
            Attempt("empty", new([], " ")),
            Attempt("selected", new([guard], "other declaration unknown")),
            Attempt("diagnostic", new([], "membership unavailable")),
            Attempt("second", new([guard with { ClassName = "Sample.OtherGuard" }]))
        };
        var legacy = ImpactReplayReportWriter.Render(Report(attempts.Select(a => a with { DeclaredGuards = null }).ToArray()));

        var markdown = ImpactReplayReportWriter.Render(Report(attempts));

        Assert.Equal(1, markdown.Split(Heading, StringSplitOptions.None).Length - 1);
        Assert.Equal(legacy + Section(
            "- selected: other declaration unknown",
            "- selected: tests/Foreign.Tests/Foreign.Tests.csproj / Sample.Guard; live-tree-structural; " +
                "declaration: tests/Foreign.Tests/Guard.cs; inputs: src/Input.cs; repository tree changed",
            "- diagnostic: membership unavailable",
            "- second: tests/Foreign.Tests/Foreign.Tests.csproj / Sample.OtherGuard; live-tree-structural; " +
                "declaration: tests/Foreign.Tests/Guard.cs; inputs: src/Input.cs; repository tree changed"), markdown);
    }

    [Theory]
    [InlineData("unknown dependency kind")]
    [InlineData("unmatched declaration owner")]
    [InlineData("declaration present but guard unselected")]
    public void Render_DiagnosticWithoutSelections_PreservesSection(string reason)
    {
        var attempt = Attempt("diagnostic", new([], reason));
        var legacy = ImpactReplayReportWriter.Render(Report(attempt with { DeclaredGuards = null }));

        var markdown = ImpactReplayReportWriter.Render(Report(attempt));

        Assert.Equal(legacy + Section("- diagnostic: " + reason), markdown);
    }

    private static string Section(params string[] rows) => Environment.NewLine + Heading +
        Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, rows) + Environment.NewLine;

    private static ImpactReplayReport Report(params ImpactReplayAttemptRecord[] attempts) =>
        new("override", [], attempts, [], [], []);

    private static ImpactReplayAttemptRecord Attempt(string id, ImpactReplayDeclaredGuardReport guards) =>
        new(id, "goal", DateTimeOffset.UnixEpoch, "base", "candidate", "Resolved", null, null,
            [], [], [], [], [], [], 0, [], [], true, [], DeclaredGuards: guards);
}
