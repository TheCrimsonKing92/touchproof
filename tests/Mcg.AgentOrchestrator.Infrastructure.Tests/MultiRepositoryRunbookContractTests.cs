using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

// Parallel-safe: reads repository contracts and mutates only in-memory copies.
public sealed class MultiRepositoryRunbookContractTests
{
    private const string Title = "Multi-repository goals";
    private const string RepairVerb = "repair-multi-repository-landing";
    private static readonly string[] RequiredText =
        ["--repositories", "multi-repository-landing-failed", RepairVerb];

    [Fact]
    public void Section_CurrentRunbook_MatchesRepairContract()
    {
        Assert.Empty(FindFailures(ReadRunbook(), ReadRepairVerb()));
    }

    [Theory]
    [InlineData(Title)]
    [InlineData("--repositories")]
    [InlineData("multi-repository-landing-failed")]
    [InlineData(RepairVerb)]
    public void Contract_RemovedRequirement_ReportsMissingText(string removed)
    {
        var markdown = ReadRunbook();
        var section = FindSection(markdown);
        Assert.NotNull(section);
        var mutated = removed == Title
            ? markdown.Replace(Title, "Other goals", StringComparison.Ordinal)
            : markdown.Replace(section, section.Replace(removed, "removed", StringComparison.Ordinal),
                StringComparison.Ordinal);

        Assert.Contains("Missing " + removed, FindFailures(mutated, ReadRepairVerb()));
    }

    [Theory]
    [InlineData("###")]
    [InlineData("##")]
    [InlineData("#")]
    public void Contract_TextOutsideSection_DoesNotSatisfyRequirements(string nextHeading)
    {
        var required = string.Join(' ', RequiredText);
        var markdown = $"# Other guidance\n{required}\n### 6.2.5 {Title}\nPending.\n" +
            $"{nextHeading} Next section\n{required}\n";

        Assert.Equal(RequiredText.Select(text => "Missing " + text), FindFailures(markdown, RepairVerb));
    }

    [Fact]
    public void Contract_RenamedRepairConstant_ReportsDrift()
    {
        var renamedVerb = ReadRepairVerb() + "-renamed";

        Assert.Contains("Repair verb constant differs from " + RepairVerb,
            FindFailures(ReadRunbook(), renamedVerb));
    }

    [Theory]
    [InlineData("repair-multi-repository-landing-v2")]
    [InlineData("other-repair-multi-repository-landing")]
    [InlineData("repair-multi-repository-landing_v2")]
    public void Contract_RenamedDocumentedVerb_ReportsMissingVerb(string documentedVerb)
    {
        var markdown = ReadRunbook().Replace(RepairVerb, documentedVerb, StringComparison.Ordinal);

        Assert.Contains("Missing " + RepairVerb, FindFailures(markdown, ReadRepairVerb()));
    }

    [Theory]
    [InlineData("\n", "```")]
    [InlineData("\r\n", "~~~")]
    public void Section_FencesAndSubheadings_PreserveBody(string newline, string fence)
    {
        var markdown = string.Join(newline,
            fence, "### " + Title, fence,
            "### 6.2.5 " + Title, fence + "text", "# Example comment", fence,
            "#### Details", string.Join(' ', RequiredText));

        Assert.Empty(FindFailures(markdown, RepairVerb));
    }

    private static IReadOnlyList<string> FindFailures(string markdown, string expectedVerb)
    {
        var failures = new List<string>();
        if (expectedVerb != RepairVerb)
            failures.Add("Repair verb constant differs from " + RepairVerb);

        var section = FindSection(markdown);
        if (section is null)
        {
            failures.Add("Missing " + Title);
            return failures;
        }

        foreach (var text in RequiredText.Append(expectedVerb).Distinct(StringComparer.Ordinal))
            if (!Regex.IsMatch(section, @"(?<![A-Za-z0-9_-])" + Regex.Escape(text) + @"(?![A-Za-z0-9_-])",
                    RegexOptions.CultureInvariant))
                failures.Add("Missing " + text);
        return failures;
    }

    private static string? FindSection(string markdown)
    {
        var body = new List<string>();
        var sectionLevel = 0;
        char fenceCharacter = '\0';
        var fenceLength = 0;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var fence = Regex.Match(line, @"^ {0,3}(?<marker>`{3,}|~{3,})(?<rest>.*)$");
            if (fenceCharacter != '\0')
            {
                if (sectionLevel > 0) body.Add(line);
                if (fence.Success && fence.Groups["marker"].Value[0] == fenceCharacter &&
                    fence.Groups["marker"].Length >= fenceLength &&
                    string.IsNullOrWhiteSpace(fence.Groups["rest"].Value))
                    fenceCharacter = '\0';
                continue;
            }
            if (fence.Success)
            {
                fenceCharacter = fence.Groups["marker"].Value[0];
                fenceLength = fence.Groups["marker"].Length;
                if (sectionLevel > 0) body.Add(line);
                continue;
            }

            var heading = Regex.Match(line, @"^ {0,3}(?<level>#{1,6})[ \t]+(?<title>.*)$");
            if (heading.Success)
            {
                var level = heading.Groups["level"].Length;
                if (sectionLevel > 0 && level <= sectionLevel) break;
                if (sectionLevel == 0 && heading.Groups["title"].Value.Contains(Title, StringComparison.Ordinal))
                {
                    sectionLevel = level;
                    continue;
                }
            }
            if (sectionLevel > 0) body.Add(line);
        }
        return sectionLevel == 0 ? null : string.Join('\n', body);
    }

    private static string ReadRunbook() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "operator-runbook.md"))
            .ReplaceLineEndings("\n");

    private static string ReadRepairVerb()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.App",
            "Orchestration", "MultiRepositoryLandingFailedHold.cs"));
        var declaration = Regex.Match(source,
            "(?m)^\\s*internal\\s+const\\s+string\\s+NextAction\\s*=\\s*\"(?<verb>[^\"]+)\"\\s*;");
        Assert.True(declaration.Success, "Could not read MultiRepositoryLandingFailedHold.NextAction declaration.");
        return declaration.Groups["verb"].Value;
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
