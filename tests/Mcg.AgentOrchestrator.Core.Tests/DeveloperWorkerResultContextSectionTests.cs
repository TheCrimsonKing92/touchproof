using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: in-memory goals and an injected clock; no filesystem or process I/O.
public sealed class DeveloperWorkerResultContextSectionTests
{
    private const string Heading = "## Developer WORKER_RESULT for the candidate under review";
    private const string Candidate = "abcdef123456";
    private const string EndMarker = "END_WORKER_RESULT";

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void MatchingCandidate_ContextContainsVerbatimReportAndOwnAccount(AgentRole role)
    {
        var fixture = new Fixture();
        const string prose = "Diagnosis: preserve café and whitespace.\r\n  Cause confirmed.\n";
        var block = Block(Candidate, "matching report\t ", "\r\n");
        var completedAt = fixture.Clock.UtcNow;
        fixture.Report(0, prose + block + "\r\ntrailing-output-excluded");

        var source = fixture.Kernel.BuildTaskBriefSource(fixture.Goal.Id,
            fixture.Recipient(role).Id, targetHeadCommit: Candidate);
        var segment = Assert.Single(source.Segments.Where(segment =>
            segment.Lines.Any(line => line.StartsWith(Heading, StringComparison.Ordinal))));
        Assert.Null(segment.CollapsedLines);
        var section = JoinSection(segment.Lines);
        Assert.Contains(block, section, StringComparison.Ordinal);
        Assert.Contains("Source: Task 1 Developer verification", section, StringComparison.Ordinal);
        Assert.Contains(completedAt.ToString("O", CultureInfo.InvariantCulture), section);
        Assert.Contains("Developer's own account", section);
        Assert.Contains(prose, section, StringComparison.Ordinal);
        Assert.Contains("check it against the diff", section);
        Assert.Contains("not evidence that any claim is true", section);
        Assert.DoesNotContain("trailing-output-excluded", section);
        Assert.True(section.IndexOf(block, StringComparison.Ordinal) <
            section.IndexOf("Developer's own account", StringComparison.Ordinal));
        Assert.Equal(1, Count(source.ProjectLegacyMarkedTextV1(false).Content, Heading));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherCommit_OlderOrNewerReportCannotHideCandidate(bool otherIsNewer)
    {
        var fixture = new Fixture();
        var matching = Block(Candidate, "candidate-only-marker");
        var other = Block("different-commit", "other-candidate-marker");
        fixture.Report(otherIsNewer ? 0 : 1, otherIsNewer ? matching : other);
        fixture.Clock.Advance();
        fixture.Report(otherIsNewer ? 1 : 0, otherIsNewer ? other : matching);

        var section = fixture.Section();
        Assert.Contains(matching, section, StringComparison.Ordinal);
        Assert.DoesNotContain("other-candidate-marker", section);
        Assert.Contains("Source: Task 1", section);
    }

    [Fact]
    public void MatchingHistory_SelectsLatestVerificationThenDescendingTaskNumber()
    {
        var fixture = new Fixture();
        fixture.Report(0, Block(Candidate, "old-round"));
        fixture.Clock.Advance();
        fixture.Report(0, Block(Candidate, "new-round"));
        var latest = fixture.Section();
        Assert.Contains("new-round", latest);
        Assert.DoesNotContain("old-round", latest);

        // Equal completion times must choose the higher task number, not insertion order.
        fixture.Report(1, Block(Candidate, "tie-winner"));
        fixture.Report(0, Block(Candidate, "tie-loser"));
        var tied = fixture.Section();
        Assert.Contains("tie-winner", tied);
        Assert.DoesNotContain("tie-loser", tied);
        Assert.Contains("Source: Task 2", tied);
    }

    [Fact]
    public void NoMatch_OneLineNamesNewestIdentifiableDeveloperCommit()
    {
        var fixture = new Fixture();
        fixture.Report(0, Block("old-commit", "old-output"));
        fixture.Clock.Advance();
        fixture.Report(1, Block("newest-commit", "new-output"));
        fixture.Clock.Advance();
        fixture.Report(2, Block("none", "unknown-output"), resultCommit: null);

        var section = fixture.Section();
        Assert.StartsWith(Heading, section);
        Assert.DoesNotContain("\r", section);
        Assert.DoesNotContain("\n", section);
        Assert.Contains($"no completed Developer verification matches candidate {Candidate}", section);
        Assert.Contains("newest Developer commit found: newest-commit (Task 2)", section);
        Assert.Contains("checked against the diff, not evidence a claim is true", section);
        Assert.DoesNotContain("old-commit", section);
        Assert.DoesNotContain("new-output", section);
    }

    [Fact]
    public void NoDeveloperHistory_ExplicitCandidateReportsNoCompletedCommit()
    {
        var fixture = new Fixture();
        var section = fixture.Section();
        Assert.Contains("no completed Developer commit was found", section);
        Assert.DoesNotContain("\n", section);
    }

    [Theory]
    [InlineData("  ABCDEF123456  ", "different-dispatch-result")]
    [InlineData("none", Candidate)]
    public void CommitIdentity_UsesWorkerFieldThenResultCommit(string commit, string result)
    {
        var fixture = new Fixture();
        var block = Block(commit, "commit-precedence-marker");
        fixture.Report(0, block, resultCommit: result);

        Assert.Contains(block, fixture.Section("  ABCDEF123456  "), StringComparison.Ordinal);
    }

    [Fact]
    public void BaseCommitOnly_CannotMatchCandidate()
    {
        var fixture = new Fixture();
        fixture.Report(0, Block("none", "base-is-not-result"), resultCommit: null);
        var section = fixture.Section();
        Assert.Contains("no completed Developer verification matches", section);
        Assert.DoesNotContain("base-is-not-result", section);
    }

    [Theory]
    [InlineData(10000, 40, false)]
    [InlineData(3000, 5000, true)]
    public void OversizedOutput_KeepsBlockAndCountsAllExcludedProse(
        int proseLength, int payloadLength, bool sectionBudgetCutsProse)
    {
        var fixture = new Fixture();
        var prose = new string('p', proseLength - 14) + "PROSE-SUFFIX\r\n";
        var block = Block(Candidate, new string('b', payloadLength));
        fixture.Report(0, prose + block + "\nnot-part-of-omitted-count");

        var section = fixture.Section();
        Assert.InRange(section.Length, 1, 6000);
        Assert.Contains(block, section, StringComparison.Ordinal);
        var retained = Number(section,
            @"Developer's own account \(last (\d+) characters before WORKER_RESULT\):");
        Assert.InRange(retained, 1, 2000);
        Assert.Equal(prose.Length - retained,
            Number(section, @"Omitted: (\d+) characters of Developer output before WORKER_RESULT\."));
        Assert.Contains(prose[^retained..], section, StringComparison.Ordinal);
        Assert.DoesNotContain("not-part-of-omitted-count", section);
        if (sectionBudgetCutsProse)
        {
            Assert.True(retained < 2000);
            Assert.InRange(section.Length, 5998, 6000);
        }
        else
        {
            Assert.Equal(2000, retained);
        }
    }

    [Fact]
    public void EightThousandCharacterBlock_KeptWholeWithoutProse()
    {
        var fixture = new Fixture();
        const string prose = "PROSE-MUST-BE-DROPPED\n";
        var block = Block(Candidate, new string('b', 8000));
        fixture.Report(0, prose + block);

        var section = fixture.Section();
        Assert.InRange(section.Length, 6001, 12000);
        Assert.Contains(block, section, StringComparison.Ordinal);
        Assert.DoesNotContain("Developer's own account", section);
        Assert.DoesNotContain("PROSE-MUST-BE-DROPPED", section);
        Assert.DoesNotContain("WORKER_RESULT cut:", section);
        Assert.Equal(prose.Length,
            Number(section, @"Omitted: (\d+) characters of Developer output before WORKER_RESULT\."));
    }

    [Fact]
    public void FifteenThousandCharacterBlock_CutsOnlyEndAndStatesExactCount()
    {
        var fixture = new Fixture();
        const string prose = "drop this account\n";
        var block = Block(Candidate, new string('b', 15000) + "body-tail-must-go");
        fixture.Report(0, prose + block);

        var section = fixture.Section();
        Assert.InRange(section.Length, 11998, 12000);
        Assert.DoesNotContain("Developer's own account", section);
        Assert.DoesNotContain("body-tail-must-go", section);
        var blockStart = section.IndexOf("WORKER_RESULT:\n", StringComparison.Ordinal);
        var cutStart = section.IndexOf(Environment.NewLine + "WORKER_RESULT cut:",
            blockStart, StringComparison.Ordinal);
        Assert.True(blockStart >= 0 && cutStart > blockStart);
        var retainedBody = section[blockStart..cutStart];
        Assert.StartsWith(retainedBody, block, StringComparison.Ordinal);
        var originalBodyLength = block.Length - EndMarker.Length;
        Assert.Equal(originalBodyLength - retainedBody.Length,
            Number(section, @"WORKER_RESULT cut: (\d+) characters omitted from the end"));
        Assert.Contains("characters omitted from the end" + Environment.NewLine + EndMarker, section);
        Assert.Equal(prose.Length,
            Number(section, @"Omitted: (\d+) characters of Developer output before WORKER_RESULT\."));
    }

    [Fact]
    public void MultipleBlocks_UsesLastCompleteBlockAndImmediateProse()
    {
        var fixture = new Fixture();
        var first = Block(Candidate, "first-block");
        var last = Block(Candidate, "last-block", "\r\n");
        var prose = first + "\nDiagnosis immediately before last block.\n";
        fixture.Report(0, prose + last + "\nignored trailing account");

        var section = fixture.Section();
        var accountStart = section.IndexOf("Developer's own account", StringComparison.Ordinal);
        Assert.True(accountStart > 0);
        Assert.Contains(last, section[..accountStart], StringComparison.Ordinal);
        Assert.DoesNotContain(first, section[..accountStart]);
        Assert.Contains(prose, section[accountStart..], StringComparison.Ordinal);
        Assert.DoesNotContain("ignored trailing account", section);
    }

    [Fact]
    public void MatchingVerificationWithoutCompleteBlock_ReportsUnavailable()
    {
        var fixture = new Fixture();
        fixture.Report(0, "Diagnosis only; no block.", resultCommit: Candidate);
        var section = fixture.Section();
        Assert.Contains("Source: Task 1 Developer verification", section);
        Assert.Contains("Complete WORKER_RESULT unavailable for this verification.", section);
        Assert.DoesNotContain("no completed Developer verification matches", section);
    }

    [Fact]
    public void AuthoritativeOutput_UsedInsteadOfBoundedPreview()
    {
        var fixture = new Fixture();
        var full = "Full diagnosis before report.\n" + Block(Candidate, "authoritative-marker");
        fixture.Report(0, Block(Candidate, "preview-marker"), fullOutput: full);
        var section = fixture.Section();
        Assert.Contains(Block(Candidate, "authoritative-marker"), section, StringComparison.Ordinal);
        Assert.Contains("Full diagnosis before report.\n", section, StringComparison.Ordinal);
        Assert.DoesNotContain("preview-marker", section);
    }

    private static string Block(string commit, string payload, string newline = "\n") =>
        string.Join(newline, "WORKER_RESULT:", "files: none", "commands: none",
            "tests: deferred - DeveloperWorkerResultContextSectionTests", $"commit: {commit}",
            "blockers: none", $"model_fit: {payload}", "skills: none", "confidence: high", EndMarker);

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static int Number(string section, string pattern)
    {
        var match = Regex.Match(section, pattern, RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Missing count matching {pattern}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string JoinSection(IReadOnlyList<string> lines)
    {
        Assert.NotEmpty(lines);
        Assert.Equal(string.Empty, lines[^1]);
        return string.Join(Environment.NewLine, lines.Take(lines.Count - 1));
    }

    private sealed class Fixture
    {
        internal FakeClock Clock { get; } = new();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }

        internal Fixture()
        {
            Kernel = new AgentOrchestratorKernel(Clock);
            Goal = Kernel.CreateGoal("Expose the candidate's Developer report",
                [new(TaskId.New(), "Implement first", AgentRole.Developer),
                 new(TaskId.New(), "Implement second", AgentRole.Developer),
                 new(TaskId.New(), "Implement third", AgentRole.Developer),
                 new(TaskId.New(), "Test candidate", AgentRole.Tester),
                 new(TaskId.New(), "Review candidate", AgentRole.Reviewer)]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
        }

        internal TaskSpec Recipient(AgentRole role) => Goal.Tasks.Single(task => task.RequiredRole == role);

        internal void Report(int index, string output, string? resultCommit = Candidate,
            string? fullOutput = null)
        {
            var developer = Goal.Tasks[index];
            if (developer.LastDispatch is null)
            {
                Kernel.RecordTaskDispatch(Goal.Id, developer.Id, new TaskDispatchRecord(
                    "fixture", "fixture developer", "C:\\repo", Clock.UtcNow,
                    BaseCommit: Candidate, ResultCommit: resultCommit));
            }
            Kernel.RecordTaskVerification(Goal.Id, developer.Id, new TaskVerificationRecord(
                "fixture developer", "C:\\repo", 0, output, string.Empty, Clock.UtcNow,
                FullStandardOutput: fullOutput));
            Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        }

        internal string Section(string? candidate = Candidate) =>
            JoinSection(DeveloperWorkerResultContextSection.Build(Goal,
                Recipient(AgentRole.Reviewer), candidate));
    }
}
