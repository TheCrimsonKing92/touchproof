using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalCitedDefectAdvisoryTests
{
    private const string Expected = "DEPENDENCY_SUGGESTION goal=11111111 cited=abcdef01 status=Draft command=goal-depends abcdef01 --on 11111111";

    [Theory]
    [InlineData("## Measured premise\n- abcdef01 ran three times.", true)]
    [InlineData("## Measured premise\n- abcdef01 attempt failed.", true)]
    [InlineData("## Measured premise\n- abcdef01 RUNNING.", true)]
    [InlineData("## Measured premise\n- abcdef01 is in\n  flight.", true)]
    [InlineData("## Measured premise\n- abcdef01 burning resources.", true)]
    [InlineData("## Measured premise\n- abcdef01 consumed host\n  time.", true)]
    [InlineData("## Measured premise\n- abcdef01 used a paid round.", true)]
    [InlineData("## Measured premise\n- abcdef01 measured latency.", false)]
    [InlineData("## Evidence\n- abcdef01 ran three times.", false)]
    [InlineData("Measured premise: abcdef01 ran three times.", false)]
    [InlineData("## Measured premise###\n- abcdef01 ran.", false)]
    [InlineData("## Measured premise::\n- abcdef01 ran.", false)]
    [InlineData("## Measured premise\n## Evidence\n- abcdef01 ran.", false)]
    [InlineData("## Measured premise\n### Detail\n- abcdef01 ran.", true)]
    [InlineData("## MEASURED PREMISE: ##\n- abcdef01 ran.", true)]
    [InlineData("Measured premise:\n=================\n- abcdef01 ran.", true)]
    [InlineData("Measured premise\n----------------\n- abcdef01 ran.", true)]
    [InlineData("## Measured premise\n- measured only.\n## Other\n## Measured premise\n- abcdef01 ran.", true)]
    [InlineData("## Measured premise\n- abcdef01000000000000000000000001 ran.", true)]
    [InlineData("## Measured premise\n- ABCDEF01 ran.", true)]
    [InlineData("## Measured premise\n- `abcdef01` ran.", true)]
    [InlineData("## Measured premise\n- `[source](abcdef01)` ran.", true)]
    [InlineData("## Measured premise\n- ``[source](abcdef01)`` ran.", true)]
    [InlineData("## Measured premise\n- [abcdef01](https://example.test) ran.", true)]
    [InlineData("## Measured premise\n- [source](https://example.test/abcdef01) ran.", false)]
    [InlineData("## Measured premise\n- [source](https://example.test/(abcdef01)) ran.", false)]
    [InlineData("## Measured premise\n- [source](\n  https://example.test/abcdef01) ran.", false)]
    [InlineData("## Measured premise\n- [source][abcdef01] ran.", false)]
    [InlineData("## Measured premise\n- <https://example.test/abcdef01> ran.", false)]
    [InlineData("## Measured premise\n[source]: https://example.test/abcdef01 ran", false)]
    [InlineData("## Measured premise\n```\n- abcdef01 ran.\n```", false)]
    [InlineData("```markdown\n## Measured premise\n```\n- abcdef01 ran.", false)]
    [InlineData("## Measured premise\n~~~\n## Evidence\n~~~\n- abcdef01 ran.", true)]
    [InlineData("## Measured premise\nabcdef01 ran a long\nattempt.", true)]
    [InlineData("## Measured premise\nabcdef01 measured latency. Another goal ran.", false)]
    [InlineData("## Measured premise\nAnother goal ran! abcdef01 measured latency.", false)]
    [InlineData("## Measured premise\nabcdef01 measured latency? Another goal ran.", false)]
    [InlineData("## Measured premise\nabcdef01 measured latency\n\nAnother goal ran.", false)]
    [InlineData("## Measured premise\n- abcdef01 measured latency. Another sentence says ran.", true)]
    [InlineData("## Measured premise\n- abcdef01 measured latency.\n  Wrapped continuation ran.", true)]
    [InlineData("## Measured premise\n- abcdef01 measured latency.\n  - Another goal ran.", false)]
    [InlineData("## Measured premise\n- Another goal ran.\n  - abcdef01 measured latency.", false)]
    [InlineData("## Measured premise\n- Another goal measured latency.\n  - abcdef01 ran.", true)]
    [InlineData("## Measured premise\n1. abcdef01 measured latency.\n   Continuation ran.", true)]
    [InlineData("## Measured premise\n- abcdef01 transient measurement.", false)]
    [InlineData("## Measured premise\n- abcdef01 attempts failed.", false)]
    [InlineData("## Measured premise\n- abcdef01 used paid rounds.", false)]
    [InlineData("## Measured premise\n- abcdef01 in-flight.", false)]
    [InlineData("## Measured premise\n- xabcdef01 ran.", false)]
    [InlineData("## Measured premise\n- abcdef01_ ran.", false)]
    [InlineData("## Measured premise\n- abcdef010 ran.", false)]
    [InlineData("## Measured premise\n- abcdef010000000000000000000000010 ran.", false)]
    [InlineData("## Measured premise\n- abcdef01000000000000000000000002 ran.", false)]
    [InlineData("## Measured premise\n- deadbeef ran.", false)]
    [InlineData("## Measured premise\n- abcdef01 measured latency.\n- abcdef01 ran.", true)]
    [InlineData("## Measured premise\n- abcdef01 ran. abcdef01 ran again.", true)]
    public void CitationContexts(string brief, bool suggests)
    {
        using var fixture = new Fixture();
        var stderr = fixture.Run(brief);
        Assert.Equal(suggests ? Expected + Environment.NewLine : "", stderr);
        string[] expected = suggests ? [Expected] : [];
        Assert.Equal(expected, fixture.EventDetails());
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("self")]
    [InlineData("new-depends-on-cited")]
    [InlineData("cited-depends-on-new")]
    [InlineData("ambiguous")]
    [InlineData("ambiguous-terminal")]
    public void IneligibleGoalsAreSilent(string scenario)
    {
        using var fixture = new Fixture();
        var citation = "abcdef01";
        if (scenario == "terminal") fixture.Kernel.CancelGoal(fixture.Cited.Id, "done");
        if (scenario == "self") citation = "11111111";
        if (scenario == "new-depends-on-cited") fixture.Kernel.SetGoalDependency(fixture.NewGoal.Id, fixture.Cited.Id);
        if (scenario == "cited-depends-on-new") fixture.Kernel.SetGoalDependency(fixture.Cited.Id, fixture.NewGoal.Id);
        if (scenario.StartsWith("ambiguous", StringComparison.Ordinal))
        {
            var other = fixture.Kernel.CreateGoal(new GoalId("abcdef01000000000000000000000002"), "Other");
            if (scenario == "ambiguous-terminal") fixture.Kernel.CancelGoal(other.Id, "done");
        }
        Assert.Equal("", fixture.Run($"## Measured premise\n- {citation} ran."));
        Assert.Empty(fixture.EventDetails());
    }

    [Fact]
    public void SuggestionsFollowFirstQualifyingOccurrence()
    {
        using var fixture = new Fixture();
        fixture.Kernel.CreateGoal(new GoalId("22222222000000000000000000000001"), "Second");
        var stderr = fixture.Run("## Measured premise\n- abcdef01 measurement only.\n- Parent ran.\n  - 22222222 ran.\n  abcdef01 ran.\n- 22222222 ran again.");
        string[] expected = [
            "DEPENDENCY_SUGGESTION goal=11111111 cited=22222222 status=Draft command=goal-depends 22222222 --on 11111111",
            Expected];
        Assert.Equal(string.Join(Environment.NewLine, expected) + Environment.NewLine, stderr);
        Assert.Equal(expected, fixture.EventDetails());
    }

    [Fact]
    public void ReaderFailureOnlyReportsUnavailable()
    {
        using var fixture = new Fixture();
        var stderr = fixture.Run("## Measured premise\n- abcdef01 ran.",
            () => throw new IOException("reader failed\r\nsecond line"));
        Assert.Equal("Dependency suggestion advisory unavailable: reader failed  second line" + Environment.NewLine, stderr);
        Assert.Empty(fixture.EventDetails());
    }

    [Fact]
    public void LogFailurePreservesAllSuggestions()
    {
        using var fixture = new Fixture();
        fixture.Kernel.CreateGoal(new GoalId("22222222000000000000000000000001"), "Second");
        Directory.CreateDirectory(fixture.LogPath);
        var stderr = fixture.Run("## Measured premise\n- abcdef01 ran.\n- 22222222 ran.");
        var lines = stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(Expected, lines[0]);
        Assert.Equal("DEPENDENCY_SUGGESTION goal=11111111 cited=22222222 status=Draft command=goal-depends 22222222 --on 11111111", lines[1]);
        Assert.StartsWith("Dependency suggestion advisory unavailable: ", lines[2]);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly Goal NewGoal;
        internal readonly Goal Cited;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cited-defect-advisory-" + Guid.NewGuid().ToString("N"));
        internal string LogPath => Path.Combine(_root, "conduct-events.jsonl");

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            NewGoal = Kernel.CreateGoal(new GoalId("11111111000000000000000000000001"), "Fix");
            Cited = Kernel.CreateGoal(new GoalId("abcdef01000000000000000000000001"), "Cited");
        }

        internal string Run(string brief, Func<IEnumerable<Goal>>? reader = null)
        {
            var before = Kernel.Goals.ToDictionary(goal => goal.Id, goal => goal.DependsOn.OrderBy(id => id.Value).ToArray());
            var stderr = InfrastructureTestSupport.CaptureConsoleError(() =>
                GoalCitedDefectAdvisory.Warn(brief, reader ?? (() => Kernel.Goals), NewGoal, LogPath));
            foreach (var goal in Kernel.Goals)
                Assert.Equal(before[goal.Id], goal.DependsOn.OrderBy(id => id.Value).ToArray());
            return stderr;
        }

        internal string[] EventDetails() => !File.Exists(LogPath) ? [] : File.ReadAllLines(LogPath).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal("dependency-suggestion", json.RootElement.GetProperty("eventKind").GetString());
            Assert.Equal("11111111", json.RootElement.GetProperty("goalId").GetString());
            return json.RootElement.GetProperty("detail").GetString()!;
        }).ToArray();

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
