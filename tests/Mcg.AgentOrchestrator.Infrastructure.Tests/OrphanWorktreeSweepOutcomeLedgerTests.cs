using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: writers and ledgers are instance-owned, with no Console mutation.
public sealed class OrphanWorktreeSweepOutcomeLedgerTests
{
    [Xunit.Fact]
    public void Sweep_RepeatedPassAndReasonTransitions_ReportOnlyChanges()
    {
        using var fixture = new OrphanMetadataSweepFixture();
        using var writer = new StringWriter();
        var ledger = new OrphanWorktreeSweepOutcomeLedger();
        var hooks = fixture.Hooks with
        {
            OrphanSweepOutcomeSink = outcome => OrphanWorktreeSweepOutcome.Write(outcome, writer, ledger)
        };
        fixture.Sweep(hooks: hooks);
        fixture.Sweep(hooks: hooks);
        Assert.Single(Lines(writer));

        fixture.Metadata = [fixture.Summary("Cancelled")];
        OrphanWorktreeSweep.Run(fixture.Root, null, hooks, _ => "removal-failed", "test");
        Assert.Equal(2, Lines(writer).Length);

        fixture.Metadata = [];
        fixture.Sweep(hooks: hooks);
        Assert.Equal(new[] { "goal-state-unavailable", "removal-failed", "goal-state-unavailable" },
            Lines(writer).Select(line =>
            {
                using var record = JsonDocument.Parse(line["orphan-sweep-outcome ".Length..]);
                return record.RootElement.GetProperty("Reason").GetString();
            }));
        Assert.True(Directory.Exists(fixture.Folder));
    }

    [Xunit.Fact]
    public void Write_EquivalentPathsAndPayloadChanges_SuppressUntilReasonChanges()
    {
        using var fixture = new OrphanMetadataSweepFixture();
        using var writer = new StringWriter();
        var ledger = new OrphanWorktreeSweepOutcomeLedger();
        var outcome = new OrphanWorktreeSweepOutcome(fixture.Folder, null, "goal-state-unavailable", 1, "a", "first");
        OrphanWorktreeSweepOutcome.Write(outcome, writer, ledger);
        var equivalent = Path.Combine(fixture.Folder, ".");
        if (OperatingSystem.IsWindows()) equivalent = equivalent.ToUpperInvariant();
        OrphanWorktreeSweepOutcome.Write(outcome with { Path = equivalent, CallerRole = "second", ProcessId = 2 }, writer, ledger);
        Assert.Single(Lines(writer));
        OrphanWorktreeSweepOutcome.Write(outcome with { Reason = "recent" }, writer, ledger);
        OrphanWorktreeSweepOutcome.Write(outcome, writer, ledger);
        Assert.Equal(2, Lines(writer).Length);
    }

    [Xunit.Fact]
    public void Write_ReclaimsAndSeparateWorkspaces_AlwaysReport()
    {
        using var first = new OrphanMetadataSweepFixture();
        using var second = new OrphanMetadataSweepFixture();
        using var writer = new StringWriter();
        var ledger = new OrphanWorktreeSweepOutcomeLedger();
        var outcome = new OrphanWorktreeSweepOutcome(first.Folder, null, "removed", 1, null, "test");
        OrphanWorktreeSweepOutcome.Write(outcome, writer, ledger);
        OrphanWorktreeSweepOutcome.Write(outcome, writer, ledger);
        OrphanWorktreeSweepOutcome.Write(outcome with { Reason = "goal-state-unavailable" }, writer, ledger);
        OrphanWorktreeSweepOutcome.Write(outcome with { Path = second.Folder, Reason = "goal-state-unavailable" }, writer, ledger);
        Assert.Equal(4, Lines(writer).Length);
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
