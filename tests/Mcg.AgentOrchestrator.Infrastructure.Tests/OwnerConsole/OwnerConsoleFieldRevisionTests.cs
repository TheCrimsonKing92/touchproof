using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.ReadModels;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: detached typed fixtures, instance-owned views and injected clipboard writes.
public sealed class OwnerConsoleFieldRevisionTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void MultilineRowsOwnEveryLogicalAndWrappedLine(string newline)
    {
        var value = "Rule 日本語" + newline + "second line that wraps at narrow width" + newline + "last";
        var lesson = OwnerConsoleFieldSnapshot.Create("lesson/full", new OwnerConsoleField("rule", "Rule", value));
        var fallback = OwnerConsoleFieldSnapshot.Create("digest", new OwnerConsoleField("count", "Count", "2"));
        using var writer = new OwnerConsoleStructuredWriter();
        writer.WriteLine("Header" + newline + "subheading");
        writer.Mark(lesson); writer.WriteLine(value);
        writer.WriteLine("Unmarked summary");
        var second = OwnerConsoleFieldSnapshot.Create("second", new OwnerConsoleField("value", "Value", "next"));
        writer.Mark(second); writer.WriteLine("next");
        var metadata = writer.Build(new(fallback))!;
        using var text = new OwnerConsoleTextDialog("Digest", writer.ToString()) { Structured = metadata };
        text.Format(12, 8);
        var opened = new List<OwnerConsoleFieldSnapshot>();
        text.FieldsRequested += opened.Add;
        for (var i = 0; i < text.Page.Lines.Count; i++)
        {
            text.Page.Select(i);
            Assert.True(text.HandleKey(OwnerConsoleBindings.OpenFields));
            var source = text.Page.SourceLines[i];
            Assert.Equal(source is >= 2 and <= 4 ? "lesson/full" : source == 6 ? "second" : "digest", opened[^1].Identity);
        }
        Assert.Equal(value, Copy(lesson, "rule"));
    }

    [Fact]
    public void DigestExposesEscapeSourceAndAuthoritativeMechanicalTotal()
    {
        var row = new OwnerDigestGoalRow("goal/full", At, "revision", new(1, 2, 3), "escaped", 0.5,
            new(1.25, 2.5, 3.75), 0, 1, "found-by/full 日本語");
        var digest = new OwnerDigestResult(At, At.AddDays(1), "none", [row],
            new(1, new(1, 2, 3), 6, 0, 1, 0, 0, 0.5, 0.5, 1, 0, row.MechanicalHours, 0), 0);
        var fields = CliOwnerDigestConsoleAdapter.DigestFields(digest);
        Assert.Equal(row.EscapeSource, Copy(fields.Rows![2], "escape-source"));
        Assert.Equal("7.5", Copy(fields.Detail!, "mechanical/total"));
    }

    [Fact]
    public void StatusExposesDisplayedCountsAndSupervisorFlags()
    {
        var status = new ConductorStatusSnapshot(42, new("running", At, null), "build", new("current", "build", "main"), At,
            new(true, [new("goal/full", "Active", "Developer")], 0, 0), null, null,
            new("pending", null, [new("goal/full", 3, At), new("other/full", 1, At)]), [], new(true, 0x2000));
        var fields = CliConductorConsoleAdapter.StatusFields(status);
        Assert.Equal("1", Copy(fields, "goals/count"));
        Assert.Equal("2", Copy(fields, "intents/count"));
        Assert.Equal("0", Copy(fields, "provider-holds/count"));
        Assert.Equal("0x00002000", Copy(fields, "supervisor/job-flags"));
        var unavailable = CliConductorConsoleAdapter.StatusFields(status with { Goals = new(false, null, null, null), SupervisorJob = null });
        Assert.False(unavailable.Fields.Single(field => field.Key == "goals/count").Available);
        Assert.False(unavailable.Fields.Single(field => field.Key == "supervisor/job-flags").Available);
    }

    [Fact]
    public void EpicNextStepsKeepScopeAndOutcomeSemanticsSeparate()
    {
        var item = new EpicPlanItemStatus(new(1, EpicPlanItemKind.Step, null, "Done", true), null, "Done");
        var plan = new EpicPlanView(new("epic", "Outcome still needs proof", [item.Item], []), [item], []);
        var snapshot = new OwnerConsoleFieldSnapshot("epic", [.. OwnerConsoleEpicPlanFormatter.Fields(plan)]);
        Assert.Equal("complete", Copy(snapshot, "plan/next"));
        Assert.Equal(EpicPlanNextStep.Compute(plan).Text, Copy(snapshot, "plan/outcome-next"));
        Assert.NotEqual(Copy(snapshot, "plan/next"), Copy(snapshot, "plan/outcome-next"));
    }

    [Fact]
    public void GateFieldsIncludeCountsQuietMemberReferencesAndLocalOccupancy()
    {
        OwnerConsoleGateLane Lane(string name, OwnerConsoleGateLaneState state, string outcome, string location = "remote") =>
            new(name, state, outcome, location, TimeSpan.FromSeconds(10), 20, false);
        var lanes = new[] {
            Lane("failed", OwnerConsoleGateLaneState.Done, "failed"), Lane("passed", OwnerConsoleGateLaneState.Done, "passed"),
            Lane("cached", OwnerConsoleGateLaneState.Cached, ""), Lane("queued", OwnerConsoleGateLaneState.Queued, ""),
            Lane("unknown", OwnerConsoleGateLaneState.Done, "unknown"), Lane("skip", OwnerConsoleGateLaneState.Skipped, ""),
            Lane("local-one", OwnerConsoleGateLaneState.Running, "", "local slot 1"),
            Lane("local-two", OwnerConsoleGateLaneState.Running, "", "local slot 2"),
            Lane("comparison [trusted main baseline]", OwnerConsoleGateLaneState.Running, "", "local slot 3"),
            Lane("repro", OwnerConsoleGateLaneState.Running, "", "local slot 4") with {
                IsAttribution = true, QuietSeconds = 345.678, ReproductionMemberId = "12345678901234567890123456789012" } };
        var gate = new OwnerConsoleGateProgress(true, "acceptance", [new("goal/full/canonical", "Member title")], At,
            TimeSpan.FromMinutes(1), lanes, 0.125, 30, 4, 8, Capacity: new([], 4));
        var fields = gate.FieldSnapshot();
        foreach (var key in new[] { "failed", "passed", "cached", "queued", "unknown", "skipped" }) Assert.Equal("1", Copy(fields, key));
        Assert.Equal("2", Copy(fields, "running"));
        Assert.Equal("0.125", Copy(fields, "progress"));
        Assert.Contains(fields.Fields, field => field.Value == "goal/full/canonical");
        Assert.Equal("2", Copy(gate.CapacityFields(), "local/running"));
        Assert.Equal("345.678", Copy(OwnerConsoleGateProgress.LaneFields(lanes[^1]), "quiet"));
        Assert.Equal(lanes[^1].ReproductionMemberId, Copy(OwnerConsoleGateProgress.LaneFields(lanes[^1]), "reproduction-member"));
        Assert.Equal(TimeSpan.FromMinutes(7).ToString("c"), Copy(OwnerConsoleGateProgress.Idle(TimeSpan.FromMinutes(7)).FieldSnapshot(), "idle-age"));
    }

    [Fact]
    public void GoalDetailExposesEvidenceHistoryAndRecentEventTimes()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("12345678901234567890123456789012", "Title", AgentRole.Developer);
        var evidence = new OwnerConsoleEvidenceAttemptProgress(At.AddMinutes(1), 3, At, Lane: "lane/full", Previous:
            new(goal.Id.Value, "previous", 12.345, "failed", 42));
        var fields = new OwnerGoalDetailSnapshot(goal, null, [new(At, "event", goal.Id.Value, "Detail\n日本語")], Evidence: evidence).Fields();
        Assert.Equal("3", Copy(fields, "evidence/ordinal"));
        Assert.Equal("12.345", Copy(fields, "evidence/previous/duration"));
        Assert.Equal("failed", Copy(fields, "evidence/previous/outcome"));
        Assert.Equal("42", Copy(fields, "evidence/previous/tests"));
        Assert.Equal(At.ToString("O"), Copy(fields, "event/0/at"));
        Assert.Equal("Detail\n日本語", Copy(fields, "event/0/detail"));
    }

    private static string Copy(OwnerConsoleFieldSnapshot snapshot, string key)
    {
        var writes = new List<string>();
        using var dialog = new OwnerConsoleFieldsDialog(snapshot, new(value => { writes.Add(value); return true; }), key);
        Assert.Equal(key, dialog.SelectedField?.Key);
        dialog.Copy();
        return Assert.Single(writes);
    }
}
