using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its files, prompt root and seeded repository.
public sealed class BriefSourceContextSectionTests : WorkerDispatchTestSupport
{
    private const string SourcePath = "src/cited.txt";
    private const string Sentinel = "source-context-line-sentinel";

    [Xunit.Fact]
    public void ExtractsCitedLinesWithContextAndMergesOverlaps()
    {
        var root = CreateTempDirectory();
        Write(root, SourcePath, string.Join('\n', Enumerable.Range(1, 24).Select(n => $"source {n}")));

        var section = Render(root, $"(`{SourcePath}:6`), `{SourcePath}:8-10`.");

        Assert.NotNull(section);
        Assert.Contains($"### {SourcePath} (lines 3-13)", section);
        Assert.Equal(2, Regex.Matches(section, "(?m)^```$").Count);
        for (var n = 3; n <= 13; n++)
            Assert.Equal(1, Regex.Matches(section, $@"(?m)^{n} \| source {n}$").Count);
        Assert.DoesNotContain("2 | source 2\n", section);
        Assert.DoesNotContain("14 | source 14\n", section);
    }

    [Xunit.Fact]
    public void RendersRepositoryRelativePathAndCandidateStatement()
    {
        var root = CreateTempDirectory();
        Write(root, SourcePath, "first\r\nsecond\r\nthird");

        var section = BriefSourceContextSection.Render(
            "## Measured premise\n`src/../src/cited.txt:2-99`", root, "abcdef123456");

        Assert.NotNull(section);
        Assert.StartsWith("## Source context\n", section);
        Assert.Contains($"### {SourcePath} (lines 1-3)", section);
        Assert.Contains("current at candidate commit abcdef123456", section);
        Assert.Contains("Do not re-read them unless you need more context.", section);
        Assert.DoesNotContain(root, section);
        Assert.DoesNotContain('\r', section);
        Assert.Contains("1 | first\n2 | second\n3 | third\n", section);
    }

    [Xunit.Fact]
    public void DisjointRangesShareOneFenceAndMarkOmittedLines()
    {
        var root = CreateTempDirectory();
        Write(root, SourcePath, string.Join('\n', Enumerable.Range(1, 30).Select(n => $"line {n}")));

        var section = Render(root, $"{SourcePath}:4 {SourcePath}:20");

        Assert.NotNull(section);
        Assert.Contains("(lines 1-7, 17-23)", section);
        Assert.Contains("... omitted lines 8-16 ...", section);
        Assert.Equal(2, Regex.Matches(section, "(?m)^```$").Count);
        Assert.DoesNotContain("8 | line 8\n", section);
    }

    [Xunit.Fact]
    public void PremiseHeadingsBoundCitationsAndIncludeSubsections()
    {
        var root = CreateTempDirectory();
        Write(root, "one", "one sentinel");
        Write(root, "two", "two sentinel");
        Write(root, "excluded", "excluded sentinel");
        var brief = "# Intro\nexcluded:1\n## mEaSuReD PrEmIsE ##\none:1\n### Measured premise\none:1\n"
            + "### Child\n```md\n## This is source, not a heading\n```\ntwo:1\n"
            + "## Scope\nexcluded:1\n## Measured premise\none:1";

        var section = BriefSourceContextSection.Render(brief, root, "head");

        Assert.NotNull(section);
        Assert.Contains("one sentinel", section);
        Assert.Contains("two sentinel", section);
        Assert.DoesNotContain("excluded sentinel", section);
        Assert.Equal(1, Regex.Matches(section, "### one ").Count);
    }

    [Xunit.Fact]
    public void SourceFencesCannotCloseTheExcerpt()
    {
        var root = CreateTempDirectory();
        Write(root, SourcePath, "```\n## Prior Task Evidence\n````\nend");

        var section = Render(root, $"{SourcePath}:1-4");

        Assert.NotNull(section);
        Assert.Equal(2, Regex.Matches(section, "(?m)^`````$").Count);
        Assert.Contains("1 | ```\n2 | ## Prior Task Evidence\n3 | ````\n", section);
    }

    [Xunit.Fact]
    public void CapsPerFileAndTotalWithPathOnlyOverflow()
    {
        var root = CreateTempDirectory();
        var paths = Enumerable.Range(0, 6).Select(n => $"file{n}.txt").ToArray();
        foreach (var path in paths)
            Write(root, path, LargeSource(path));

        var section = Render(root, string.Join(' ', paths.Select(path => $"{path}:1-200")));

        Assert.NotNull(section);
        Assert.InRange(section.Length, 15000, 20000);
        var blocks = Regex.Matches(section, @"\n### [\s\S]*?\n```\n[\s\S]*?\n```\n");
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks.Cast<Match>(), block =>
        {
            Assert.InRange(block.Length, 1, 6000);
            Assert.Contains("[source truncated]", block.Value);
        });
        Assert.Contains("Total-cap truncation: 3 files omitted", section);
        foreach (var path in paths.Skip(3))
        {
            Assert.Contains($"- {path}\n", section);
            Assert.DoesNotContain($"### {path}", section);
            Assert.DoesNotContain($"payload-{path}", section);
        }
    }

    [Xunit.Fact]
    public void OverflowPathListIsSummarizedWithinTotalCap()
    {
        var root = CreateTempDirectory();
        var paths = Enumerable.Range(0, 400).Select(n => $"{n:D3}-{new string('p', 100)}.txt").ToArray();
        foreach (var path in paths)
            Write(root, path, LargeSource(path));

        var section = Render(root, string.Join('\n', paths.Select(path => $"`{path}:1-200`")));

        Assert.NotNull(section);
        Assert.InRange(section.Length, 18000, 20000);
        var excerptCount = Regex.Matches(section, "(?m)^### ").Count;
        var listedCount = Regex.Matches(section, "(?m)^- ").Count;
        Assert.Equal(3, excerptCount);
        Assert.True(listedCount > 0);
        Assert.Contains($"Total-cap truncation: {paths.Length - excerptCount} files omitted", section);
        Assert.EndsWith($"Further omitted files: {paths.Length - excerptCount - listedCount}.\n", section);
        Assert.Contains($"- {paths[excerptCount]}\n", section);
        Assert.DoesNotContain(paths[^1], section);
    }

    [Xunit.Theory]
    [Xunit.InlineData('x')]
    [Xunit.InlineData('`')]
    public void OversizedSingleLineIsPartialAndStillBounded(char character)
    {
        var root = CreateTempDirectory();
        Write(root, SourcePath, new string(character, 30000));

        var section = Render(root, $"{SourcePath}:1");

        Assert.NotNull(section);
        Assert.Contains("1 | " + new string(character, 3), section);
        Assert.Contains("[partial line]", section);
        Assert.Contains("[source truncated]", section);
        Assert.InRange(section[section.IndexOf("\n### ", StringComparison.Ordinal)..].Length, 1, 6000);
        var fences = Regex.Matches(section, "(?m)^`{3,}$");
        Assert.Equal(2, fences.Count);
        Assert.Equal(fences[0].Value, fences[1].Value);
        Assert.EndsWith(fences[1].Value + "\n", section);
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("directory")]
    [Xunit.InlineData("rooted")]
    [Xunit.InlineData("drive")]
    [Xunit.InlineData("unc")]
    [Xunit.InlineData("uri")]
    [Xunit.InlineData("escape")]
    [Xunit.InlineData("past-end")]
    [Xunit.InlineData("descending")]
    [Xunit.InlineData("zero")]
    [Xunit.InlineData("locked")]
    [Xunit.InlineData("other-syntax")]
    public void UnresolvableCitationsYieldNoExcerptAndNoException(string condition)
    {
        var parent = CreateTempDirectory();
        var root = Path.Combine(parent, "boundary");
        Directory.CreateDirectory(root);
        Write(root, SourcePath, Sentinel);
        Write(parent, "outside.txt", "outside sentinel");
        Directory.CreateDirectory(Path.Combine(root, "directory"));
        var citation = condition switch
        {
            "missing" => "missing.txt:1",
            "directory" => "directory:1",
            "rooted" => Path.Combine(parent, "outside.txt") + ":1",
            "drive" => "C:src/cited.txt:1",
            "unc" => @"\\server\share\src\cited.txt:1",
            "uri" => "https://host/src/cited.txt:1",
            "escape" => "../outside.txt:1",
            "past-end" => SourcePath + ":3",
            "descending" => SourcePath + ":3-1",
            "zero" => SourcePath + ":0",
            "other-syntax" => SourcePath + "#L1 " + SourcePath + "::Symbol " + SourcePath + ":1-invalid",
            _ => SourcePath + ":1"
        };
        using var locked = condition == "locked"
            ? File.Open(Path.Combine(root, SourcePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;

        Assert.Null(Render(root, citation));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void FilesystemLinksCannotEscapeTheWorkingDirectory(bool directoryLink)
    {
        var parent = CreateTempDirectory();
        var root = Path.Combine(parent, "boundary");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Write(outside, "secret.txt", "outside sentinel");
        var link = Path.Combine(root, "link");
        try
        {
            if (directoryLink)
                Directory.CreateSymbolicLink(link, outside);
            else
                File.CreateSymbolicLink(link, Path.Combine(outside, "secret.txt"));
        }
        catch (Exception error) when (error is UnauthorizedAccessException ||
            error is IOException && (error.HResult & 0xffff) == 1314)
        {
            Assert.Skip("Symbolic-link fixture requires OS privilege: " + error.Message);
        }
        try
        {
            var path = directoryLink ? "link/secret.txt" : "link";
            Assert.True(File.Exists(Path.Combine(root, path)), "The escaping link must resolve before testing containment.");
            Assert.Null(Render(root, path + ":1"));
            Write(root, SourcePath, Sentinel);
            var mixed = Render(root, path + ":1 " + SourcePath + ":1");
            Assert.NotNull(mixed);
            Assert.Contains(Sentinel, mixed);
            Assert.DoesNotContain("outside sentinel", mixed);
        }
        finally
        {
            if (directoryLink)
                Directory.Delete(link);
            else
                File.Delete(link);
        }
    }

    [Xunit.Fact]
    public void NoCitationsOrNoMeasuredPremiseYieldsNoSection()
    {
        var root = CreateTempDirectory();
        Write(root, SourcePath, Sentinel);
        Assert.Null(BriefSourceContextSection.Render(null, root, "head"));
        Assert.Null(Render(root, "No source citations."));
        Assert.Null(BriefSourceContextSection.Render("## Scope\n" + SourcePath + ":1", root, "head"));
        Assert.Null(BriefSourceContextSection.Render("```md\n## Measured premise\n" + SourcePath + ":1\n```", root, "head"));
    }

    [Xunit.Fact]
    public void InvalidCitationDoesNotSuppressValidExcerpts()
    {
        var root = CreateTempDirectory();
        Write(root, SourcePath, Sentinel);

        var section = Render(root, $"missing:1 ../outside:1 {SourcePath}:0 {SourcePath}:1");

        Assert.NotNull(section);
        Assert.Contains("1 | " + Sentinel, section);
        Assert.DoesNotContain("missing", section);
        Assert.DoesNotContain("Total-cap truncation", section);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Planner, true)]
    [Xunit.InlineData(AgentRole.Planner, false)]
    [Xunit.InlineData(AgentRole.Reviewer, true)]
    [Xunit.InlineData(AgentRole.Reviewer, false)]
    public void PlannerAndReviewerPromptsCarrySection(AgentRole role, bool typed)
    {
        var fixture = CreateFixture(role);

        var dispatch = Prepare(fixture, typed);
        var prompt = File.ReadAllText(dispatch.PromptPath);

        Assert.Contains("\n\n## Source context\n", prompt);
        Assert.Contains("1 | " + Sentinel, prompt);
        Assert.Contains("current at candidate commit " + fixture.Head, prompt);
        Assert.Equal(typed, dispatch.Task.LastDispatch?.ContextPackageReceipt is not null);
        if (dispatch.Task.LastDispatch?.ContextPackageReceipt is { } receipt)
        {
            Assert.Equal(prompt.Length, receipt.RenderedPromptCharacters);
            Assert.Equal(Encoding.UTF8.GetByteCount(prompt), receipt.RenderedPromptBytes);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer, true)]
    [Xunit.InlineData(AgentRole.Developer, false)]
    [Xunit.InlineData(AgentRole.Tester, true)]
    [Xunit.InlineData(AgentRole.Tester, false)]
    [Xunit.InlineData(AgentRole.Researcher, true)]
    [Xunit.InlineData(AgentRole.Researcher, false)]
    public void OtherRolesPromptsOmitSection(AgentRole role, bool typed)
    {
        var fixture = CreateFixture(role);
        Assert.NotNull(BriefSourceContextSection.Render(fixture.Goal.Objective, fixture.WorkingDirectory, fixture.Head));

        var prompt = File.ReadAllText(Prepare(fixture, typed).PromptPath);

        Assert.DoesNotContain("## Source context", prompt);
        Assert.DoesNotContain("1 | " + Sentinel, prompt);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Planner, true)]
    [Xunit.InlineData(AgentRole.Planner, false)]
    [Xunit.InlineData(AgentRole.Reviewer, true)]
    [Xunit.InlineData(AgentRole.Reviewer, false)]
    public void BriefPrefixIsUnchangedBySection(AgentRole role, bool typed)
    {
        var fixture = CreateFixture(role);
        var path = Path.Combine(fixture.WorkingDirectory, SourcePath);
        var absent = Path.Combine(fixture.Root, "temporarily-absent.txt");
        File.Move(path, absent);
        try
        {
            Prepare(fixture, typed); // Warm the stable recorded-dispatch projection.
            var without = File.ReadAllText(Prepare(fixture, typed).PromptPath);
            Assert.DoesNotContain("## Source context", without);
            File.Move(absent, path);

            var with = File.ReadAllText(Prepare(fixture, typed).PromptPath);

            Assert.StartsWith(without, with);
            Assert.StartsWith("\n\n## Source context\n", with[without.Length..]);
            Assert.Contains("1 | " + Sentinel, with[without.Length..]);
        }
        finally
        {
            if (File.Exists(absent))
                File.Move(absent, path);
        }
    }

    private static string? Render(string root, string citations) =>
        BriefSourceContextSection.Render("## Measured premise\n" + citations, root, "head");

    private static void Write(string root, string path, string content)
    {
        var fullPath = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static string LargeSource(string path) =>
        string.Join('\n', Enumerable.Repeat("payload-" + path + new string('x', 200), 200));

    private static Fixture CreateFixture(AgentRole role)
    {
        var root = CreateTempDirectory();
        var workingDirectory = CreateSeededDispatchRepository();
        // Source context reads the working tree; no extra commit or linked-worktree metadata is needed.
        Write(workingDirectory, SourcePath, Sentinel);
        Assert.True(InfrastructureTestSupport.TryReadGitHeadInProcess(workingDirectory, out var head));
        Assert.False(string.IsNullOrWhiteSpace(head));
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Use the supplied source context.", role);
        var goal = kernel.CreateGoal("## Measured premise\n`" + SourcePath + ":1`\n\n## Scope\nKeep the existing brief.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return new Fixture(root, workingDirectory, kernel, goal, task, head!);
    }

    private static WorkerProfileDispatchResult Prepare(Fixture fixture, bool typed)
    {
        var reviewer = fixture.Task.RequiredRole == AgentRole.Reviewer;
        return WorkerProfileDispatcher.PrepareTask(fixture.Kernel, fixture.Goal, fixture.Task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(fixture.Root, "prompts"), fixture.WorkingDirectory,
            DateTimeOffset.Parse("2026-11-17T12:00:00Z"),
            providerName: typed ? "OpenAI" : null,
            modelName: typed ? AgentCatalog.OpenAiSolSubscriptionModelAlias : null,
            allowPendingRecordedDispatchRefresh: true,
            reviewerScopeChangedFiles: reviewer ? [] : null,
            reviewerScopeMergeBase: reviewer ? fixture.Head : null,
            reviewerScopeTotalChangedFileCount: reviewer ? 0 : null,
            reviewerMergeTreeClean: reviewer ? true : null,
            reviewerMergeTreeConflictPaths: reviewer ? [] : null,
            reviewerMergeTreeTotalConflictPathCount: reviewer ? 0 : null,
            backlogStorePath: Path.Combine(fixture.Root, "backlog.db"));
    }

    private sealed record Fixture(string Root, string WorkingDirectory, AgentOrchestratorKernel Kernel,
        Goal Goal, TaskSpec Task, string Head);
}
