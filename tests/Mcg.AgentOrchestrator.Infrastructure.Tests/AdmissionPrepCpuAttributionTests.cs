using System.Diagnostics;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repository, decision store, caches and worker. Actual process CPU is
// diagnostic output only; assertions concern executed work, never a duration or speed ratio.
public sealed class AdmissionPrepCpuAttributionTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void WarmPaths_ReportRealCpuAndFactsBypassSnapshotAndOwnerEvaluation()
    {
        const int samples = 24;
        const string path = "src/Mcg.AgentOrchestrator.Core/AdmissionFacts.cs";
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "CPU attribution replay");
        using var fixture = new AdditiveConflictGitFixture(
            files: [new(path, "base\n", "main\n", "goal\n")], goalId: goal.Id);
        Directory.CreateDirectory(Path.Combine(fixture.Repository, "config"));
        File.WriteAllText(Path.Combine(fixture.Repository, "config", "landing-denylist.json"),
            $$"""{"version":1,"rules":[{"id":"cpu-replay","reason":"Protected fixture","paths":["{{path}}"]}]}""");
        AdditiveConflictGitFixture.Commit(fixture.Repository, "Protect attribution fixture");
        var derivations = 0;
        var reader = new AdmissionFactsTickReader(fixture.Repository, "main", (inputs, _) => inputs.Select(input =>
        {
            Interlocked.Increment(ref derivations);
            var denylist = LandingExecutor.LoadLandingDenylist(input.Repository, input.Main).Denylist;
            var decisions = CollaborationItemStore.ForDirectory(
                OrchestratorWorkspace.ForDirectory(input.Repository).OrchestratorDirectory);
            var approval = LandingDenylistOwnerGate.Evaluate(input.Repository, denylist, decisions,
                input.GoalId.Value, input.Main, input.Branch, [path]);
            return AdmissionFactsTestData.Fact(input) with { Approval = approval };
        }).ToArray());
        var current = new AdmissionFactsDriverState { Reader = reader };
        try
        {
            Begin();
            AdmissionFactsTestData.Wait(reader.Derivation.Completion, "CPU replay facts did not finish.");
            Begin();
            var facts = Assert.IsType<AdmissionFacts>(reader.Facts(goal.Id));
            Assert.False(facts.Approval.Approved);
            var ready = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(facts.Projector().Project(
                new(goal.Id, GoalLifecycleState.Verified, true, ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto)));
            var heads = new GateReadyProjectionCache.Heads(facts.Inputs.Branch, facts.Inputs.Main, facts.Inputs.ExecutionHead);
            var legacy = new AdmissionFactsDriverState();
            var legacyCache = new GateReadyProjectionCache();
            // Warm the retained legacy cache before measuring its hit path.
            Assert.Same(ready, Project(legacy, legacyCache));
            Sample(legacy, legacyCache, "retained-legacy", expectSnapshot: true);
            Sample(current, new(), "finished-facts", expectSnapshot: false);
            Assert.Equal(1, derivations);

            GateReadyCandidateProjectionResult Project(AdmissionFactsDriverState state, GateReadyProjectionCache cache) =>
                state.Project(goal, ConductorAutonomyPolicy.Permissive, cache,
                    _ => new(WorkspaceExists: true), _ => true, () => ready, _ => heads,
                    null, fixture.Repository, "main");

            void Sample(AdmissionFactsDriverState state, GateReadyProjectionCache cache, string mode, bool expectSnapshot)
            {
                var ledger = new ConductorTickStepLedger(() => Environment.CpuUsage.TotalTime);
                var timer = Stopwatch.StartNew();
                using (ledger.Activate())
                    for (var sample = 0; sample < samples; sample++)
                    {
                        var projection = ConductorTickStepLedger.Measure("prep-projection", () => Project(state, cache));
                        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(projection);
                        var held = ConductorTickStepLedger.Measure("prep-approval-hold", () =>
                        {
                            var repeated = ConductorTickStepLedger.Measure("approval-projections", () => Project(state, cache));
                            return state.ApplyApproval(goal, repeated, fixture.Repository);
                        });
                        Assert.Equal(GateReadyCandidateExclusionReason.LandingHeld,
                            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(held).Reason);
                    }
                var report = ledger.FormatAdmission(timer.Elapsed);
                output.WriteLine($"ADMISSION_CPU_SAMPLE mode={mode} samples={samples} {report}");
                Assert.Contains($"prep_projection_cache=hits:{samples * 2},misses:0", report);
                Assert.Equal(expectSnapshot ? samples * 2 : 0, Calls(report, "projection-key-snapshot"));
                Assert.Equal(expectSnapshot ? samples * 2 : 0, Calls(report, "projection-key-refs"));
                Assert.Equal(expectSnapshot ? samples : 0, Calls(report, "projection-owner-approval"));
                Assert.Equal(expectSnapshot ? 0 : samples, Calls(report, "approval-facts-lookup"));
            }
        }
        finally
        {
            current.Stop();
            AdmissionFactsTestData.Wait(reader.Derivation.Completion, "CPU replay worker did not stop.");
        }

        void Begin() => current.Begin([goal], kernel, fixture.Repository, "main", null, null, _ => false);
    }

    private static int Calls(string report, string step)
    {
        var match = Regex.Match(report, $@"(?:=|,){step}:\d+:\d+:(\d+)(?:,| |$)");
        Assert.True(match.Success, $"Missing timing sub-step {step}: {report}");
        return int.Parse(match.Groups[1].Value);
    }
}
