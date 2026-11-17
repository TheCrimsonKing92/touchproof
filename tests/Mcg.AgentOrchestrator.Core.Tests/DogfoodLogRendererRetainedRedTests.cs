using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all evidence and goal state are local to each case.
public sealed class DogfoodLogRendererRetainedRedTests
{
    [Theory]
    [InlineData(false, false, DogfoodBaselineState.Unknown, "unknown")]
    [InlineData(false, true, DogfoodBaselineState.Unknown, "unknown")]
    [InlineData(true, false, DogfoodBaselineState.Unknown, "unknown")]
    [InlineData(true, true, DogfoodBaselineState.Unknown, "unknown")]
    [InlineData(false, false, DogfoodBaselineState.Unavailable, "unavailable")]
    [InlineData(true, true, DogfoodBaselineState.Unavailable, "unavailable")]
    public void Inconclusive_RetainedFailure_RendersIndependentFacts(
        bool manual, bool parked, DogfoodBaselineState baseline, string baselineText)
    {
        var goal = new Goal(GoalId.New(), "Retain executed failure", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
        var evidence = new DogfoodLandingEvidence(
            DogfoodAcceptanceDisposition.Inconclusive, "apparatus-regate", WasManuallyLanded: manual,
            ExecutedCandidateResult: DogfoodExecutedCandidateResult.Failed, BaselineState: baseline,
            LatestSchedulingDisposition: "apparatus-regate", IsParked: parked);

        var entry = DogfoodLogRenderer.Render(goal, evidence);

        Assert.Contains("Acceptance inconclusive: apparatus-regate.", entry.Summary);
        Assert.Contains(
            "Executed candidate RED retained: an earlier acceptance run for this candidate recorded failed.",
            entry.Summary);
        Assert.Contains($"Baseline: {baselineText}.", entry.Summary);
        Assert.Equal(parked, entry.Summary.Contains("Parked.", StringComparison.Ordinal));
        Assert.Equal(manual, entry.Summary.Contains(
            "Manual landing recorded; acceptance not recorded as passed.", StringComparison.Ordinal));
        Assert.Equal(evidence, entry.LandingEvidence);
        Assert.DoesNotContain("Acceptance passed", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gate passed", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gate-passed", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("introduced", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Baseline: executed", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inconclusive_WithoutRetainedFacts_PreservesPriorWording(bool manual)
    {
        var goal = new Goal(GoalId.New(), "Unexecuted acceptance", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
        var evidence = new DogfoodLandingEvidence(
            DogfoodAcceptanceDisposition.Inconclusive, "apparatus-regate", WasManuallyLanded: manual);

        var summary = DogfoodLogRenderer.Render(goal, evidence).Summary;
        var prefix = manual ? "Manual landing recorded; acceptance not recorded as passed. " : string.Empty;

        Assert.EndsWith($"{prefix}Acceptance inconclusive: apparatus-regate.", summary);
        Assert.DoesNotContain("Executed candidate", summary);
        Assert.DoesNotContain("Baseline:", summary);
        Assert.DoesNotContain("Parked", summary);
    }
}
