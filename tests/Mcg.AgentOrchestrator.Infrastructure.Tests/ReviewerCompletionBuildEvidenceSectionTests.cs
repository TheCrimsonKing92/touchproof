using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ReviewerBuildEvidencePackageLifecycleTests.Fixture;

// Parallel-safe: unique repositories and snapshot records; all process interaction is limited to owned git workspaces.
public sealed class ReviewerCompletionBuildEvidenceSectionTests
{
    [Fact]
    public void MatchingAppliedReceipt_Section_ShowsBuildFieldsWithoutTestCredit()
    {
        using var fixture = new Fixture();
        fixture.CommitCandidate();
        var digest = fixture.Digest();
        var receipt = fixture.Receipt(fixture.Developer, digest);
        fixture.SetEvidence(fixture.Developer, receipt);

        var section = fixture.Section();

        Assert.Contains("matches reviewed candidate", section);
        Assert.Contains($"Reviewed worktree tree digest: {digest}", section);
        Assert.Contains($"Tree digest: {digest}", section);
        Assert.Contains("Phase: Success; exit code: 0; applied: True; custody released: True", section);
        foreach (var project in receipt.Projects) Assert.Contains(project, section);
        Assert.Contains("Host build evidence for the candidate only", section);
        Assert.Contains("not a product-test result and grants no test credit", section);
        Assert.Contains("Acceptance owns test execution", section);
    }

    public static IEnumerable<object?[]> NonMatchingRecords()
    {
        // Incomplete execution wins over digest mismatch, except for the explicit stale phase.
        foreach (var phase in Enum.GetValues<CompletionBuildEvidencePhase>())
        {
            if (phase == CompletionBuildEvidencePhase.Success) continue;
            foreach (var differentDigest in new[] { false, true })
                yield return [phase, 0, true, true, differentDigest,
                    phase == CompletionBuildEvidencePhase.StaleCandidate ? "stale: does not describe the reviewed candidate" : "unknown"];
        }
        yield return [CompletionBuildEvidencePhase.Success, 0, true, true, true, "stale: does not describe the reviewed candidate"];
        foreach (var differentDigest in new[] { false, true })
        {
            yield return [CompletionBuildEvidencePhase.Success, 1, true, true, differentDigest, "unknown"];
            yield return [CompletionBuildEvidencePhase.Success, null, true, true, differentDigest, "unknown"];
            yield return [CompletionBuildEvidencePhase.Success, 0, false, true, differentDigest, "unknown"];
            yield return [CompletionBuildEvidencePhase.Success, 0, true, false, differentDigest, "unknown"];
        }
        yield return [CompletionBuildEvidencePhase.StaleCandidate, null, false, false, false,
            "stale: does not describe the reviewed candidate"];
    }

    [Theory]
    [MemberData(nameof(NonMatchingRecords))]
    public void IneligibleReceipt_Section_NeverReportsCandidateMatch(
        CompletionBuildEvidencePhase phase, int? exitCode, bool applied, bool released, bool differentDigest, string expected)
    {
        using var fixture = new Fixture();
        var digest = fixture.Digest();
        var receipt = fixture.Receipt(fixture.Developer,
            differentDigest ? WorktreeTreeDigest.Algorithm + ":" + new string('1', 40) : digest) with
        {
            Phase = phase, BuildExitCode = exitCode, Applied = applied, CustodyReleased = released
        };
        fixture.SetEvidence(fixture.Developer, receipt);

        var section = fixture.Section();

        Assert.Contains($"): {expected}", section);
        Assert.Contains($"Phase: {phase}; exit code: {exitCode?.ToString() ?? "unknown"}", section);
        Assert.DoesNotContain("matches reviewed candidate", section);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid-digest")]
    public void MissingReceiptDigest_Section_ReportsUnknown(string digest)
    {
        using var fixture = new Fixture();
        fixture.SetEvidence(fixture.Developer, fixture.Receipt(fixture.Developer, digest));

        var section = fixture.Section();

        Assert.Contains("unknown (record tree digest unavailable)", section);
        Assert.DoesNotContain("matches reviewed candidate", section);
    }

    [Fact]
    public void MissingWorktreeDigest_Section_KeepsReceiptFieldsAndReportsUnknown()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Receipt(fixture.Developer, fixture.Digest());
        fixture.SetEvidence(fixture.Developer, receipt);

        var section = fixture.Section(fixture.Root);

        Assert.Contains("Reviewed worktree tree digest: unavailable (not a git workspace)", section);
        Assert.Contains("unknown (reviewed worktree tree digest unavailable)", section);
        Assert.Contains($"Tree digest: {receipt.TreeDigest}", section);
        Assert.Contains("Phase: Success; exit code: 0", section);
        Assert.DoesNotContain("matches reviewed candidate", section);
    }

    [Fact]
    public void DigestComputationFails_Section_ReportsUnknownWithoutHeadFallback()
    {
        using var fixture = new Fixture();
        fixture.SetEvidence(fixture.Developer, fixture.Receipt(fixture.Developer, fixture.Digest()));
        var brokenWorkspace = Path.Combine(fixture.Root, "broken-workspace");
        Directory.CreateDirectory(brokenWorkspace);
        File.WriteAllText(Path.Combine(brokenWorkspace, ".git"), "gitdir: missing-git-directory\n");

        var section = fixture.Section(brokenWorkspace);

        Assert.Contains("Reviewed worktree tree digest: unavailable", section);
        Assert.Contains("unknown (reviewed worktree tree digest unavailable)", section);
        Assert.DoesNotContain("matches reviewed candidate", section);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoHostRecord_Section_ReportsUnknownDespiteWorkerReport(bool workerReport)
    {
        using var fixture = new Fixture();
        fixture.SetEvidence(fixture.Developer, null,
            workerReport ? "inconclusive - build channel unavailable" : null);

        var section = fixture.Section();

        Assert.Contains("Host completion build: unknown (no record)", section);
        Assert.DoesNotContain("matches reviewed candidate", section);
        if (workerReport)
            Assert.Contains("Worker-reported (self-report, not host evidence): inconclusive - build channel unavailable", section);
    }

    [Theory]
    [InlineData("same-attempt", true)]
    [InlineData("missing-attempt", false)]
    [InlineData("other-attempt", false)]
    [InlineData("missing-dispatch", false)]
    public void WorkerChannelFailure_HostReceipt_LabelsOnlyProvenOrdering(string linkage, bool laterFact)
    {
        using var fixture = new Fixture();
        var attempt = linkage == "missing-attempt" ? (DateTimeOffset?)null
            : linkage == "other-attempt" ? Fixture.DispatchAt.AddMinutes(-1) : Fixture.DispatchAt;
        fixture.SetEvidence(fixture.Developer, fixture.Receipt(fixture.Developer, fixture.Digest()),
            "inconclusive - build channel unavailable", attempt, withDispatch: linkage != "missing-dispatch");

        var section = fixture.Section();

        Assert.Contains("Worker-reported (self-report, not host evidence): inconclusive - build channel unavailable", section);
        Assert.Contains("matches reviewed candidate", section);
        if (laterFact) Assert.Contains("later host fact for the same task dispatch", section);
        else
        {
            Assert.Contains("ordering relative to worker report not established", section);
            Assert.DoesNotContain("later host fact", section);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrongReceiptOwner_Section_ReportsUnknown(bool wrongGoal)
    {
        using var fixture = new Fixture();
        var receipt = fixture.Receipt(fixture.Developer, fixture.Digest());
        receipt = wrongGoal ? receipt with { GoalId = "another-goal" } : receipt with { TaskId = "another-task" };
        fixture.SetEvidence(fixture.Developer, receipt);

        var section = fixture.Section();

        Assert.Contains("unknown (record ownership does not match the goal and task)", section);
        Assert.DoesNotContain("matches reviewed candidate", section);
        Assert.DoesNotContain("later host fact", section);
    }

    [Fact]
    public void PriorTaskSelection_Section_IncludesPendingAndFailedPredecessorsOnly()
    {
        using var fixture = new Fixture();
        var pending = new TaskSpec(TaskId.New(), "Pending developer", AgentRole.Developer);
        var failed = new TaskSpec(TaskId.New(), "Failed tester", AgentRole.Tester);
        var planner = new TaskSpec(TaskId.New(), "Planner", AgentRole.Planner);
        var reviewer = new TaskSpec(TaskId.New(), "Reviewer", AgentRole.Reviewer);
        var subsequent = new TaskSpec(TaskId.New(), "Later developer", AgentRole.Developer);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Selection", [pending, failed, planner, reviewer, subsequent]);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(item => item with
            {
                Tasks = item.Tasks.Select(task => task.Id == failed.Id.Value
                    ? task with { Status = WorkTaskStatus.Failed } : task).ToArray()
            }).ToArray()
        });
        goal = kernel.GetGoal(goal.Id);

        var section = string.Join("\n", ReviewerCompletionBuildEvidenceSection.Build(goal,
            goal.Tasks.Single(task => task.Id == reviewer.Id), fixture.Worktree));

        Assert.Contains(pending.Id.Value, section);
        Assert.Contains(failed.Id.Value, section);
        Assert.DoesNotContain(planner.Id.Value, section);
        Assert.DoesNotContain(subsequent.Id.Value, section);
        Assert.Equal(2, section.Split("unknown (no record)").Length - 1);
        Assert.Empty(ReviewerCompletionBuildEvidenceSection.Build(goal, goal.Tasks[0], fixture.Worktree));
    }
}
