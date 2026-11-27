using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each test owns its database directory and captures its own console output via AsyncLocal.
public sealed class CliAttentionShowHistoryLimitTests : CliTaskQueryTestSupport, IDisposable
{
    private const string GoalPrefix = "a1100000";
    private const string QuestionPrefix = "    question: ";
    private const string SubjectPrefix = "    subject: ";
    private const string OmissionSuffix = " older rows omitted; use --limit <n> to widen the view.";

    private readonly string _root = CreateTempDirectory();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryKeepsNewestFiftyAfterFiltering(bool scoped)
    {
        var fixture = CreateFixture();
        await fixture.SeedAsync(55, 55);
        await fixture.Store.RaiseAsync(CollaborationItemType.Notice, fixture.Goal.Id.Value, "Excluded notice", "");
        if (scoped)
        {
            var other = fixture.Kernel.CreateGoal(new GoalId("b2200000000000000000000000000000"), "Other goal");
            fixture.Kernel.RequestHumanInput(other.Id, null, "Excluded question", HumanWaitKind.RiskReview);
            await fixture.Store.RaiseAsync(CollaborationItemType.Decision, other.Id.Value, "Excluded decision", "");
        }

        string[] args = scoped ? ["--all", "--goal", GoalPrefix] : ["--all"];
        var lines = Show(fixture, args);

        Assert.Contains("Human waits: 50 wait(s)", lines);
        Assert.Contains("Attention queue: 50 reach-up item(s)", lines);
        Assert.Equal(QuestionLines(6, 50), WithPrefix(lines, QuestionPrefix));
        Assert.Equal(await ItemLines(fixture, 50), WithPrefix(lines, SubjectPrefix));
        AssertOmissions(lines, 5, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryWithinLimitKeepsAllOutput(bool scoped)
    {
        var fixture = CreateFixture();
        await fixture.SeedAsync(3, 3);
        string[] args = scoped ? ["--goal", GoalPrefix, "--all"] : ["--all"];

        var lines = Show(fixture, args);
        var atLimit = Show(fixture, [.. args, "--limit", "3"]);

        Assert.Contains("Human waits: 3 wait(s)", lines);
        Assert.Contains("Attention queue: 3 reach-up item(s)", lines);
        Assert.Equal(QuestionLines(1, 3), WithPrefix(lines, QuestionPrefix));
        Assert.Equal(await ItemLines(fixture, 3), WithPrefix(lines, SubjectPrefix));
        Assert.DoesNotContain(lines, line => line.EndsWith(OmissionSuffix, StringComparison.Ordinal));
        // The renderer samples the current time for relative wait ages.
        Assert.Equal(WithoutWaitAges(lines), WithoutWaitAges(atLimit));
    }

    [Theory]
    [InlineData("--all")]
    [InlineData("--include-parked")]
    public async Task ExplicitLimitAppliesIndependentlyToBothSections(string historyFlag)
    {
        var fixture = CreateFixture();
        await fixture.SeedAsync(8, 12);

        var lines = Show(fixture, "--limit", "+5", historyFlag);

        Assert.Contains("Human waits: 5 wait(s)", lines);
        Assert.Contains("Attention queue: 5 reach-up item(s)", lines);
        Assert.Equal(QuestionLines(4, 5), WithPrefix(lines, QuestionPrefix));
        Assert.Equal(await ItemLines(fixture, 5), WithPrefix(lines, SubjectPrefix));
        AssertOmissions(lines, 3, 7);
    }

    [Fact]
    public async Task GlobalHistoryBoundsCollaborationEvenWithoutHumanWaits()
    {
        var fixture = CreateFixture();
        await fixture.SeedAsync(0, 55);

        var lines = Show(fixture, "--include-parked");

        Assert.DoesNotContain(lines, line => line.StartsWith("Human waits: ", StringComparison.Ordinal));
        Assert.Contains("Attention queue: 50 reach-up item(s)", lines);
        Assert.Equal(await ItemLines(fixture, 50), WithPrefix(lines, SubjectPrefix));
        Assert.Equal("5" + OmissionSuffix,
            Assert.Single(lines, line => line.EndsWith(OmissionSuffix, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("--limit", "5")]
    [InlineData("--goal", GoalPrefix, "--limit", "5")]
    [InlineData("--all", "--limit")]
    [InlineData("--all", "--limit", "")]
    [InlineData("--all", "--limit", "0")]
    [InlineData("--all", "--limit", "-3")]
    [InlineData("--all", "--limit", "abc")]
    [InlineData("--all", "--limit", "1.5")]
    [InlineData("--all", "--limit", "1e2")]
    [InlineData("--all", "--limit", "2147483648")]
    [InlineData("--all", "--limit", "5", "--limit", "6")]
    [InlineData("--all", "--limit=5")]
    [InlineData("--limit", "--all")]
    public void InvalidLimitReportsShowUsage(params string[] args)
    {
        var fixture = CreateFixture();
        var command = new[] { "attention", "show" }.Concat(args).ToArray();
        var error = Assert.Throws<ArgumentException>(() =>
        {
            CliCommandHelp.ThrowIfInvalidFlags(command);
            ExecuteCliAndCapture(command, fixture.Kernel, fixture.Workspace);
        });

        Assert.Contains("Usage: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("attention show [--all|--include-parked] [--limit <n>]", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultViewKeepsAllOpenRowsAndTheirExistingFormat(bool scoped)
    {
        var fixture = CreateFixture();
        await fixture.SeedAsync(60, 60, crossGoal: !scoped);
        string[] args = scoped ? ["--goal", GoalPrefix] : [];
        var before = await Snapshot(fixture);

        var lines = Show(fixture, args);

        Assert.Contains("Human waits: 60 active wait(s)", lines);
        Assert.Equal(QuestionLines(1, 60), WithPrefix(lines, QuestionPrefix));
        Assert.DoesNotContain(lines, line => line.EndsWith(OmissionSuffix, StringComparison.Ordinal));
        var items = await fixture.Store.GetAttentionQueueAsync();
        if (scoped)
        {
            var expected = items.Select(item => $"[{item.Id[..8]}] (Decision) {item.Subject}");
            Assert.Equal(expected, WithPrefix(lines, "["));
        }
        else
        {
            Assert.Contains("Attention queue: 60 open reach-up item(s)", lines);
            Assert.Equal(items.Select(item => SubjectPrefix + item.Subject), WithPrefix(lines, SubjectPrefix));
        }
        Assert.Equal(before, await Snapshot(fixture));
    }

    [Fact]
    public async Task HistoryAndInvalidLimitLeaveLegacyParkedStateUntouched()
    {
        var fixture = CreateFixture();
        const string legacyQuestion = "Goal parked: stale_resurrected_sweep";
        var wait = fixture.Kernel.RequestHumanInput(fixture.Goal.Id, null, legacyQuestion);
        Assert.Equal(GoalStatus.WaitingForHuman, fixture.Goal.Status);
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, fixture.Goal.Id.Value, "Legacy item", "");
        var before = await Snapshot(fixture);

        var error = Assert.Throws<ArgumentException>(() =>
            ExecuteCliAndCapture(["attention", "show", "--limit", "0"], fixture.Kernel, fixture.Workspace));
        Assert.Contains("Usage: attention show", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await Snapshot(fixture));

        string[][] commands =
        [
            ["attention", "show", "--all"],
            ["attention", "show", "--all", "--limit", "5", "--goal", GoalPrefix]
        ];
        foreach (var args in commands)
        {
            var result = ExecuteCliAndCaptureResult(args, fixture.Kernel, fixture.Workspace);
            var lines = Lines(result.Output);
            Assert.Contains(QuestionPrefix + legacyQuestion, lines);
            Assert.Contains(SubjectPrefix + "Legacy item", lines);
            Assert.False(result.Changed);
            Assert.Equal(before, await Snapshot(fixture));
        }

        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public void HelpDocumentsLimitAndItsHistoryRequirement()
    {
        var help = CaptureConsole(() => Assert.True(CliCommandHelp.TryPrintStartupHelp(["attention", "show", "--help"])));

        Assert.Contains("attention show [--all|--include-parked] [--limit <n>]", help, StringComparison.Ordinal);
        Assert.Contains("--limit <n> requires --all or --include-parked", help, StringComparison.Ordinal);
        Assert.Contains("per history section (default 50)", help, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundBreaksTiesByLaterPositionAndRestoresDisplayOrder()
    {
        var start = DateTimeOffset.UnixEpoch;
        var rows = new[]
        {
            (Label: "earlier tie", CreatedAt: start.AddMinutes(1)),
            (Label: "later tie", CreatedAt: start.AddMinutes(1)),
            (Label: "newest", CreatedAt: start.AddMinutes(2)),
            (Label: "oldest", CreatedAt: start)
        };

        var (retained, omitted) = AttentionHistoryLimit.Parse("2").Apply(rows, row => row.CreatedAt);

        Assert.Equal(new[] { "later tie", "newest" }, retained.Select(row => row.Label));
        Assert.Equal(2, omitted);
    }

    private string[] Show(Fixture fixture, params string[] args)
    {
        string[] command = ["attention", "show", .. args];
        CliCommandHelp.ThrowIfInvalidFlags(command);
        return Lines(ExecuteCliAndCapture(command, fixture.Kernel, fixture.Workspace));
    }

    private Fixture CreateFixture()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(_root);
        var clock = new SeedClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(new GoalId(GoalPrefix + new string('a', 24)), "Inspect attention history");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return new Fixture(workspace, kernel, goal, CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory), clock);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string ExecuteCliAndCapture(
        IReadOnlyList<string> args, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace) =>
        ExecuteCliAndCaptureResult(args, kernel, workspace).Output;

    private static (string Output, bool Changed) ExecuteCliAndCaptureResult(
        IReadOnlyList<string> args, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var providers = new InMemoryModelProviderRegistry([]);
        Goal? selected = null;
        var changed = false;
        var output = CaptureConsole(() => changed = CliCommandDispatcher.ExecuteCommand(
            args, kernel, workspace, ref agents, providers, ref profiles, ref selected));
        return (output, changed);
    }

    private static string[] Lines(string output) => output.Split(Environment.NewLine);

    private static IEnumerable<string> WithPrefix(string[] lines, string prefix) =>
        lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static IEnumerable<string> QuestionLines(int first, int count) =>
        Enumerable.Range(first, count).Select(index => $"{QuestionPrefix}question-{index:D3}");

    private static async Task<string[]> ItemLines(Fixture fixture, int count) =>
        (await fixture.Store.ListAsync(fixture.Goal.Id.Value))
            .Where(item => item.Type == CollaborationItemType.Decision)
            .TakeLast(count)
            .Select(item => SubjectPrefix + item.Subject)
            .ToArray();

    private static IEnumerable<string> WithoutWaitAges(string[] lines) =>
        lines.Select(line => line.StartsWith("  [RiskReview]", StringComparison.Ordinal)
            ? line[..line.IndexOf(" age=", StringComparison.Ordinal)]
            : line);

    private static async Task<string> Snapshot(Fixture fixture) =>
        JsonSerializer.Serialize(new { Kernel = fixture.Kernel.ExportSnapshot(), Items = await fixture.Store.ListAsync() });

    private static void AssertOmissions(string[] lines, int waits, int items)
    {
        var waitLine = waits + OmissionSuffix;
        var itemLine = items + OmissionSuffix;
        Assert.Equal(new[] { waitLine, itemLine },
            lines.Where(line => line.EndsWith(OmissionSuffix, StringComparison.Ordinal)));
        var queueHeader = Array.FindIndex(lines, line => line.StartsWith("Attention queue: ", StringComparison.Ordinal));
        Assert.InRange(Array.IndexOf(lines, waitLine),
            Array.FindLastIndex(lines, line => line.StartsWith("    resume: ", StringComparison.Ordinal)) + 1,
            queueHeader - 1);
        Assert.True(Array.LastIndexOf(lines, itemLine) >
            Array.FindLastIndex(lines, line => line.StartsWith("    raised: ", StringComparison.Ordinal)));
    }

    private sealed record Fixture(OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel,
        Goal Goal, CollaborationItemStore Store, SeedClock Clock)
    {
        internal async Task SeedAsync(int waits, int items, bool crossGoal = false)
        {
            for (var index = 1; index <= waits; index++)
            {
                Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
                Kernel.RequestHumanInput(Goal.Id, null, $"question-{index:D3}", HumanWaitKind.RiskReview);
            }

            for (var index = 1; index <= items; index++)
                await Store.RaiseAsync(CollaborationItemType.Decision, crossGoal ? null : Goal.Id.Value, $"item-{index:D3}", "");
        }
    }

    private sealed class SeedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
