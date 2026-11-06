using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;

// Parallel-safe: unique repository, decision store and instance derivation.
public sealed class AdmissionFactsApprovalHoldDerivationTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedInputs_ReuseApprovalAndChangesDeriveAgain(bool changeApproval)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Approval facts");
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Repository);
        var decisions = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var evaluations = 0;
        var reader = new AdmissionFactsTickReader(fixture.Repository, "main", (inputs, _) => inputs.Select(input =>
        {
            Interlocked.Increment(ref evaluations);
            var denylist = LandingExecutor.LoadLandingDenylist(input.Repository, input.Main).Denylist;
            var approval = LandingDenylistOwnerGate.Evaluate(input.Repository, denylist, decisions, input.GoalId.Value,
                input.Main, input.Branch, [AdditiveConflictGitFixture.WidgetPath]);
            return AdmissionFactsTestData.Fact(input) with { Approval = approval };
        }).ToArray());
        WriteDenylist("watched");
        try
        {
            Prep();
            AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Approval facts did not finish.");
            var first = Prep();
            var original = Assert.IsType<AdmissionFacts>(reader.Facts(goal.Id));
            Assert.False(original.Approval.Approved);
            Assert.Contains("prep_approval_hold_cache=hits:1,misses:0", first);
            var second = Prep();
            Assert.Contains("prep_approval_hold_cache=hits:1,misses:0", second);
            Assert.Equal(1, evaluations);
            if (changeApproval) Approve(workspace, goal, fixture.OriginalHead);
            else WriteDenylist("changed-rule");
            Prep();
            AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Changed approval inputs did not finish.");
            Prep();
            Assert.Equal(2, evaluations);
            var changed = Assert.IsType<AdmissionFacts>(reader.Facts(goal.Id));
            Assert.NotEqual(original.Inputs, changed.Inputs);
            if (changeApproval) Assert.True(changed.Approval.Approved);
            else Assert.Equal("changed-rule", Assert.Single(changed.Approval.Matches).RuleId);
        }
        finally { reader.Derivation.Stop(); AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Approval worker did not stop."); }

        string Prep()
        {
            var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
            using (ledger.Activate())
            {
                reader.Begin([goal], _ => []);
                reader.ApplyApproval(goal.Id, reader.Project(goal, ConductorAutonomyPolicy.Permissive,
                    _ => new(WorkspaceExists: true), _ => true));
            }
            return ledger.FormatAdmission(TimeSpan.Zero);
        }

        void WriteDenylist(string id)
        {
            Directory.CreateDirectory(Path.Combine(fixture.Repository, "config"));
            File.WriteAllText(Path.Combine(fixture.Repository, "config", "landing-denylist.json"),
                $$"""{"version":1,"rules":[{"id":"{{id}}","reason":"Approval test","paths":["src/Widget.cs"]}]}""");
            AdditiveConflictGitFixture.Commit(fixture.Repository, "Change denylist");
        }
    }
}
