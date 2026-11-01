using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its directory and kernel source; no default source is changed.
public sealed class PlannerCommandSelectorValidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndeclaredCommandsRejectBothRecordingPathsWithoutObligationsOrWaits(bool legacy)
    {
        using var fixture = new Fixture();
        fixture.Record(["npm test", "npm run build"], legacy);

        AssertRejected(fixture, "npm test", "npm run build", "npm ci", "npx tsc --noEmit", "crosswalk-test .");
        var diagnostic = fixture.Planner.LastVerification!.OrchestratorFailureReason!;
        Assert.Contains("existing checks pass", diagnostic);
        Assert.Contains("gate verdict", diagnostic);
        Assert.Contains("needs no test obligation", diagnostic);
        Assert.Contains("Name a declared check or omit", diagnostic);
    }

    [Theory]
    [InlineData("npx tsc --noEmit")]
    [InlineData("npm ci")]
    [InlineData("crosswalk-test .")]
    [InlineData("tsc --noEmit")]
    [InlineData("  npx tsc --noEmit  ")]
    public void DeclaredCommandsRecordOriginalSelectors(string selector)
    {
        using var fixture = new Fixture();
        fixture.Record([selector]);

        var obligation = Assert.Single(fixture.Goal.GateTestObligations);
        Assert.Equal([selector], obligation.Selectors);
        Assert.Empty(fixture.Kernel.ExportSnapshot().HumanInputRequests);
        Assert.NotEqual(WorkTaskStatus.Failed, fixture.Planner.Status);
        if (selector.Contains("tsc", StringComparison.Ordinal))
        {
            var manifest = GoalAcceptanceVerifier.AcceptanceManifest.Load(fixture.Root, null);
            var outcomes = new ProspectiveEvidenceGateResolver.GateCheckOutcomes(manifest.Checks,
                new Dictionary<string, bool>(), PassedWithNoFailedChecks: true);
            Assert.Equal(GateCheckSelectorMatcher.Outcome.Satisfied,
                GateCheckSelectorMatcher.Grade(Assert.Single(obligation.Selectors), outcomes));
        }
    }

    [Theory]
    [InlineData("src/game/engine.test.ts::resolves a choice")]
    [InlineData("GateTests.Check(value: 2)")]
    [InlineData("Namespace.GateTests")]
    [InlineData("Check")]
    public void TestIdentitiesStayUnchangedWithoutResolvingManifest(string selector)
    {
        using var fixture = new Fixture();
        fixture.Kernel.SetDeclaredGateCheckSource((_, _) =>
            throw new InvalidOperationException("Identity-only evidence must not resolve declared checks."));
        fixture.Record([selector]);

        Assert.Equal([selector], Assert.Single(fixture.Goal.GateTestObligations).Selectors);
        Assert.Empty(fixture.Kernel.ExportSnapshot().HumanInputRequests);
        Assert.NotEqual(WorkTaskStatus.Failed, fixture.Planner.Status);
    }

    [Fact]
    public void InvalidSiblingRejectsEntireBatchBeforeAnyObligationOrWait()
    {
        using var fixture = new Fixture();
        var batch = Directive(["npm ci"], "valid") + "\n" +
            Directive(null, "operator", "operator") + "\n" +
            Directive(["npm test"], "invalid-first") + "\n" +
            Directive(["npm run build"], "invalid-second");
        fixture.RecordBatch(batch);

        AssertRejected(fixture, "npm test", "npm run build");
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("npx")]
    [InlineData("pnpm")]
    [InlineData("yarn")]
    [InlineData("bunx")]
    [InlineData("node")]
    [InlineData("dotnet")]
    [InlineData("go")]
    [InlineData("cargo")]
    [InlineData("pytest")]
    [InlineData("make")]
    [InlineData("busted")]
    [InlineData(" NPM\tunknown ")]
    [InlineData("pytest tests/test_game.py::choice")]
    [InlineData("node check(value: 2)")]
    [InlineData("custom-runner file.ts")]
    public void RunnerVerbsAndWhitespaceCommandsRequireDeclarations(string selector)
    {
        using var fixture = new Fixture();
        fixture.Record([selector]);

        AssertRejected(fixture, selector);
    }

    [Fact]
    public void PowerShellRunnerSelectorRequiresDeclaration()
    {
        using var fixture = new Fixture();
        var selector = "pwsh";
        fixture.Record([selector]);

        AssertRejected(fixture, selector);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("malformed")]
    public void UnavailableDeclarationsRejectCommands(string manifestState)
    {
        using var fixture = new Fixture();
        var manifestPath = Path.Combine(fixture.Root, "config", "acceptance-manifest.json");
        if (manifestState == "missing") File.Delete(manifestPath);
        else File.WriteAllText(manifestPath, manifestState == "empty" ? "{\"checks\":[]}" : "malformed");
        fixture.Record(["npm test"]);

        AssertRejected(fixture, "npm test", "no declared checks are available");
    }

    [Fact]
    public void ProjectHomeOverrideIsAuthoritative()
    {
        using var fixture = new Fixture();
        var projectHome = Path.Combine(fixture.Root, "project-home");
        Directory.CreateDirectory(projectHome);
        File.WriteAllText(Path.Combine(projectHome, "acceptance-manifest.json"),
            "{\"checks\":[{\"name\":\"project check\",\"type\":\"command\",\"command\":\"npm\",\"arguments\":[\"test\"]}]}");
        fixture.Kernel.SetDeclaredGateCheckSource(DeclaredGateCheckSource.For(projectHome));
        fixture.Record(["npm ci"]);

        AssertRejected(fixture, "npm ci", "project check");
        Assert.DoesNotContain("'npx tsc --noEmit'", fixture.Planner.LastVerification!.OrchestratorFailureReason!);
    }

    [Fact]
    public void ProjectHomeCommandIsRecordedThroughExistingMatcher()
    {
        using var fixture = new Fixture();
        var projectHome = Path.Combine(fixture.Root, "project-home");
        Directory.CreateDirectory(projectHome);
        File.WriteAllText(Path.Combine(projectHome, "acceptance-manifest.json"),
            "{\"checks\":[{\"name\":\"project check\",\"type\":\"command\",\"command\":\"npm\",\"arguments\":[\"test\"]}]}");
        fixture.Kernel.SetDeclaredGateCheckSource(DeclaredGateCheckSource.For(projectHome));
        fixture.Record(["npm test"]);

        Assert.Equal(["npm test"], Assert.Single(fixture.Goal.GateTestObligations).Selectors);
        Assert.Empty(fixture.Kernel.ExportSnapshot().HumanInputRequests);
    }

    private static void AssertRejected(Fixture fixture, params string[] expectedText)
    {
        Assert.Equal(WorkTaskStatus.Failed, fixture.Planner.Status);
        Assert.Equal("planner-output-contract-rejected",
            fixture.Planner.LastVerification!.CompletionVerdictRule);
        Assert.False(fixture.Planner.LastVerification.CompletionVerdictVerifiedSuccess);
        foreach (var text in expectedText)
            Assert.Contains(text, fixture.Planner.LastVerification.OrchestratorFailureReason!);
        Assert.Contains("replan", fixture.Planner.LastVerification.OrchestratorFailureReason!);
        Assert.Empty(fixture.Goal.GateTestObligations);
        Assert.Empty(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
        Assert.Empty(fixture.Kernel.ExportSnapshot().HumanInputRequests);
    }

    private static string Directive(string[]? selectors, string key = "gate", string owner = "acceptance-gate") =>
        "PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(new
        {
            criterion_index = 1, evidence_key = key, availability = "post-implementation", owner,
            tests = selectors, needed = "candidate receipt", reason = "Acceptance executes the candidate"
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-command-selectors-" + Guid.NewGuid().ToString("N"));
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal TaskSpec Planner { get; } = new(TaskId.New(), "Plan", AgentRole.Planner);
        internal Goal Goal { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"), """
                {"checks":[
                  {"name":"npm ci","type":"command","command":"npm","arguments":["ci"]},
                  {"name":"npx tsc --noEmit","type":"command","command":"npx","arguments":["tsc","--noEmit"]},
                  {"name":"crosswalk-test .","type":"crosswalk-test"}
                ]}
                """);
            Kernel.SetDeclaredGateCheckSource(DeclaredGateCheckSource.For(null));
            Goal = Kernel.CreateGoal("Validate Planner command selectors", [Planner]);
            Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Validate selectors",
                ["Acceptance executes candidate checks."], VerificationClass.TestVerifiable, [], []));
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Kernel.RecordTaskDispatch(Goal.Id, Planner.Id, new TaskDispatchRecord("planner", "plan", Root, DateTimeOffset.UtcNow));
        }

        internal void Record(string[] selectors, bool legacy = false) => RecordBatch(Directive(selectors), legacy);

        internal void RecordBatch(string output, bool legacy = false)
        {
            var parsed = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);
            Assert.False(parsed.IsMalformed, parsed.Diagnostic);
            Assert.NotEmpty(parsed.Directives);
            var directive = legacy ? Assert.Single(parsed.Directives) : null;
            var fullOutput = output + "\nWORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: not-run\ncommit: none\nblockers: none\nmodel_fit: adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";
            Kernel.RecordDispatchExecutionResult(Goal.Id, Planner.Id, new TaskVerificationRecord(
                "plan", Root, 0, fullOutput, "", DateTimeOffset.UtcNow, WorkerResultPresent: true,
                HumanInputQuestion: directive?.Question, HumanInputKind: directive?.Kind,
                HumanInputEvidenceOwner: directive?.EvidenceOwner,
                DispatchAttemptAt: Planner.LastDispatch!.DispatchedAt,
                PlannerEvidenceBatch: legacy ? new(null, null) : parsed));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
