using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every scenario owns its repository, storage roots and coordinator.
public sealed class AcceptanceCohortWorkflowTestsLongCohortRepeatedRuns : AcceptanceCohortWorkflowBackgroundSupport
{
    [Fact]
    public async Task ProductionBatch_LongCohortGateLandsBothGoalsAcrossRepeatedRuns()
    {
        // xUnit serializes a class's tests. Run its sibling bodies explicitly, with fresh
        // fixture state, inside this test's host-capacity slot (one sibling at a time).
        await using var membership = new AcceptanceCohortWorkflowTestsBackgroundGateMembership();
        await using var progress = new AcceptanceCohortWorkflowTestsProgressRecording();
        await using var capacity = new AcceptanceCohortWorkflowTestsCapacityIsolation();
        await using var production = new AcceptanceCohortWorkflowTestsProductionBatchReconciliation();
        Action[] siblingFacts =
        [
            progress.ConductLog_FairnessYield_PersistsTypedDecision,
            progress.CohortProgressEvents_AreStructuredForBothMembersWithoutUnknownGoal,
            membership.BackgroundCohortGate_MainAdvanceDoesNotLaunchDuplicateForSameMembers,
            membership.BackgroundCohortGate_OverlappingMembersAreHeldByActualOwner,
            membership.BackgroundCohortGate_SamePairCanRegisterWhenIncumbentCompletesAfterSweep,
            capacity.IndependentWorkspaceDrivers_OverlapWithoutSharingStorageRoots,
            capacity.ProductionBatch_TwoLiveCohortRootsFillSharedAcceptanceCapacity,
            production.ProductionBatch_OrdinaryParallelAcceptanceStillStartsPromptlyAndReconcilesLater,
            capacity.CohortStableSlotLease_RefusesToExceedTrustedHostGateCap
        ];
        Func<string, Task> siblingTheory =
            membership.BackgroundCohortGate_TerminalRunReleasesMembersWhileDisjointRunRemainsActive;
        string[] theoryRows = ["succeeded", "failed", "cancelled"];
        var siblingTypes = new[]
        {
            typeof(AcceptanceCohortWorkflowTestsBackgroundGateMembership),
            typeof(AcceptanceCohortWorkflowTestsProgressRecording),
            typeof(AcceptanceCohortWorkflowTestsCapacityIsolation),
            typeof(AcceptanceCohortWorkflowTestsProductionBatchReconciliation),
            GetType()
        };
        var expectedMethods = siblingTypes.SelectMany(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
            .Select(method => method.Name)
            .Except([
                nameof(AcceptanceCohortWorkflowTestsProductionBatchReconciliation.ProductionBatch_LongCohortGateDoesNotBlockTicksOrOperatorIntents_AndReconcilesLater),
                nameof(ProductionBatch_LongCohortGateLandsBothGoalsAcrossRepeatedRuns)
            ]);
        Assert.Equal(expectedMethods.Order(StringComparer.Ordinal),
            siblingFacts.Select(test => test.Method.Name).Append(siblingTheory.Method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(siblingTheory.Method.GetCustomAttributes<InlineDataAttribute>()
                .Select(row => Assert.IsType<string>(Assert.Single(row.Data))).Order(StringComparer.Ordinal),
            theoryRows.Order(StringComparer.Ordinal));

        using var stopSiblings = new CancellationTokenSource();
        using var cohortHeld = new ManualResetEventSlim();
        var firstSiblingPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedSiblingPasses = 0;
        var siblingLoad = Task.Run(async () =>
        {
            try
            {
                Assert.True(cohortHeld.Wait(TimeSpan.FromMinutes(2)),
                    "The first scenario did not signal its held cohort gate to sibling tests.");
                do
                {
                    foreach (var test in siblingFacts)
                    {
                        try { test(); }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"Sibling {test.Method.Name} failed under cohort load.", ex);
                        }
                    }
                    foreach (var row in theoryRows)
                    {
                        try { await siblingTheory(row); }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"Sibling {siblingTheory.Method.Name}({row}) failed under cohort load.", ex);
                        }
                    }
                    completedSiblingPasses++;
                    firstSiblingPass.TrySetResult();
                } while (!stopSiblings.IsCancellationRequested);
            }
            catch (Exception ex)
            {
                firstSiblingPass.TrySetException(ex);
                throw;
            }
        });
        try
        {
            for (var iteration = 1; iteration <= 20; iteration++)
            {
                try
                {
                    AssertLongCohortGateLandsBothGoals(iteration, iteration == 1 ? ObserveSiblingOverlap : null);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Long-cohort iteration {iteration}/20 failed.", ex);
                }
            }
        }
        finally
        {
            stopSiblings.Cancel();
            cohortHeld.Set();
            await siblingLoad;
        }
        Assert.True(completedSiblingPasses > 0, "No complete sibling test pass overlapped the cohort scenarios.");

        void ObserveSiblingOverlap()
        {
            cohortHeld.Set();
            Assert.True(firstSiblingPass.Task.Wait(TimeSpan.FromMinutes(2)),
                "Sibling tests did not complete their first pass while the cohort gate was held.");
        }
    }
}
