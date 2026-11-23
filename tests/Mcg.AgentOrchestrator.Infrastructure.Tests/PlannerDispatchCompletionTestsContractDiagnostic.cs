using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the support fixture owns a unique repository, worktree, and log directory.
public sealed class PlannerDispatchCompletionTestsContractDiagnostic : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("*.cs")]
    [Xunit.InlineData("src/Missing?.cs")]
    public void MarkedWildcardCompletion_RecordsContractFailure(string citation)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        var plan = PlanWithNewTarget(citation);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Planner, plan, string.Empty, clock, includePlannerContract: false);
        Xunit.Assert.Equal("0", File.ReadAllText(process.ExitCodePath).Trim());
        Xunit.Assert.Equal(plan, File.ReadAllText(process.StandardOutputPath));
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        var failure = Xunit.Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed));
        Xunit.Assert.StartsWith(
            "Dispatch failed: rule=planner-output-contract-rejected: Planner output contract failed.",
            failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("path contains a wildcard", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending plan line ", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));
    }

    [Xunit.Fact]
    public void ConcreteNewFileCompletion_RecordsSuccess()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        var plan = PlanWithNewTarget("src/Missing.cs");
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Planner, plan, string.Empty, clock, includePlannerContract: false);
        Xunit.Assert.False(File.Exists(Path.Combine(process.WorkingDirectory, "src", "Missing.cs")));
        Xunit.Assert.Equal(plan, File.ReadAllText(process.StandardOutputPath));
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed);
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));
    }

    [Xunit.Fact]
    public void MissingTargetCitationAppearsInTaskFailedMessageBeforeCommand()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));
        var plan = PlannerContractPlanFixture().Replace(
            "`./seed.txt`", "`src/Missing.cs`", StringComparison.Ordinal);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Planner, plan, string.Empty, clock);
        Xunit.Assert.True(File.Exists(process.ExitCodePath));
        Xunit.Assert.Contains("`src/Missing.cs`", File.ReadAllText(process.StandardOutputPath), StringComparison.Ordinal);

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        var failure = Xunit.Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed));
        Xunit.Assert.StartsWith(
            "Dispatch failed: rule=planner-output-contract-rejected: Planner output contract failed.",
            failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending plan line ", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.EndsWith(" Command: " + task.LastDispatch!.Command, failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/Contract.AttentionReads.cs:43", "src/Contract.AttentionReads.cs", "src/Contract.AttentionReads.cs:43", true, false)]
    [Xunit.InlineData("...AttentionReads.cs:43", "src/Contract.AttentionReads.cs", "src/Contract.AttentionReads.cs:43", true, false)]
    [Xunit.InlineData("...AttentionReads.cs:43", "src/Contract.AttentionReads.cs", "src/Contract.AttentionReads.cs:43", true, true)]
    [Xunit.InlineData("...MissingReads.cs:43", "src/Contract.AttentionReads.cs", null, false, false)]
    [Xunit.InlineData("OnlyGuide.md:43", "guides/OnlyGuide.md", "guides/OnlyGuide.md:43", true, false)]
    [Xunit.InlineData("......Target.cs:43", "...Target.cs", "./...Target.cs:43", true, false)]
    public void TrackedShortCitationCompletionRecordsCanonicalReceiptOrTypedRejection(
        string citation, string trackedPath, string? canonicalCitation, bool accepted, bool withNewFileDeclaration)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var targetFile = Path.Combine(root, trackedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        File.WriteAllText(targetFile, "// tracked citation fixture");
        RunGit(root, ["add", "--", trackedPath], clock.UtcNow);
        if (trackedPath == "...Target.cs")
        {
            File.WriteAllText(Path.Combine(root, "src/NestedTarget.cs"), "// second match on reparse");
            RunGit(root, ["add", "--", "src/NestedTarget.cs"], clock.UtcNow);
        }
        RunGit(root, ["commit", "-m", "Seed tracked Planner target"], clock.UtcNow);
        const string newFile = "NewReviewTarget.cs";
        var sibling = withNewFileDeclaration ? $" and `{newFile}`" : string.Empty;
        var plan = PlanWithTargetBody($"Inspect repository evidence `{citation}`{sibling} for the concrete implementation seam.");
        if (withNewFileDeclaration)
        {
            plan = plan.Replace("The premise is valid", $"Create `future/{newFile}` (new file). The premise is valid", StringComparison.Ordinal);
        }
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Planner, plan, string.Empty, clock, includePlannerContract: false);
        Xunit.Assert.Equal("0", File.ReadAllText(process.ExitCodePath).Trim());
        Xunit.Assert.Equal(plan, File.ReadAllText(process.StandardOutputPath));
        Xunit.Assert.Equal(trackedPath, ReadGit(process.WorkingDirectory, ["ls-files", "--", trackedPath]));
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Single(task.DispatchHistory);
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));
        var stdout = File.ReadAllText(process.StandardOutputPath);
        if (!accepted)
        {
            Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
            Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
            Xunit.Assert.False(PlannerOutputContract.TryExtractDurablePlan(stdout, out _, out _));
            var failure = Xunit.Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed));
            Xunit.Assert.Contains("rule=planner-output-contract-rejected", failure.Message, StringComparison.Ordinal);
            Xunit.Assert.Contains("Retry Planner for contract repair", failure.Message, StringComparison.Ordinal);
            var stderr = File.ReadAllText(process.StandardErrorPath);
            Xunit.Assert.Contains($"Offending citation: '{citation}'", stderr, StringComparison.Ordinal);
            Xunit.Assert.Contains("No tracked file matches the short citation.", stderr, StringComparison.Ordinal);
            return;
        }

        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.NotEqual(GoalStatus.Failed, goal.Status);
        Xunit.Assert.Single(goal.Timeline.Where(item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskCompleted));
        Xunit.Assert.DoesNotContain(goal.Timeline, item => item.Kind is ProgressKind.TaskFailed or ProgressKind.TaskRetried);
        Xunit.Assert.True(PlannerOutputContract.TryExtractDurablePlan(stdout, out var receipt, out var receiptDiagnostic),
            receiptDiagnostic);
        Xunit.Assert.Contains($"Inspect repository evidence `{canonicalCitation}`{sibling}", receipt, StringComparison.Ordinal);
        var audit = $"Planner contract note: raw citation `{citation}` resolved to `{canonicalCitation}`.";
        Xunit.Assert.Equal(citation == canonicalCitation ? 0 : 1, receipt.Split(audit, StringSplitOptions.None).Length - 1);
        var revalidated = PlannerOutputContract.Resolve(stdout, string.Empty, process.WorkingDirectory);
        Xunit.Assert.True(revalidated.Succeeded, revalidated.Diagnostic);
        Xunit.Assert.Equal(receipt, revalidated.Plan);
        Xunit.Assert.True(WorkerArtifactWriter.TryResolveDurablePlannerPlan(task.LastVerification!,
            out var retrieved, out var retrievalDiagnostic), retrievalDiagnostic);
        Xunit.Assert.Equal(receipt, retrieved);
    }

    private static string PlanWithNewTarget(string citation) => PlanWithTargetBody(
        $"Validate new file targets against the cited implementation path.\n- Inspect `{citation}` — new class.");

    private static string PlanWithTargetBody(string body)
    {
        var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var bodyStart = plan.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return plan[..bodyStart] + $"\n\n{body}\n" + plan[bodyEnd..];
    }
}
