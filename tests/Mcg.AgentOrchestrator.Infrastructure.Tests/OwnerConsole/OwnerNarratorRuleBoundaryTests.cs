using System.Text.RegularExpressions;

// Parallel-safe: source checks are read-only and use the explicitly verified repository root.
public sealed class OwnerNarratorRuleBoundaryTests
{
    [Fact]
    public void Narrator_ExtractedRules_StaysWithinLineLimitWithoutQuestionMatcher()
    {
        var path = SourcePath("OwnerActivityNarrator");

        Assert.InRange(File.ReadLines(path).Count(), 1, 260);
        Assert.DoesNotContain("SameQuestion", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OwnerQuestionRoutes")]
    [InlineData("OwnerEscalationNarration")]
    [InlineData("OwnerAttentionNarration")]
    public void RuleFile_PresentationOnly_DeclaresOneSmallSealedType(string name)
    {
        var path = SourcePath(name);
        var source = File.ReadAllText(path);

        Assert.InRange(File.ReadLines(path).Count(), 1, 119);
        Assert.DoesNotMatch(@"\bpartial\b", source);
        var declarations = Regex.Matches(source,
            @"(?m)^(?:(?:public|internal|file)\s+)?(?:(?:sealed|static|abstract|readonly|ref)\s+)*(?:class|record(?:\s+(?:class|struct))?|struct|interface|enum)\s+(\w+)");
        Assert.Equal(name, Assert.Single(declarations.Cast<Match>()).Groups[1].Value);
        Assert.Matches(@"(?m)^internal sealed class " + Regex.Escape(name) + @"\b", source);
        foreach (var forbidden in new[] { "IOrchestratorStateQueries", "System.IO", "Process" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    private static string SourcePath(string name) =>
        Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Console", name + ".cs");
}
