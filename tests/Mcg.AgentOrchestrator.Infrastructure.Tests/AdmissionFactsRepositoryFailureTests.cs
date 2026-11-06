using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its repository, stores, queue and background worker.
public sealed class AdmissionFactsRepositoryFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateFailure_PublishesOtherMembersAndOtherMainGroups(bool mergeFailure)
    {
        using var fixture = new AdditiveConflictGitFixture();
        var baseHead = AdditiveConflictGitFixture.Git(fixture.Repository, "rev-parse", "main^").Trim();
        var before = Input(fixture, GoalId.New()) with
        { Branch = fixture.MainHead, Main = baseHead, ExecutionHead = baseHead };
        var bad = Input(fixture, GoalId.New()) with { Branch = fixture.MainHead };
        bad = mergeFailure ? bad with { Branch = new string('e', 40) } :
            bad with { ExecutionHead = new string('e', 40) };
        var after = Input(fixture, GoalId.New()) with { Branch = fixture.MainHead };
        var otherMain = Input(fixture, GoalId.New()) with
        { Main = fixture.OriginalHead, ExecutionHead = fixture.OriginalHead };
        var repository = new AdmissionFactsRepository("main");
        var queue = new AdmissionFactsDerivation(repository.Derive);
        try
        {
            foreach (var input in new[] { before, bad, after, otherMain }) queue.Request(input);
            queue.Start();
            AdmissionFactsTestData.Wait(queue.Completion, "Failure-isolation batch did not finish.");
            foreach (var input in new[] { before, after, otherMain })
                Assert.Equal(input, Assert.IsType<AdmissionFacts>(queue.Read(input)).Inputs);
            Assert.Null(queue.Read(bad));
            Assert.Equal(1, queue.PendingCount);
            var failure = Assert.Single(repository.DrainReports().Where(line =>
                line.StartsWith("ADMISSION_FACTS_FAILED ", StringComparison.Ordinal)));
            Assert.Contains($"goal={bad.GoalId.Value} inputs={bad.Identity}", failure);
            Assert.Contains(mergeFailure ? "ReviewerMergeTreeStatusException" : "Admission diff failed", failure);
        }
        finally { queue.Stop(); AdmissionFactsTestData.Wait(queue.Completion, "Failure-isolation worker did not stop."); }
    }

    [Fact]
    public void ChangedApprovalInputs_ReportCauseInsteadOfSilentlyDroppingCandidate()
    {
        using var fixture = new AdditiveConflictGitFixture();
        var input = Input(fixture, GoalId.New()) with { Branch = fixture.MainHead, Approvals = "superseded" };
        AssertFailure(fixture, input, "Approval inputs changed during derivation.");
    }

    [Fact]
    public void DenylistReadFailure_PreservesFinishedFactsForOtherMain()
    {
        using var fixture = new AdditiveConflictGitFixture();
        var good = Input(fixture, GoalId.New()) with { Branch = fixture.MainHead };
        var bad = Input(fixture, GoalId.New()) with
        { Main = fixture.OriginalHead, ExecutionHead = fixture.OriginalHead };
        var failedReads = 0;
        using var runner = LandingExecutor.UseGitRunner((root, args) =>
        {
            if (args[0] == "ls-tree" && args.Contains(bad.Main))
            {
                failedReads++;
                return new(128, "", "injected denylist read failure") { ProcessStarted = true };
            }
            return GitCli.Run(root, args);
        });
        var repository = new AdmissionFactsRepository("main");
        Assert.Equal(good, Assert.Single(repository.Derive([good, bad], CancellationToken.None)).Inputs);
        Assert.Equal(1, failedReads);
        var failure = Assert.Single(repository.DrainReports().Where(line =>
            line.StartsWith("ADMISSION_FACTS_FAILED ", StringComparison.Ordinal)));
        Assert.Contains($"goal={bad.GoalId.Value} inputs={bad.Identity}", failure);
        Assert.Contains("injected denylist read failure", failure);
    }

    [Fact]
    public void UnavailableApproval_ReportsDiagnosticAndDoesNotPublishFacts()
    {
        using var fixture = new AdditiveConflictGitFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Repository, "config"));
        File.WriteAllText(Path.Combine(fixture.Repository, "config", "landing-denylist.json"),
            """{"version":1,"rules":[{"id":"failure-test","reason":"Protected path","paths":["src/Widget.cs"]}]}""");
        AdditiveConflictGitFixture.Commit(fixture.Repository, "Protect failure fixture");
        var head = AdditiveConflictGitFixture.Git(fixture.Repository, "rev-parse", "HEAD").Trim();
        var input = Input(fixture, fixture.GoalId) with { Main = head, ExecutionHead = head };
        var failedReads = 0;
        using var runner = LandingExecutor.UseGitRunner((root, args) =>
        {
            if (args[0] == "rev-parse" && args.Contains($"{input.Branch}^{{commit}}"))
            {
                failedReads++;
                return new(128, "", "injected approval read failure") { ProcessStarted = true };
            }
            return GitCli.Run(root, args);
        });
        AssertFailure(fixture, input, "Approval observation unavailable:");
        Assert.True(failedReads > 0, "The approval read failure was not exercised.");
    }

    [Fact]
    public void MissingConflictWorktree_ReportsProbeReasonAndRequestedInputs()
    {
        using var fixture = new AdditiveConflictGitFixture();
        var input = Input(fixture, GoalId.New());
        Assert.Null(GoalWorktrees.TryResolve(fixture.Repository, input.GoalId));
        AssertFailure(fixture, input, "reason=worktree-missing branch=unresolved main=unresolved");
    }

    [Fact]
    public void ChangedProbeHead_ReportsCauseAndDoesNotPublishOldInputs()
    {
        using var fixture = new AdditiveConflictGitFixture();
        var input = Input(fixture, fixture.GoalId);
        File.WriteAllText(Path.Combine(fixture.Worktree, "later.txt"), "later branch head");
        AdditiveConflictGitFixture.Commit(fixture.Worktree, "Advance probe head");
        AssertFailure(fixture, input, "Conflict probe inputs unavailable or changed:");
    }

    [Theory]
    [InlineData(true, "probe-cleanup-failed")]
    [InlineData(false, "merge-failed")]
    public void IndeterminateProbe_ReportsFailureAndRetriesUnchangedInputs(bool failAdd, string reason)
    {
        using var fixture = new AdditiveConflictGitFixture();
        var input = Input(fixture, fixture.GoalId);
        var repository = new AdmissionFactsRepository("main");
        var now = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        var injectFailure = true;
        var injected = 0;
        var queue = new AdmissionFactsDerivation((inputs, token) =>
        {
            // This hook is thread-local: install it on the worker and restore it before returning.
            var previous = GitCli.StartInfoObserver;
            GitCli.StartInfoObserver = info =>
            {
                previous?.Invoke(info);
                var args = info.ArgumentList;
                if (injectFailure && (failAdd
                        ? args.Contains("worktree") && args.Contains("add")
                        : args.Contains("status") && args.Contains("--porcelain=v2")))
                {
                    injected++;
                    args.Add("--admission-test-invalid-option");
                }
            };
            try { return repository.Derive(inputs, token); }
            finally { GitCli.StartInfoObserver = previous; }
        }, () => now);
        try
        {
            queue.Request(input);
            queue.Start();
            AdmissionFactsTestData.Wait(queue.Completion, "Indeterminate probe batch did not finish.");
            Assert.Equal(1, injected);
            Assert.Null(queue.Read(input));
            Assert.Equal(1, queue.PendingCount);
            var failure = Assert.Single(repository.DrainReports().Where(line =>
                line.StartsWith("ADMISSION_FACTS_FAILED ", StringComparison.Ordinal)));
            Assert.Contains($"goal={input.GoalId.Value} inputs={input.Identity}", failure);
            Assert.Contains($"Conflict probe indeterminate: reason={reason} branch={input.Branch} main={input.Main}", failure);

            queue.Start();
            AdmissionFactsTestData.Wait(queue.Completion, "Backoff check did not finish.");
            Assert.Equal(1, injected);
            Assert.Null(queue.Read(input));
            injectFailure = false;
            now = now.AddSeconds(31);
            queue.Start();
            AdmissionFactsTestData.Wait(queue.Completion, "Conflict probe retry did not finish.");
            var facts = Assert.IsType<AdmissionFacts>(queue.Read(input));
            Assert.Equal(input, facts.Inputs);
            Assert.Equal(AdditiveConflictProbeOutcome.Resolvable, facts.ConflictProbe!.Outcome);
            Assert.Equal(0, queue.PendingCount);
            Assert.DoesNotContain(repository.DrainReports(), line =>
                line.StartsWith("ADMISSION_FACTS_FAILED ", StringComparison.Ordinal));
        }
        finally { queue.Stop(); AdmissionFactsTestData.Wait(queue.Completion, "Conflict probe worker did not stop."); }
    }

    private static void AssertFailure(AdditiveConflictGitFixture fixture, AdmissionFactsInputs input, string detail)
    {
        var repository = new AdmissionFactsRepository("main");
        Assert.Empty(repository.Derive([input], CancellationToken.None));
        var failure = Assert.Single(repository.DrainReports().Where(line =>
            line.StartsWith("ADMISSION_FACTS_FAILED ", StringComparison.Ordinal)));
        Assert.Contains($"goal={input.GoalId.Value} inputs={input.Identity}", failure);
        Assert.Contains(detail, failure);
    }

    private static AdmissionFactsInputs Input(AdditiveConflictGitFixture fixture, GoalId goal) => new(
        fixture.Repository, goal, fixture.OriginalHead, fixture.MainHead, fixture.MainHead, "[]",
        AdmissionFactsInputs.ReadApprovals(fixture.Repository, goal));
}
