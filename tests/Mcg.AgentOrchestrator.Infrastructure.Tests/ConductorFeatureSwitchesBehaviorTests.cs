using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each steward case owns its SQLite stores; driver cases only inspect admission.
public sealed class ConductorFeatureSwitchesBehaviorTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private static ConductorFeatureSwitches Parse(string key, bool? enabled)
    {
        var root = JsonNode.Parse(ConductorAutonomyPolicy.Default.ToJson())!.AsObject();
        Assert.True(root.Remove("features"));
        if (enabled.HasValue) root["features"] = new JsonObject { [key] = enabled.Value };
        return ConductorAutonomyPolicy.ParseJson(root.ToJsonString()).Features;
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Steward_PolicyEnablement_ControlsModelRound(bool? setting, bool expected)
    {
        using var harness = new StewardHarness("A", enabled: Parse("steward", setting).Steward);
        harness.Seed("A");
        harness.Model.Reply("""{"kind":"no-action","reason":"fixture"}""");
        try
        {
            harness.Host.ServiceTick(harness.Kernel);
            if (expected)
            {
                Assert.NotNull(harness.Host.CurrentRound);
                // Hang detector for the model completion event; no elapsed-time verdict.
                await harness.Host.CurrentRound.WaitAsync(TimeSpan.FromSeconds(30));
            }
            else Assert.Null(harness.Host.CurrentRound);
            Assert.Equal(expected ? 1 : 0, harness.Model.Calls);
        }
        finally { harness.Host.Stop(); }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void MergeTrains_PolicyEnablement_ControlsAdmission(bool? setting, bool expected)
    {
        var driver = MakeDriver(mergeTrainsEnabled: Parse("mergeTrains", setting).MergeTrains,
            runMergeTrain: (_, _, _) => throw new InvalidOperationException("Admission must not run a train."));
        Assert.Equal(expected, driver.MergeTrainsEnabled);
    }
}
