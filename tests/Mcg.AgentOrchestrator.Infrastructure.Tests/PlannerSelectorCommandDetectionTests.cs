using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixtures own their directories and kernel sources, without changing defaults.
public sealed class PlannerSelectorCommandDetectionTests
{
    [Theory]
    [InlineData("combat math.returns 70 for an equal matchup")]
    [InlineData("returns 70 for an equal matchup")]
    [InlineData("src/game/combat-math.test.ts::returns 70 for an equal matchup")]
    public void SpacedIdentity_WithoutCheckLookup_RecordsOriginalSelector(string selector)
    {
        using var fixture = new Fixture();
        fixture.RejectCheckLookup();

        fixture.Record([selector]);

        AssertRecorded(fixture, [selector]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityBatch_BothRecordingPaths_RecordsWithoutRefusal(bool legacy)
    {
        using var fixture = new Fixture();
        fixture.RejectCheckLookup();
        string[] selectors = ["combat math.returns 70 for an equal matchup",
            "returns 70 for an equal matchup",
            "src/game/combat-math.test.ts::returns 70 for an equal matchup", "combat math"];

        fixture.Record(selectors, legacy);

        AssertRecorded(fixture, selectors);
    }

    [Theory]
    [InlineData("npm test", true)]
    [InlineData("npx vitest run", true)]
    [InlineData("vitest run", true)]
    [InlineData("tsc --noEmit", false)]
    [InlineData("crosswalk-test .", false)]
    [InlineData("jest src", true)]
    [InlineData("bun test", true)]
    [InlineData("deno test", true)]
    [InlineData("python -m pytest", true)]
    [InlineData("vitest src/a.test.ts::name", true)]
    [InlineData("tsc --build", true)]
    [InlineData("crosswalk-test other", true)]
    [InlineData("  VITEST\trun  ", true)]
    [InlineData("npm ci --extra", true)]
    public void RunnerFirstSelector_LoadsChecks_RefusesOnlyUndeclaredCommands(
        string selector, bool refused)
    {
        using var fixture = new Fixture();

        fixture.Record([selector]);

        Assert.Equal(1, fixture.CheckLookups);
        if (refused) AssertRefused(fixture, selector);
        else AssertRecorded(fixture, [selector]);
    }

    [Theory]
    [InlineData("npm ci")]
    [InlineData("./scripts/check.sh --all")]
    public void DeclaredCommand_ExactMatch_RecordsWithExistingMatcher(string selector)
    {
        using var fixture = new Fixture();
        var manifest = GoalAcceptanceVerifier.AcceptanceManifest.Load(fixture.Root, null);
        Assert.Single(manifest.Checks.Where(check => GateCheckSelectorMatcher.Matches(selector, check)));

        fixture.Record([selector]);

        AssertRecorded(fixture, [selector]);
    }

    [Theory]
    [InlineData("./scripts/check.sh --all --fast")]
    [InlineData("vitestish run")]
    public void UnrecognizedFirstWord_WithArguments_RemainsIdentity(string selector)
    {
        using var fixture = new Fixture();
        fixture.RejectCheckLookup();

        fixture.Record([selector]);

        AssertRecorded(fixture, [selector]);
    }

    [Fact]
    public void Refusal_ListsDeclaredChecks_ExplainsBothStackIdentityForms()
    {
        using var fixture = new Fixture();

        fixture.Record(["vitest run"]);

        AssertRefused(fixture, "vitest run");
        var reason = fixture.Planner.LastVerification!.OrchestratorFailureReason!;
        Assert.Contains("Declared check names:", reason);
        Assert.Contains("npm ci", reason);
        Assert.Contains("<test file>::<test name>", reason);
        Assert.Contains("bare test name", reason);
        Assert.Contains("on .NET, use the class or Namespace.Class.Method", reason);
    }

    private static void AssertRecorded(Fixture fixture, string[] selectors)
    {
        Assert.Equal(selectors, Assert.Single(fixture.Goal.GateTestObligations).Selectors);
        Assert.Empty(fixture.Kernel.ExportSnapshot().HumanInputRequests);
        Assert.NotEqual(WorkTaskStatus.Failed, fixture.Planner.Status);
    }

    private static void AssertRefused(Fixture fixture, string selector)
    {
        Assert.Equal(WorkTaskStatus.Failed, fixture.Planner.Status);
        var verification = fixture.Planner.LastVerification!;
        Assert.Equal("planner-output-contract-rejected", verification.CompletionVerdictRule);
        Assert.False(verification.CompletionVerdictVerifiedSuccess);
        Assert.Contains(selector, verification.OrchestratorFailureReason!);
        Assert.Empty(fixture.Goal.GateTestObligations);
        Assert.Empty(fixture.Kernel.ExportSnapshot().HumanInputRequests);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-selector-detection-" + Guid.NewGuid().ToString("N"));
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal TaskSpec Planner { get; } = new(TaskId.New(), "Plan", AgentRole.Planner);
        internal Goal Goal { get; }
        internal int CheckLookups { get; private set; }

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"), """
                {"checks":[
                  {"name":"npm ci","type":"command","command":"npm","arguments":["ci"]},
                  {"name":"npx tsc --noEmit","type":"command","command":"npx","arguments":["tsc","--noEmit"]},
                  {"name":"crosswalk-test .","type":"crosswalk-test"},
                  {"name":"script check","type":"command","command":"./scripts/check.sh","arguments":["--all"]}
                ]}
                """);
            var source = DeclaredGateCheckSource.For(null);
            Kernel.SetDeclaredGateCheckSource((goal, task) =>
            {
                CheckLookups++;
                return source(goal, task);
            });
            Goal = Kernel.CreateGoal("Classify Planner selectors", [Planner]);
            Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Classify selectors",
                ["Acceptance executes candidate checks."], VerificationClass.TestVerifiable, [], []));
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Kernel.RecordTaskDispatch(Goal.Id, Planner.Id,
                new TaskDispatchRecord("planner", "plan", Root, DateTimeOffset.UtcNow));
        }

        internal void RejectCheckLookup() => Kernel.SetDeclaredGateCheckSource((_, _) =>
            throw new InvalidOperationException("Identity-only evidence must not resolve declared checks."));

        internal void Record(string[] selectors, bool legacy = false)
        {
            var output = "PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(new
            {
                criterion_index = 1, evidence_key = "gate", availability = "post-implementation",
                owner = "acceptance-gate", tests = selectors, needed = "candidate receipt",
                reason = "Acceptance executes the candidate"
            });
            var parsed = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);
            Assert.False(parsed.IsMalformed, parsed.Diagnostic);
            var directive = Assert.Single(parsed.Directives);
            output += "\nWORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: not-run\ncommit: none\nblockers: none\nmodel_fit: adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";
            Kernel.RecordDispatchExecutionResult(Goal.Id, Planner.Id, new TaskVerificationRecord(
                "plan", Root, 0, output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true,
                HumanInputQuestion: legacy ? directive.Question : null,
                HumanInputKind: legacy ? directive.Kind : null,
                HumanInputEvidenceOwner: legacy ? directive.EvidenceOwner : null,
                DispatchAttemptAt: Planner.LastDispatch!.DispatchedAt,
                PlannerEvidenceBatch: legacy ? new(null, null) : parsed));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
