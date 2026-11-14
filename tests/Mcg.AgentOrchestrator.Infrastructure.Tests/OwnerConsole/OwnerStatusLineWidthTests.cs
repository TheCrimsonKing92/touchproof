using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

// Parallel-safe: stateless presenter, fixed times and an instance-owned UI scene.
public sealed class OwnerStatusLineWidthTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 13, 31, 0, TimeSpan.Zero);
    private static readonly OwnerConsoleStatus Status = new(ConductorLiveness.Running, 12, 0, 2, TimeSpan.FromSeconds(5), 5, 1);

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void PopulatedStatus_FitsAndRetainsProtectedFacts(int width)
    {
        var line = Compose(width);

        Assert.True(line.GetColumns() <= width, line);
        Assert.Contains("running", line);
        Assert.Contains("gate: solo 41a0", line);
        Assert.Contains("3/26", line);
        Assert.Contains("~6m", line);
        Assert.Contains("active: 12", line);
        Assert.Contains("decisions: 0", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Width40_FitsAndStartsWithConductorState()
    {
        var line = Compose(40);

        Assert.True(line.GetColumns() <= 40, line);
        Assert.StartsWith("running", line);
        Assert.Contains("gate: solo 41a0", line);
        Assert.DoesNotContain("active:", line);
        Assert.DoesNotContain("decisions:", line);
    }

    [Theory]
    [InlineData(1, "…")]
    [InlineData(5, "runn…")]
    public void TinyWindow_TruncatesConductorLast(int width, string expected) =>
        Assert.Equal(expected, Compose(width));

    [Theory]
    [InlineData("working:")]
    [InlineData("failed today:")]
    [InlineData("stale")]
    [InlineData("hidden:")]
    [InlineData("bell:")]
    [InlineData("landed today:")]
    [InlineData("last event:")]
    public void OptionalSuffixes_YieldInFixedOrderBeforeProtectedFacts(string firstOmitted)
    {
        var full = Compose(0);
        var expected = full[..full.IndexOf(" | " + firstOmitted, StringComparison.Ordinal)];

        Assert.Equal(expected, Compose(expected.GetColumns()));
    }

    [Fact]
    public void WideCharacters_AreMeasuredInTerminalColumns()
    {
        const string working = "界界界界界界界界界界";
        var full = Compose(0, working: working);
        Assert.True(full.GetColumns() > full.Length);

        var line = Compose(full.Length, working: working);

        Assert.True(line.GetColumns() <= full.Length, line);
        Assert.DoesNotContain(working, line);
        Assert.Contains("decisions: 0", line);
    }

    [Fact]
    public void CompactGate_PreservesFailureAndSummarizesMembers()
    {
        var gate = Gate() with {
            Kind = "cohort", Members = [new("41a09434", "First"), new("11223344", "Second"), new("55667788", "Third")],
            Lanes = [new("Console", OwnerConsoleGateLaneState.Done, "failed", "local", TimeSpan.Zero, null, false, IsScheduled: true)] };

        var line = Compose(80, gate);

        Assert.True(line.GetColumns() <= 80, line);
        Assert.StartsWith("running", line);
        Assert.Contains("gate: cohort 41a0+2", line);
        Assert.Contains("FAILING", line);
        Assert.Contains("active: 12", line);
        Assert.Contains("decisions: 0", line);
    }

    [Fact]
    public void LongParticipation_PreservesOtherProtectedFacts()
    {
        var gate = Gate() with { Live = false, Participation = new string('x', 100) };

        var line = Compose(80, gate);

        Assert.True(line.GetColumns() <= 80, line);
        Assert.StartsWith("running", line);
        Assert.Contains("gate: ", line);
        Assert.Contains('…', line);
        Assert.Contains("active: 12", line);
        Assert.Contains("decisions: 0", line);
        Assert.Equal(new string('x', 100), Assert.Single(new OwnerConsoleGateFieldProjection(gate).FieldSnapshot().Fields, field => field.Key == "participation").Value);
    }

    [Fact]
    public async Task FullScreenResize_RecomposesStatusFromTheFullSnapshot()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        await scene.Render([]);
        var gate = Gate();
        scene.View.Render(scene.Controller.Model! with { Status = Status, Gate = gate });
        scene.View.SetWorking("test", "refresh after conductor event");
        var label = Assert.Single(scene.View.Window.SubViews.OfType<Label>(),
            item => item.Text.StartsWith("conductor:", StringComparison.Ordinal));

        foreach (var width in new[] { 80, 120, 200, 40, 300 })
        {
            // Resizing alone must invoke the production viewport handler.
            label.Frame = new(0, 0, width, 1);
            var line = scene.View.StatusText;
            Assert.True(line.GetColumns() <= width, line);
            Assert.Contains("running", line);
            if (width >= 80)
            {
                Assert.Contains("gate: solo 41a0", line);
                Assert.Contains("active: 12", line);
                Assert.Contains("decisions: 0", line);
            }
        }
        Assert.Contains("working: refresh after conductor event", scene.View.StatusText);
        Assert.Equal(gate.ExecutionId, scene.Controller.Model!.Gate!.ExecutionId);
        Assert.Equal(gate.ExecutionId, Assert.Single(new OwnerConsoleGateFieldProjection(scene.Controller.Model.Gate).FieldSnapshot().Fields,
            field => field.Key == "execution").Value);
    }

    private static string Compose(int width, OwnerConsoleGateProgress? gate = null, string working = "refresh, command") =>
        new OwnerStatusPresenter().Compose(Status, gate ?? Gate(), "calm on, attention off, alarm on",
            Now.AddMinutes(-6), Now, TimeZoneInfo.Utc, TimeSpan.FromSeconds(10), [working], width);

    private static OwnerConsoleGateProgress Gate() => new(true, "solo", [new("41a09434", "Goal")],
        Now.AddMinutes(-18), TimeSpan.FromMinutes(18), [], .5, 360, 3, 26,
        LanePlanAvailable: true, ExecutionId: "41a09434-0-20261105131307015-97f069e446d6477bb6d563c41853a816", EtaAvailable: true);
}
