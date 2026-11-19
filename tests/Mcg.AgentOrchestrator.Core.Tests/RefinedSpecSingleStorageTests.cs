using Mcg.AgentOrchestrator.Core;
using Xunit;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RefinedSpecSingleStorageTests
{
    [Fact]
    public void ExportStoresAllVersionsIncludingCurrentWithoutTopLevelSpec()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Refine a goal twice");
        kernel.RecordGoalRefinement(goal.Id,
            new RefinedSpec("Initial contract", ["Initial criterion"], VerificationClass.TestVerifiable, [], []));
        kernel.RecordGoalRefinement(goal.Id,
            new RefinedSpec("Current contract", ["Current criterion"], VerificationClass.TestVerifiable, [], []));

        var snapshot = kernel.ExportGoalSnapshot(goal.Id);

        Assert.Null(snapshot.RefinedSpec);
        Assert.Collection(snapshot.RefinedSpecVersions!,
            initial =>
            {
                Assert.Equal(1, initial.Version);
                Assert.Equal(2, initial.SupersededByVersion);
                Assert.Equal("Initial contract", initial.Spec.BehavioralContract);
                Assert.Equal("Initial criterion", Assert.Single(initial.Spec.AcceptanceCriteria));
            },
            current =>
            {
                Assert.Equal(2, current.Version);
                Assert.Null(current.SupersededByVersion);
                Assert.Equal("Current contract", current.Spec.BehavioralContract);
                Assert.Equal("Current criterion", Assert.Single(current.Spec.AcceptanceCriteria));
            });
    }

    [Fact]
    public void ExportWithoutRefinementKeepsEmptySpecAndVersionList()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Unrefined goal");

        var snapshot = kernel.ExportGoalSnapshot(goal.Id);

        Assert.Null(snapshot.RefinedSpec);
        Assert.Empty(snapshot.RefinedSpecVersions!);
    }
}
