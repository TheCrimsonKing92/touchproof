using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSelfRelaunchDefaultEnablement : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsSelfRelaunchDefaultEnablement(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(null, true)]
    [Xunit.InlineData("true", true)]
    [Xunit.InlineData("false", false)]
    public void UnsetSettingSchedulesConductorLanding(string? setting, bool expected)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(ConductorAutonomyPolicy.Conservative.ToJson())!.AsObject();
        Assert.True(json.Remove("features"));
        if (setting is not null) json["features"] = System.Text.Json.Nodes.JsonNode.Parse($"{{\"selfRelaunch\":{setting}}}");
        var policy = ConductorAutonomyPolicy.ParseJson(json.ToJsonString());
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
        var landed = false;
        var relaunchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ =>
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"]);

        var output = AsyncLocalConsoleRouter.Capture(() =>
            new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    return new ConductorSelfRelaunchResult(false, "build", "test stop");
                },
                selfRelaunchEnabled: policy.Features.SelfRelaunch).Run(
                    kernel, driver, policy, NoStopPath(), maxIterations: 3));

        Assert.Equal(expected, relaunchCalls > 0);
        Assert.Equal(expected, output.Contains("LOOP_RELAUNCH_SCHEDULED", StringComparison.Ordinal));
    }

}
