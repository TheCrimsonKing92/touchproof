using Mcg.AgentOrchestrator.App.Orchestration;
using Scenario = ActivationRemoteLaneTailAdoptionScenarioTests.Scenario;

// Parallel-safe: scripted process host, private supervisor files, and event-gated fake time.
public sealed class ActivationTrainBetweenGatesAdoptionScenarioTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Observe_MeasuredSurvivorsGap_AdoptsAfterTrainRelease()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        await scenario.HeldTickAsync(1, "admitted", waiting: 5, sample: new(1, false, 5, 1, 1));
        for (var tick = 2; tick <= 13; tick++)
        {
            scenario.Clock.Advance(TimeSpan.FromSeconds(12 + tick % 4));
            await scenario.HeldTickAsync(tick, "admitted", waiting: 5,
                sample: new(tick, false, 5, 1, 0, 0, 1, "train:survivors"));
        }
        scenario.Clock.Advance(TimeSpan.FromSeconds(13));
        await scenario.HeldTickAsync(14, "admitted", waiting: 5, sample: new(14, false, 5, 1, 1));
        await scenario.HeldTickAsync(15, "terminal", waiting: 5, sample: new(15, false, 5, 0, 0));
        scenario.Tick(16, "released", waiting: 5, sample: new(16, false, 5, 0, 0));
        await scenario.AdoptedAsync();
        Assert.Contains(scenario.Lines, line => line.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Lines, line => line.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal));
        await scenario.FinishAsync();
    }
}
