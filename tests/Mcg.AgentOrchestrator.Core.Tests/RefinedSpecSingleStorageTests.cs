using System.Text.Json;
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

    [Fact]
    public void RestoreWithBothRepresentationsUsesVersionListAndPreservesHistory()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Restore a pre-change goal");
        kernel.RecordGoalRefinement(goal.Id,
            new RefinedSpec("Initial contract", ["Initial criterion"], VerificationClass.TestVerifiable, [], []));
        kernel.RecordGoalRefinement(goal.Id,
            new RefinedSpec("Current contract", ["Current criterion"], VerificationClass.TestVerifiable, [], []));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var versions = snapshot.RefinedSpecVersions!;
        var preChangeSnapshot = snapshot with { RefinedSpec = versions[0].Spec };

        var restoredKernel = new AgentOrchestratorKernel();
        restoredKernel.ReplaceGoalWithSnapshot(preChangeSnapshot);

        var restored = restoredKernel.GetGoal(goal.Id);
        Assert.Equal(JsonSerializer.Serialize(goal.RefinedSpec), JsonSerializer.Serialize(restored.RefinedSpec));
        Assert.Equal(JsonSerializer.Serialize(goal.RefinedSpecVersions),
            JsonSerializer.Serialize(restored.RefinedSpecVersions));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoreTopLevelOnlySynthesizesVersionOne(bool omitVersionList)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Restore a legacy goal");
        kernel.ReviseGoalBrief(goal.Id, "Revised legacy brief");
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var legacySpec = new RefinedSpecSnapshot("Legacy contract", ["Legacy criterion"],
            nameof(VerificationClass.TestVerifiable), [], []);
        var legacySnapshot = snapshot with
        {
            RefinedSpec = legacySpec,
            RefinedSpecVersions = omitVersionList ? null : []
        };
        Assert.Null(legacySnapshot.CreatedAt);
        var goalCreated = Assert.Single(legacySnapshot.Timeline, item => item.Kind == ProgressKind.GoalCreated);

        var restoredKernel = new AgentOrchestratorKernel();
        restoredKernel.ReplaceGoalWithSnapshot(legacySnapshot);

        var restored = restoredKernel.GetGoal(goal.Id);
        var version = Assert.Single(restored.RefinedSpecVersions);
        Assert.Equal(1, version.Version);
        Assert.Null(version.SupersededByVersion);
        Assert.Equal(goalCreated.OccurredAt, version.RecordedAt);
        Assert.Equal(2, version.BriefVersion);
        Assert.Same(version.Spec, restored.RefinedSpec);
        Assert.Equal("Legacy contract", version.Spec.BehavioralContract);
        Assert.Equal("Legacy criterion", Assert.Single(version.Spec.AcceptanceCriteria));
        Assert.Equal(VerificationClass.TestVerifiable, version.Spec.VerificationClass);
    }
}
