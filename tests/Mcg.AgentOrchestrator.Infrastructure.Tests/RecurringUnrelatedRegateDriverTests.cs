using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static RecurringUnrelatedTestRuleTests;

// Parallel-safe: source reads are injected; the sole index is inside this test's temporary root.
public sealed class RecurringUnrelatedRegateDriverTests
{
    [Fact(DisplayName = "Recurring failures re-enter the gate twice without charging either retry counter", Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ReentersGateThenHoldsWithMappedOwner()
    {
        WithRoot(root =>
        {
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var fixture = new RecurringUnrelatedTestDispositionTests.Fixture();
            var gates = 0;
            var failures = 0;
            var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => { gates++; return Red(); },
                retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Unrelated failures must not route Developer"),
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                recordAcceptanceFailure: (g, checks, branch, main, attributions, baseline) =>
                {
                    failures++;
                    g.RecordAcceptanceFailure(checks, Now, branch, main, attributions, baseline);
                },
                getLandingFileScopes: _ => ["src/CandidateFeature.cs"],
                apparatusRedGate: Gate(root));
            driver.RecurringUnrelatedTests = fixture.Handler();
            for (var ordinal = 1; ordinal <= 2; ordinal++)
            {
                var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
                Assert.Equal(RecurringUnrelatedTestDisposition.RegateKey, held.Decision!.DiscriminatingEvidence);
                Assert.Equal(GoalLifecycleState.Verified, held.State);
                Assert.Equal(ordinal, gates);
                Assert.Equal(0, failures);
                Assert.Null(goal.LatestAcceptanceFailure);
                Assert.Equal(0, task.CriterionRetryCount);
                Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            }
            var third = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(third.Outcome);
            Assert.Equal(3, gates);
            Assert.Equal(1, failures);
            Assert.Equal(ConductorHoldOwner.Operator, hold.Owner);
            Assert.Contains(Assert.Single(fixture.Store.Items).Key, hold.Reason, StringComparison.Ordinal);
            ConductorLandingBlockerRecorder.Observe(kernel, goal, hold, Now, []);
            Assert.Equal("operator", goal.LandingBlocker!.NextActor);
            Assert.Equal("land-fix-item", goal.LandingBlocker.NextVerb);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        });
    }

    [Fact(DisplayName = "A Genuine companion preserves the existing Developer retry route")]
    public void MixedGenuineStillRoutesDeveloper()
    {
        WithRoot(root =>
        {
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var retries = 0;
            var fixture = new RecurringUnrelatedTestDispositionTests.Fixture();
            var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => Red(mixed: true),
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                { retries++; return kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause); },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/CandidateFeature.cs"], apparatusRedGate: Gate(root));
            driver.RecurringUnrelatedTests = fixture.Handler();
            Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Equal(1, retries);
            Assert.Equal(1, task.CriterionRetryCount);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Empty(fixture.Store.Items);
            Assert.Empty(fixture.Counts);
        });
    }

    private static ApparatusRedGate Gate(string root) => new(new AcceptanceFailingTestIndex(Path.Combine(root, "index.jsonl")),
        _ => root, () => Now, createSourceRequest: _ => new AcceptanceTestSourceResolver.Request(root,
            enumerate: _ => [Path.Combine(root, "tests", "UnreliableTests.cs")],
            read: _ => "namespace Example; public class UnreliableTests { public void DrainCompletes() {} public void Genuine() {} }",
            directoryExists: _ => true));

    private static AcceptanceVerificationSummary Red(bool mixed = false)
    {
        string[] identities = mixed ? [Identity, "Example.UnreliableTests.Genuine"] : [Identity];
        var check = new AcceptanceCheckResult("verifier lane", false, 1, "drain was incomplete",
            FailingTestIdentities: identities,
            FailingTestAttributions: identities.Select(identity => new AcceptanceTestFailureAttribution(identity,
                AcceptanceTestFailureOrigin.Introduced, "passed on merge base", new("Failed", "fake-receipt"))).ToArray());
        return new(false, [check], FailedChecks: [check.Name], BranchHeadSha: "candidate", MainHeadSha: "main");
    }

    private static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "recurrence-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
