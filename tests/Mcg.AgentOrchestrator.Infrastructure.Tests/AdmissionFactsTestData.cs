using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

internal static class AdmissionFactsTestData
{
    internal static AdmissionFactsInputs Input(GoalId goal, char revision = 'a') => new(
        Path.GetFullPath(Path.GetTempPath()), goal, new string(revision, 40), new string('b', 40),
        new string('b', 40), "[]", "approvals");

    internal static AdmissionFacts Fact(AdmissionFactsInputs input) => new(input,
        new(true, ["src/Mcg.AgentOrchestrator.Core/AdmissionFacts.cs"]), new(true, []), ChangeRiskTier.DocsOnly,
        new([], string.Empty, true, string.Empty), null);

    internal static void Wait(Task completion, string signal) =>
        Assert.True(completion.Wait(TimeSpan.FromSeconds(60)), signal);

    // Landing/policy scenarios require completed facts as a precondition. The cold-tick
    // and blocked-worker tests deliberately omit this and assert the pending hold instead.
    internal static void Warm(ConductorDriver driver, AgentOrchestratorKernel kernel)
    {
        driver.BeginAdmissionFacts(kernel.Goals, kernel);
        var reader = Assert.IsType<AdmissionFactsTickReader>(driver.AdmissionFacts.Reader);
        try
        {
            Wait(reader.Derivation.Completion, "Scenario admission facts did not finish.");
            foreach (var goal in kernel.Goals)
                if (reader.Inputs(goal.Id) is { } input)
                    Assert.True(reader.Derivation.Read(input) is not null,
                        $"Scenario admission facts unavailable for {goal.Id}: {string.Join(" | ", reader.DrainReports())}");
        }
        finally
        {
            driver.AdmissionFacts.Stop();
            Wait(reader.Derivation.Completion, "Scenario admission worker did not stop.");
        }
    }
}
