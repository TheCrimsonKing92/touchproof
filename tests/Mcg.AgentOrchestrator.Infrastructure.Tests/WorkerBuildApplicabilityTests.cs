using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure comparisons or fixture-owned repositories, stores and fake identities.
public sealed class WorkerBuildApplicabilityTests
{
    [Fact]
    public void Consume_NoInvocations_RetainsExactCurrentInputs()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        var result = fixture.Consume();
        Assert.Equal(WorkerBuildEvidenceGap.MissingApplicableInvocation, result.Gap);
        var explanation = Assert.IsType<WorkerBuildApplicabilityExplanation>(result.Applicability);
        Assert.Equal(fixture.DispatchId, explanation.DispatchId);
        Assert.Equal(fixture.Digest(), explanation.TreeDigest);
        Assert.Equal(fixture.Scope().Identity, explanation.ScopeIdentity);
        Assert.Equal(new[] { WorkerBuildApplicabilityFixture.Project }, explanation.RequiredProjects);
        Assert.Equal(new[] { WorkerBuildApplicabilityReason.NoInvocationRecorded }, explanation.Reasons);
    }

    [Fact]
    public void Consume_OtherDispatchSuccess_IsExplainedButNeverCredited()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        var older = fixture.Task.LastDispatch! with { DispatchedAt = fixture.Task.LastDispatch!.DispatchedAt.AddHours(-1) };
        var invocation = fixture.Invocation("00000001") with { DispatchAt = older.DispatchedAt,
            DispatchId = BackgroundDispatchRunner.BuildDispatchId(fixture.Build.GoalId, fixture.Build.TaskId,
                older with { ProviderSessionId = null }) };
        fixture.Mutate(task => task with { DispatchHistory = task.DispatchHistory!.Prepend(
            task.LastDispatch! with { DispatchedAt = older.DispatchedAt }).ToArray() });
        fixture.SetInvocations(invocation);
        var result = fixture.Consume();
        Assert.Equal(WorkerBuildEvidenceGap.MissingApplicableInvocation, result.Gap);
        Assert.Contains(WorkerBuildApplicabilityReason.OnlyOtherDispatches, result.Applicability!.Reasons);
        var comparison = Assert.Single(result.Applicability.Invocations);
        Assert.Equal(invocation.DispatchId, comparison.DispatchId);
        Assert.False(comparison.DispatchMatches);
        Assert.True(comparison.TreeMatches);
        Assert.True(comparison.CoversProjects);
        Assert.False(Assert.Single(fixture.Task.WorkerBuildInvocations).Consumed);
    }

    [Fact]
    public void Consume_SimultaneousMismatches_RetainsEveryComparison()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        const string wrong = "wbc1:git-tree:1111111111111111111111111111111111111111";
        var failed = fixture.Invocation("00000002", WorkerBuildOutcome.ApparatusFailure, wrong, ["other.csproj"]);
        var success = fixture.Invocation("00000001", digest: wrong);
        fixture.SetInvocations(failed, success);
        var result = fixture.Consume();
        Assert.Equal(WorkerBuildEvidenceGap.MissingApplicableInvocation, result.Gap);
        var explanation = result.Applicability!;
        Assert.Equal(fixture.Digest(), explanation.TreeDigest);
        Assert.Equal(new[] { "00000001", "00000002" }, explanation.Invocations.Select(i => i.InvocationId));
        var comparison = explanation.Invocations[1];
        Assert.Equal(wrong, comparison.TreeDigest);
        Assert.Equal(failed.ScopeIdentity, comparison.ScopeIdentity);
        Assert.Equal(new[] { "other.csproj" }, comparison.Projects);
        Assert.Equal(new[] { WorkerBuildApplicabilityFixture.Project }, comparison.MissingProjects);
        Assert.Equal(new[] { WorkerBuildApplicabilityReason.TreeMismatch,
            WorkerBuildApplicabilityReason.IncompleteProjectCoverage, WorkerBuildApplicabilityReason.TerminalNonSuccess }, comparison.Reasons);
        Assert.Null(comparison.ChangedPaths);
        Assert.All(fixture.Task.WorkerBuildInvocations, i => Assert.False(i.Consumed));
        var reversed = WorkerBuildApplicabilityExplanation.Capture(fixture.DispatchId, fixture.Digest(), fixture.Scope(), [success, failed]);
        Assert.Equal(WorkerBuildApplicabilityFormatter.Format(explanation), WorkerBuildApplicabilityFormatter.Format(reversed));
    }

    [Theory]
    [InlineData(WorkerBuildOutcome.Success, false, false)]
    [InlineData(WorkerBuildOutcome.CompilerFailure, false, true)]
    [InlineData(WorkerBuildOutcome.CapacityBeforeStart, true, false)]
    [InlineData(WorkerBuildOutcome.TimeoutAfterStart, true, false)]
    [InlineData(WorkerBuildOutcome.Cancelled, true, false)]
    [InlineData(WorkerBuildOutcome.StaleCandidate, true, false)]
    [InlineData(WorkerBuildOutcome.Interrupted, true, false)]
    [InlineData(WorkerBuildOutcome.ApparatusFailure, true, false)]
    public void Consume_ApplicableTerminal_PreservesVerdictAndConsumption(
        WorkerBuildOutcome outcome, bool missing, bool compilerFailed)
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        fixture.SetInvocations(fixture.Invocation("00000001", outcome));
        var result = fixture.Consume();
        Assert.Equal(missing, result.MissingEvidence);
        Assert.Equal(compilerFailed, result.CompilerFailed);
        Assert.Equal(WorkerBuildEvidenceGap.Unclassified, result.Gap);
        Assert.True(Assert.Single(fixture.Task.WorkerBuildInvocations).Consumed);
        Assert.Equal(outcome != WorkerBuildOutcome.Success,
            result.Applicability!.Reasons.Contains(WorkerBuildApplicabilityReason.TerminalNonSuccess));
        Assert.True(Assert.Single(result.Applicability.Invocations).TreeMatches);
        Assert.True(Assert.Single(result.Applicability.Invocations).CoversProjects);
    }

    [Fact]
    public void Consume_PartialInvocations_DoesNotUnionCoverage()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        var directory = Path.Combine(fixture.Root, "src/Other");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Other.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(directory, "Other.cs"), "class Other { }");
        fixture.SetInvocations(fixture.Invocation("00000001"),
            fixture.Invocation("00000002", projects: ["src/Other/Other.csproj"]));
        var result = fixture.Consume();
        Assert.Equal(WorkerBuildEvidenceGap.MissingApplicableInvocation, result.Gap);
        Assert.Equal(2, result.Applicability!.RequiredProjects!.Count);
        Assert.All(result.Applicability.Invocations, comparison =>
        {
            Assert.True(comparison.TreeMatches);
            Assert.False(comparison.CoversProjects);
            Assert.Single(comparison.MissingProjects!);
        });
        Assert.All(fixture.Task.WorkerBuildInvocations, i => Assert.False(i.Consumed));
    }

    [Fact]
    public async Task Consume_PendingInvocation_ExplainsStatusWithoutConsuming()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        WorkerBuildConsumption? observed = null;
        var terminal = await fixture.RunHelper(invocation =>
        {
            if (invocation.Phase == WorkerBuildPhase.Requested) observed = fixture.Consume();
        });
        Assert.NotNull(observed);
        Assert.True(observed.MissingEvidence);
        Assert.False(observed.CompilerFailed);
        Assert.Equal(WorkerBuildEvidenceGap.Unclassified, observed.Gap);
        var comparison = Assert.Single(observed.Applicability!.Invocations);
        Assert.Equal(terminal.Id, comparison.InvocationId);
        Assert.Contains(WorkerBuildApplicabilityReason.PendingInvocation, comparison.Reasons);
        Assert.True(comparison.TreeMatches);
        Assert.True(comparison.CoversProjects);
        Assert.False(comparison.CustodyReleased);
        Assert.False(terminal.Consumed);
    }

    [Fact]
    public void Consume_MultipleMatches_PreservesDeadlineAndCaseInsensitiveCoverage()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        var earlier = fixture.Invocation("00000001");
        var later = fixture.Invocation("00000002", projects: [WorkerBuildApplicabilityFixture.Project.ToUpperInvariant()]) with
            { Deadline = earlier.Deadline.AddMinutes(1) };
        fixture.SetInvocations(earlier, later);
        var result = fixture.Consume();
        Assert.False(result.MissingEvidence);
        Assert.Contains("worker_build_invocation=00000002;", result.Diagnostic);
        Assert.True(fixture.Task.WorkerBuildInvocations.Single(i => i.Id == later.Id).Consumed);
        Assert.False(fixture.Task.WorkerBuildInvocations.Single(i => i.Id == earlier.Id).Consumed);
        Assert.All(result.Applicability!.Invocations, comparison => Assert.True(comparison.CoversProjects));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("candidate")]
    [InlineData("corrupt-digest")]
    [InlineData("corrupt-projects")]
    [InlineData("pending")]
    public void Capture_UnavailableOrPendingEvidence_PreservesIndependentFacts(string control)
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        var invocation = fixture.Invocation("00000001", control == "pending" ? null : WorkerBuildOutcome.Success);
        if (control == "corrupt-digest") invocation = invocation with { TreeDigest = "invalid" };
        if (control == "corrupt-projects") invocation = invocation with { Projects = ["C:/private/Feature.csproj"] };
        var explanation = WorkerBuildApplicabilityExplanation.Capture(fixture.DispatchId,
            control == "candidate" ? null : fixture.Digest(), control == "scope" ? null : fixture.Scope(), [invocation]);
        var comparison = Assert.Single(explanation.Invocations);
        Assert.True(comparison.DispatchMatches);
        Assert.Contains(control switch
        {
            "scope" or "corrupt-projects" => WorkerBuildApplicabilityReason.UnresolvedScope,
            "pending" => WorkerBuildApplicabilityReason.PendingInvocation,
            _ => WorkerBuildApplicabilityReason.UnresolvedCandidate
        }, explanation.Reasons);
        if (control is "candidate" or "corrupt-digest") Assert.Null(comparison.TreeMatches);
        else Assert.True(comparison.TreeMatches);
        if (control is "scope" or "corrupt-projects") Assert.Null(comparison.CoversProjects);
        else Assert.True(comparison.CoversProjects);
        Assert.Null(comparison.ChangedPaths);
    }

    [Fact]
    public void Format_LargeCollections_BoundsOutputAndKeepsOmittedReasons()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        var seed = fixture.Invocation("00000000", projects: Enumerable.Range(0, 40)
            .Select(p => $"src/Project{p:D2}/Project{p:D2}.csproj").ToArray());
        var invocations = Enumerable.Range(0, 30).Select(i => seed with { Id = i.ToString("D8"),
            Outcome = i == 29 ? WorkerBuildOutcome.ApparatusFailure : WorkerBuildOutcome.Success }).ToArray();
        var explanation = WorkerBuildApplicabilityExplanation.Capture(fixture.DispatchId, fixture.Digest(), fixture.Scope(), invocations);
        Assert.Equal(8, explanation.Invocations.Count);
        Assert.Equal(22, explanation.OmittedInvocations);
        Assert.Contains(WorkerBuildApplicabilityReason.TerminalNonSuccess, explanation.Reasons);
        var formatted = WorkerBuildApplicabilityFormatter.Format(explanation);
        Assert.True(formatted.Length <= WorkerBuildApplicabilityFormatter.RenderLimit);
        Assert.Contains("terminal-non-success; next=inspect", formatted);
        Assert.Contains("omitted=24", formatted);
        Assert.Contains("truncated=true", formatted);
        Assert.DoesNotContain("00000029", formatted);
    }

    [Fact]
    public void Format_UntrustedPayloads_ExcludesLogsEnvironmentAndAbsolutePaths()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        var invocation = fixture.Invocation("00000001") with
        {
            Diagnostic = "ENVIRONMENT_SECRET full build log",
            WorktreeRoot = "C:/private-root",
            ScopeIdentity = "C:/private-scope",
            Projects = ["C:/private/Feature.csproj"]
        };
        var explanation = WorkerBuildApplicabilityExplanation.Capture(fixture.DispatchId, fixture.Digest(), fixture.Scope(), [invocation],
            (_, _) => throw new IOException("ENVIRONMENT_SECRET"));
        var rendered = WorkerBuildApplicabilityFormatter.Format(explanation);
        Assert.Contains("covers_projects=unknown", rendered);
        Assert.Contains("changed_paths=unknown", rendered);
        Assert.DoesNotContain("ENVIRONMENT_SECRET", rendered);
        Assert.DoesNotContain("private", rendered);
        Assert.DoesNotContain("full build log", rendered);
    }

    [Fact]
    public void Format_UncreditedCapacityOutcome_DoesNotCreateRecoverySignal()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        fixture.SetInvocations(fixture.Invocation("00000001", WorkerBuildOutcome.CapacityBeforeStart,
            WorktreeTreeDigest.Algorithm + ":" + new string('1', 40)));
        var receipt = fixture.Consume();
        Assert.Equal(WorkerBuildEvidenceGap.MissingApplicableInvocation, receipt.Gap);
        var resolution = new OrchestratorBuildEvidenceResolution(receipt.MissingEvidence, receipt.CompilerFailed, receipt.Diagnostic)
            { Applicability = receipt.Applicability };
        var diagnostic = resolution.AppendDiagnostic(string.Empty);
        Assert.Contains("recorded_status=CapacityBeforeStart", diagnostic);
        Assert.Equal(WorkerBuildNoBuildCause.None, WorkerBuildCheckRecoveryText.NoBuildCause(diagnostic));
        Assert.False(Assert.Single(fixture.Task.WorkerBuildInvocations).Consumed);
    }

    [Fact]
    public void CompareRecordedTrees_ImmutableObjects_ReportsOnlySupportedPaths()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        string Git(params string[] args)
        {
            var result = GitCli.RunForRepository(fixture.Root, args);
            Assert.True(result.Succeeded, result.Error);
            return result.Output.Trim();
        }
        Git("add", "-A");
        var before = WorktreeTreeDigest.Algorithm + ":" + Git("write-tree");
        File.AppendAllText(Path.Combine(fixture.Root, WorkerBuildApplicabilityFixture.Source), "\nclass Later { }");
        Git("add", "-A");
        var after = WorktreeTreeDigest.Algorithm + ":" + Git("write-tree");
        Assert.Equal(new[] { WorkerBuildApplicabilityFixture.Source },
            WorkerBuildApplicabilityExplanation.CompareRecordedTrees(fixture.Root, before, after));
        Assert.Empty(WorkerBuildApplicabilityExplanation.CompareRecordedTrees(fixture.Root, after, after)!);
        Assert.Null(WorkerBuildApplicabilityExplanation.CompareRecordedTrees(fixture.Root, "invalid", after));
        Assert.Null(WorkerBuildApplicabilityExplanation.CompareRecordedTrees(fixture.Root,
            WorktreeTreeDigest.Algorithm + ":" + new string('1', 40), after));
    }
}
