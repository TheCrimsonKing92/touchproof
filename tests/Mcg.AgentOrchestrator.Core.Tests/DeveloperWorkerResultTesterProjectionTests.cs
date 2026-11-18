using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: in-memory goals and an injected clock; no filesystem or process I/O.
public sealed class DeveloperWorkerResultTesterProjectionTests
{
    private const string Candidate = "abcdef123456";
    private const string FullResultPath = @"C:\fixture\out.log";
    private const string Prose = "Diagnosis: preserve café and whitespace.\r\n  Cause confirmed.\n";
    private const string Files = "files: src/Changed.cs, tests/ChangedTests.cs";
    private const string Tests = "tests: deferred - ChangedTests";
    private const string Claims = "criteria_self_check: [{\"criterion_index\":0,\"status\":\"proven\",\"evidence\":\"ChangedTests.Example\"}]";

    [Fact]
    public void Tester_MatchingCandidate_RendersStructuredFieldsAndFullResultPath()
    {
        var fixture = new Fixture();
        var block = Block();
        fixture.Report(Prose + block + "\ntrailing-output-excluded");

        var section = fixture.Section(AgentRole.Tester);

        Assert.Equal(string.Join(Environment.NewLine,
            Framing(), Files, Tests, Claims, "blockers: none", $"Full result file: {FullResultPath}"), section);
        Assert.DoesNotContain(block, section, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_RESULT:", section);
        Assert.DoesNotContain("END_WORKER_RESULT", section);
        Assert.DoesNotContain("Developer's own account", section);
        Assert.DoesNotContain(Prose, section);
        Assert.DoesNotContain("trailing-output-excluded", section);
    }

    [Fact]
    public void Reviewer_SameInputs_RendersExistingFormByteForByte()
    {
        var fixture = new Fixture();
        fixture.Report(Prose + Block() + "\ntrailing-output-excluded");

        // Fixed baseline from the existing section tests' heading, block and prose expectations.
        var expected = string.Join(Environment.NewLine,
            "## Developer WORKER_RESULT for the candidate under review",
            "Source: Task 1 Developer verification completed 2026-06-01T12:00:00.0000000+00:00; verified commit abcdef123456; candidate abcdef123456.",
            "This is the Developer's report; check it against the diff. It is not evidence that any claim is true.",
            Block(),
            $"Developer's own account (last {Prose.Length} characters before WORKER_RESULT):",
            Prose);

        Assert.Equal(expected, fixture.Section(AgentRole.Reviewer));
        Assert.NotEqual(expected, fixture.Section(AgentRole.Tester));
    }

    [Fact]
    public void Tester_MarkerOutsideStructuredFields_IsExcludedAndPathNamed()
    {
        var fixture = new Fixture();
        var marker = "NEGATIVE-CONTROL-UNSELECTED-BODY-" + new string('x', 250);
        var block = string.Join("\n", "WORKER_RESULT:", Files, "commands: source inspection",
            marker, Tests, "blockers: none", Claims, $"model_fit: {marker}", "END_WORKER_RESULT");
        fixture.Report(block);

        var section = fixture.Section(AgentRole.Tester);

        Assert.DoesNotContain(marker, section, StringComparison.Ordinal);
        Assert.Contains($"Full result file: {FullResultPath}", section, StringComparison.Ordinal);
        Assert.Contains(Files, section);
        Assert.Contains(Tests, section);
        Assert.Contains(Claims, section);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tester_MissingStandardOutputPath_StatesUnavailableAndKeepsFields(string? path)
    {
        var fixture = new Fixture();
        fixture.Report(Block(), path);

        var section = fixture.Section(AgentRole.Tester);

        Assert.Equal(string.Join(Environment.NewLine,
            Framing(), Files, Tests, Claims, "blockers: none", "Full result file unavailable."), section);
        Assert.Equal(1, section.Split("Full result file unavailable.", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Full result file: ", section);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Tester_FieldsKeepContinuationsEmphasisEmptyValuesAndOrder(string newline)
    {
        var fixture = new Fixture();
        fixture.Report(string.Join(newline, "WORKER_RESULT:", "unselected preamble",
            "blockers:", "  issue: waiting for evidence", "commands: omitted",
            "  unrelated: command detail", "**criteria_self_check:** []", "  criterion 0: first claim",
            "criteria_self_check: []", "  criterion 1: second claim", "assigned_scope_complete: true",
            "**tests:** deferred - ChangedTests", "  receipt: C:\\fixture\\test.log", "commit: " + Candidate,
            "files: none", "  details: explicit no changes", "confidence: high", "END_WORKER_RESULT"));

        Assert.Equal(string.Join(Environment.NewLine, Framing(),
            "files: none", "  details: explicit no changes",
            "**tests:** deferred - ChangedTests", "  receipt: C:\\fixture\\test.log",
            "**criteria_self_check:** []", "  criterion 0: first claim",
            "criteria_self_check: []", "  criterion 1: second claim",
            "blockers:", "  issue: waiting for evidence", $"Full result file: {FullResultPath}"),
            fixture.Section(AgentRole.Tester));
    }

    [Fact]
    public void Tester_AbsentFields_AreNotInvented()
    {
        var fixture = new Fixture();
        fixture.Report("WORKER_RESULT:\nfiles:\ncommands: none\nEND_WORKER_RESULT");

        Assert.Equal(string.Join(Environment.NewLine, Framing(), "files:",
            $"Full result file: {FullResultPath}"), fixture.Section(AgentRole.Tester));
    }

    [Fact]
    public void Tester_LongFieldsAndRecordedPath_AreNotClippedOrResolved()
    {
        var fixture = new Fixture();
        var files = "files: " + new string('f', 13_000);
        var path = " relative/" + new string('p', 500) + ".log ";
        var fullOutput = string.Join("\n", "WORKER_RESULT:", files, Tests, Claims,
            "blockers: none", "END_WORKER_RESULT");
        fixture.Report(Block(), path, fullOutput);

        Assert.Equal(string.Join(Environment.NewLine, Framing(), files, Tests, Claims,
            "blockers: none", $"Full result file: {path}"), fixture.Section(AgentRole.Tester));
    }

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void MatchingReportWithoutBlock_KeepsUnavailableLine(AgentRole role)
    {
        var fixture = new Fixture();
        fixture.Report("No complete structured report.");

        Assert.Equal(Framing() + Environment.NewLine + "Complete WORKER_RESULT unavailable for this verification.",
            fixture.Section(role));
    }

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void OtherCandidate_KeepsNoMatchLine(AgentRole role)
    {
        var fixture = new Fixture();
        fixture.Report(Block());

        Assert.Equal("## Developer WORKER_RESULT for the candidate under review — no completed Developer verification matches candidate other; newest Developer commit found: abcdef123456 (Task 1). Developer reports are checked against the diff, not evidence a claim is true.",
            fixture.Section(role, "other"));
    }

    private static string Block() => string.Join("\n", "WORKER_RESULT:", Files, "commands: none",
        Tests, $"commit: {Candidate}", "blockers: none", "assigned_scope_complete: true", Claims,
        "model_fit: fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");

    private static string Framing() => string.Join(Environment.NewLine,
        "## Developer WORKER_RESULT for the candidate under review",
        "Source: Task 1 Developer verification completed 2026-06-01T12:00:00.0000000+00:00; verified commit abcdef123456; candidate abcdef123456.",
        "This is the Developer's report; check it against the diff. It is not evidence that any claim is true.");

    private sealed class Fixture
    {
        private readonly FakeClock _clock = new();
        private readonly AgentOrchestratorKernel _kernel;
        private readonly Goal _goal;

        internal Fixture()
        {
            _kernel = new AgentOrchestratorKernel(_clock);
            _goal = _kernel.CreateGoal("Project Developer report for Tester",
                [new(TaskId.New(), "Implement", AgentRole.Developer),
                 new(TaskId.New(), "Test candidate", AgentRole.Tester),
                 new(TaskId.New(), "Review candidate", AgentRole.Reviewer)]);
            _kernel.ActivateGoal(_goal.Id, DefaultAgents());
        }

        internal void Report(string output, string? standardOutputPath = FullResultPath, string? fullOutput = null)
        {
            var developer = _goal.Tasks[0];
            _kernel.RecordTaskDispatch(_goal.Id, developer.Id, new TaskDispatchRecord(
                "fixture", "fixture developer", "C:\\repo", _clock.UtcNow,
                BaseCommit: Candidate, ResultCommit: Candidate));
            _kernel.RecordTaskVerification(_goal.Id, developer.Id, new TaskVerificationRecord(
                "fixture developer", "C:\\repo", 0, output, string.Empty, _clock.UtcNow,
                StandardOutputPath: standardOutputPath, FullStandardOutput: fullOutput));
            Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        }

        internal string Section(AgentRole role, string candidate = Candidate)
        {
            var lines = DeveloperWorkerResultContextSection.Build(_goal,
                _goal.Tasks.Single(task => task.RequiredRole == role), candidate);
            Assert.NotEmpty(lines);
            Assert.Equal(string.Empty, lines[^1]);
            return string.Join(Environment.NewLine, lines.Take(lines.Count - 1));
        }
    }
}
