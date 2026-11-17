using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: git, result artifacts, prompts and kernel state use one isolated fixture root.
public sealed class GateTestSelectorDiagnosticContextTests
{
    private const string Prediction = "MergeTrainReproductionThirdMemberTests";
    private const string InvalidEvidence = "MergeTrainReproductionInvalidEvidenceTests.InvalidFirstMemberDoesNotDiscardAValidThirdMemberFailure";
    private const string Attribution = "MergeTrainReproductionAttributionTests.IsolatedReproductionRemovesAndImplicatesTheActualMember";

    [Theory]
    [InlineData(AgentRole.Tester, true)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Reviewer, false)]
    public void HistoricalMismatchReachesPreparedRoleContext(AgentRole role, bool typed)
    {
        using var f = new Fixture(role);
        var before = GateTestObligationEvaluator.Evaluate(f.Goal, f.OrchestratorDirectory, f.Candidate);
        var selectedBefore = f.Task.PreReviewEvidenceReceipt;
        var calls = 0;
        IReadOnlyList<string> diagnostic = [];
        var prepared = WorkerProfileDispatcher.PrepareTask(f.Kernel, f.Goal, f.Task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"), Path.Combine(f.Root, "prompts"), f.Root, DateTimeOffset.UtcNow,
            providerName: typed ? "OpenAI" : null, modelName: typed ? AgentCatalog.OpenAiSolSubscriptionModelAlias : null,
            reviewerScopeChangedFiles: [], reviewerScopeMergeBase: f.Candidate, reviewerScopeTotalChangedFileCount: 0,
            reviewerMergeTreeClean: true, reviewerMergeTreeConflictPaths: [], reviewerMergeTreeTotalConflictPathCount: 0,
            preparationFindingsResolver: (goal, candidate) =>
            {
                calls++;
                Assert.Equal(f.Candidate, candidate);
                diagnostic = GateTestSelectorDiagnosticPreparation.Build(goal, candidate, f.Root, f.OrchestratorDirectory, f.Kernel.HumanInputRequests);
                return diagnostic;
            });
        Assert.Equal(1, calls);
        Assert.Equal(WorkTaskStatus.Running, f.Task.Status);
        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.NotEmpty(diagnostic);
        var reportPath = Path.Combine(f.Root, ".orchestrator-context", f.Goal.Id.Value, "subscription-preflight.md");
        Assert.All(diagnostic, finding => Assert.Contains(finding, File.ReadAllText(reportPath)));
        Assert.Contains(diagnostic[0], prompt);
        Assert.Contains("declarations and Developer assertions do not prove execution", prompt);
        Assert.Contains("Full report: " + reportPath, prompt);
        foreach (var finding in diagnostic.Skip(1))
        {
            var entry = JsonSerializer.Deserialize<GateTestSelectorDiagnostic.Entry>(finding[(GateTestSelectorDiagnostic.Marker.Length + 1)..])!;
            Assert.Contains($"- criterion={entry.CriterionIndex?.ToString() ?? "?"} obligation={entry.ObligationId[..8]} selector={entry.EffectiveSelector} status={entry.Status}", prompt);
        }
        Assert.Contains(GateTestSelectorDiagnostic.Marker, prompt);
        Assert.Contains(Prediction, prompt);
        Assert.Contains(InvalidEvidence, prompt);
        Assert.Contains(Attribution, prompt);
        Assert.Contains("advisory identity projection", prompt);
        if (typed)
        {
            Assert.Contains("subscription-preflight.md", prompt);
            Assert.Contains(prepared.Task.LastDispatch!.ContextPackageReceipt!.Sections,
                section => section.LogicalIdentity == "context/subscription-preflight.md" &&
                    section.DeliveryMode == ContextDeliveryMode.InlineFull);
        }
        var contextRoot = Path.Combine(f.Root, ".orchestrator-context", f.Goal.Id.Value);
        var findings = File.ReadAllText(Path.Combine(contextRoot, "subscription-preflight.md"));
        Assert.Contains(Prediction, findings);
        Assert.Contains(InvalidEvidence, findings);
        Assert.Contains(Attribution, findings);
        Assert.Contains("\"Status\":\"Unresolved\"", findings);
        Assert.Contains("Selector naming diagnostic", File.ReadAllText(Path.Combine(contextRoot, "current-task.md")));
        Assert.Equal(new[] { Prediction }, Assert.Single(f.Goal.GateTestObligations).Selectors);
        Assert.Equal(before.Select(item => item.Outcome),
            GateTestObligationEvaluator.Evaluate(f.Goal, f.OrchestratorDirectory, f.Candidate).Select(item => item.Outcome));
        Assert.Same(selectedBefore, f.Task.PreReviewEvidenceReceipt);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Fact]
    public void ReadyBatchForwardsDiagnosticWithoutCreatingEvidenceSelection()
    {
        using var f = new Fixture(AgentRole.Tester);
        var calls = 0;
        var prepared = WorkerProfileDispatcher.PrepareReadyTasks(f.Kernel, f.Goal,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"), Path.Combine(f.Root, "prompts"), f.Root,
            DateTimeOffset.UtcNow, preparationFindingsResolver: (goal, candidate) =>
            {
                calls++;
                return GateTestSelectorDiagnosticPreparation.Build(goal, candidate, f.Root, f.OrchestratorDirectory, []);
            });
        Assert.Equal(1, calls);
        Assert.Contains(InvalidEvidence, File.ReadAllText(Assert.Single(prepared).PromptPath));
        Assert.Null(f.Task.PreReviewEvidenceReceipt);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Fact]
    public void PreparationRefreshDoesNotReusePreviousCandidateOrBrief()
    {
        using var f = new Fixture(AgentRole.Tester);
        var current = string.Join("\n", GateTestSelectorDiagnosticPreparation.Build(f.Goal, f.Candidate,
            f.Root, f.OrchestratorDirectory, []));
        Assert.Contains(InvalidEvidence, current);
        var changed = string.Join("\n", GateTestSelectorDiagnosticPreparation.Build(f.Goal, new string('b', 40),
            f.Root, f.OrchestratorDirectory, []));
        Assert.DoesNotContain(InvalidEvidence, changed);
        Assert.Contains("unknown", changed);
        Assert.Contains("\"Status\":\"Unknown\"", changed);
    }

    [Fact]
    public void Build_CommandOnlyManifest_ResolvesDeclaredCommandWithoutExecution()
    {
        using var f = new Fixture(AgentRole.Tester, "npx eslint .", commandOnly: true);
        var entry = BuildEntry(f);
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Resolved, entry.Status);
        Assert.Contains(entry.Evidence, text => text.Contains("declared manifest commands") && text.Contains("available/current"));
        Assert.DoesNotContain(entry.Evidence, text => text.Contains("executed receipt"));
        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending,
            Assert.Single(GateTestObligationEvaluator.Evaluate(f.Goal, f.OrchestratorDirectory, f.Candidate)).Outcome);
    }

    [Fact]
    public void Build_RevisedBriefWithUnchangedCriteria_ExcludesOldReportAssertions()
    {
        using var f = new Fixture(AgentRole.Tester);
        var before = BuildEntry(f);
        Assert.Contains(InvalidEvidence, before.DeveloperAssertions);
        var hash = EffectiveAcceptanceCriteriaVersion.ComputeForGoal(f.Goal);
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Revised diagnostic instructions with the same criteria");
        Assert.Equal(2, f.Goal.AuthoritativeBrief.Version);
        Assert.Equal(hash, EffectiveAcceptanceCriteriaVersion.ComputeForGoal(f.Goal));
        var kernel = GateTestObligationIntakeTests.Seed(f.Kernel, f.Goal, [Prediction]);
        var entry = BuildEntry(f, kernel.GetGoal(f.Goal.Id));
        Assert.Empty(entry.DeveloperAssertions);
        Assert.Empty(entry.Suggestions);
        Assert.Contains("dispatch_brief=1; current_brief=2", entry.DeveloperReportSource);
    }

    [Fact]
    public void Build_StaleEvaluatorReceiptAndUnrelatedCurrentDeclaration_RemainsUnknown()
    {
        using var f = new Fixture(AgentRole.Tester, csharpDeclarations: true);
        f.WriteReceipt(1, Prediction + ".Check");
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Revised diagnostic brief");
        var kernel = GateTestObligationIntakeTests.Seed(f.Kernel, f.Goal, [Prediction]);
        var goal = kernel.GetGoal(f.Goal.Id);
        var evaluated = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, f.OrchestratorDirectory, f.Candidate));
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, evaluated.Outcome);
        Assert.NotNull(evaluated.SourceId);
        var entry = BuildEntry(f, goal);
        Assert.Contains(entry.Evidence, text => text.Contains("declared CSharp classes") && text.Contains("available/current"));
        Assert.Contains(entry.Evidence, text => text.Contains("executed receipt") && text.Contains("stale or unavailable brief binding"));
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Unknown, entry.Status);
        Assert.DoesNotContain(entry.Evidence, text => text.StartsWith("existing evaluator source="));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_UntrackedQualifyingDeclaration_CannotResolveCommittedCandidate(bool ignored)
    {
        using var f = new Fixture(AgentRole.Tester, csharpDeclarations: true);
        var before = BuildEntry(f);
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Unknown, before.Status);
        Assert.Contains(before.Evidence, text => text.Contains("declared CSharp classes") && text.Contains("available/current"));
        if (ignored) File.WriteAllText(Path.Combine(f.Root, ".git", "info", "exclude"), "tests/UntrackedTests.cs\n");
        File.WriteAllText(Path.Combine(f.Root, "tests", "UntrackedTests.cs"),
            $"public class {Prediction} {{ [Fact] public void Check() {{ }} }}");
        if (ignored) Assert.Equal(0, GitCli.Run(f.Root, "check-ignore", "--quiet", "tests/UntrackedTests.cs").ExitCode);
        Assert.Equal(f.Candidate, GitCli.Run(f.Root, "rev-parse", "HEAD").Output.Trim());
        Assert.Empty(GitCli.Run(f.Root, "status", "--porcelain", "--untracked-files=no").Output.Trim());
        var entry = BuildEntry(f);
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Unknown, entry.Status);
        Assert.Contains(entry.Evidence, text => text.Contains("untracked candidate declaration inputs"));
        Assert.DoesNotContain(Prediction, entry.Suggestions);
        File.Delete(Path.Combine(f.Root, "tests", "UntrackedTests.cs"));
        // A declaration committed in the fixture does resolve through the same production boundary.
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Resolved, BuildEntry(f, selector: "OtherTests").Status);
    }

    [Fact]
    public void TesterPromptCarriesCompactSelectorTableForThreeDiagnosticRecords()
    {
        using var f = new Fixture(AgentRole.Tester);
        var advisory = GateTestSelectorDiagnostic.Render([BuildEntry(f)])[0];
        var records = Enumerable.Range(1, 3).Select(index => GateTestSelectorDiagnostic.Marker + " " +
            JsonSerializer.Serialize(new
            {
                ObligationId = new string((char)('0' + index), 8) + "-obligation",
                CriterionIndex = index,
                EffectiveSelector = $"Selector{index}.Check",
                Status = index == 1 ? "Resolved" : index == 2 ? "Unresolved" : "Unknown",
                DeveloperAssertions = new[] { $"Selector{index}.Check", $"Different{index}.Check" },
                Evidence = new[] { "evidence-sentinel" }, Suggestions = new[] { "suggestion-sentinel" },
                PlannerIdentity = "planner-sentinel", DeveloperReportSource = "report-source-sentinel"
            })).ToArray();
        string[] findings = [advisory, .. records, "ordinary-preflight-sentinel"];
        var rendered = PrepareSelectorPrompt(f, findings);

        for (var index = 1; index <= 3; index++)
        {
            var status = index == 1 ? "Resolved" : index == 2 ? "Unresolved" : "Unknown";
            var line = $"- criterion={index} obligation={new string((char)('0' + index), 8)} selector=Selector{index}.Check status={status} developer-asserted=Different{index}.Check";
            Assert.Contains(line, rendered.Prompt);
            Assert.Equal(1, rendered.Prompt.Split('\n').Count(text => text.TrimEnd('\r') == line));
            Assert.DoesNotContain($"developer-asserted=Selector{index}.Check", rendered.Prompt);
        }
        Assert.Contains(advisory, rendered.Prompt);
        Assert.Contains("declarations and Developer assertions do not prove execution", rendered.Prompt);
        Assert.Contains("Full report: " + rendered.ReportPath, rendered.Prompt);
        foreach (var field in new[] { "Evidence", "Suggestions", "PlannerIdentity", "DeveloperReportSource" })
            Assert.DoesNotContain($"\"{field}\":", rendered.Prompt);
        foreach (var sentinel in new[] { "evidence-sentinel", "suggestion-sentinel", "planner-sentinel", "report-source-sentinel", "ordinary-preflight-sentinel" })
            Assert.DoesNotContain(sentinel, rendered.Prompt);
        Assert.All(findings, finding => Assert.Contains(finding, File.ReadAllText(rendered.ReportPath)));
        Assert.Equal(ContextDeliveryMode.InlineFull, rendered.Section.DeliveryMode);
    }

    [Fact]
    public void StoredPreflightReportRendersSelectorSectionUnderFourKilobytes()
    {
        using var f = new Fixture(AgentRole.Tester);
        var fixturePath = Path.Combine(VerifiedRepositoryRoot.Find(), "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures", "SubscriptionPreflight", "tester-preflight-2026-11-17.md");
        Assert.True(new FileInfo(fixturePath).Length >= 20 * 1024);
        var findings = File.ReadAllLines(fixturePath).Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..]).ToArray();
        var records = findings.Where(line => line.StartsWith(GateTestSelectorDiagnostic.Marker + " ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(records);
        var rendered = PrepareSelectorPrompt(f, findings);

        Assert.Equal(ContextDeliveryMode.InlineFull, rendered.Section.DeliveryMode);
        Assert.InRange(rendered.Section.CharacterCount, 1, 4095);
        Assert.InRange(rendered.Section.ByteCount, 1, 4095);
        Assert.Contains("# Subscription Preflight selector summary", rendered.Prompt);
        Assert.Contains("Full report: " + rendered.ReportPath, rendered.Prompt);
        Assert.Contains("declarations and Developer assertions do not prove execution", rendered.Prompt);
        Assert.All(records, record => Assert.DoesNotContain(record, rendered.Prompt));
        Assert.All(findings, finding => Assert.Contains(finding, File.ReadAllText(rendered.ReportPath)));
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("[]")]
    public void UnparseableDiagnosticsRetainFullInlineReport(string payload)
    {
        using var f = new Fixture(AgentRole.Tester);
        var diagnostic = GateTestSelectorDiagnostic.Marker + " " + payload;
        var rendered = PrepareSelectorPrompt(f, [diagnostic]);
        Assert.Contains(diagnostic, rendered.Prompt);
        Assert.DoesNotContain("# Subscription Preflight selector summary", rendered.Prompt);
        Assert.Equal(ContextDeliveryMode.InlineFull, rendered.Section.DeliveryMode);
    }

    [Fact]
    public void PartialDiagnosticsShowUnknownFieldsAndUnparsedNotice()
    {
        using var f = new Fixture(AgentRole.Tester);
        var rendered = PrepareSelectorPrompt(f,
            [GateTestSelectorDiagnostic.Marker + " {malformed",
             GateTestSelectorDiagnostic.Marker + " " + """{"ObligationId":"short","CriterionIndex":null,"EffectiveSelector":null,"Status":null}"""]);
        Assert.Contains("- criterion=? obligation=short selector=unknown status=unknown", rendered.Prompt);
        Assert.Contains("1 unparsed records; read the full report", rendered.Prompt);
        Assert.Contains("Full report: " + rendered.ReportPath, rendered.Prompt);
        Assert.DoesNotContain("{malformed", rendered.Prompt);
    }

    [Fact]
    public void OversizedSelectorSummaryReportsOmittedRecords()
    {
        using var f = new Fixture(AgentRole.Tester);
        var records = Enumerable.Range(1, 40).Select(index => GateTestSelectorDiagnostic.Marker + " " +
            JsonSerializer.Serialize(new
            {
                ObligationId = $"{index:D8}", CriterionIndex = index,
                EffectiveSelector = new string('s', 100), Status = "Unknown",
                DeveloperAssertions = new[] { new string('a', 200), new string('b', 200), new string('c', 200), "fourth" }
            })).ToArray();
        var rendered = PrepareSelectorPrompt(f, [GateTestSelectorDiagnostic.Render([BuildEntry(f)])[0], .. records]);
        Assert.Contains("- criterion=1 obligation=00000001", rendered.Prompt);
        Assert.Contains("(+1 more)", rendered.Prompt);
        Assert.Contains("further records omitted; read the full report", rendered.Prompt);
        Assert.InRange(rendered.Section.ByteCount, 1, 4095);
        Assert.All(records, record => Assert.Contains(record, File.ReadAllText(rendered.ReportPath)));
    }

    private static (string Prompt, string ReportPath, WorkerContextSectionReceipt Section) PrepareSelectorPrompt(
        Fixture f, IReadOnlyList<string> findings)
    {
        var prepared = WorkerProfileDispatcher.PrepareTask(f.Kernel, f.Goal, f.Task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(f.Root, "prompts"), f.Root, DateTimeOffset.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias,
            preparationFindingsResolver: (_, _) => findings);
        return (File.ReadAllText(prepared.PromptPath),
            Path.Combine(f.Root, ".orchestrator-context", f.Goal.Id.Value, "subscription-preflight.md"),
            Assert.Single(f.Task.LastDispatch!.ContextPackageReceipt!.Sections,
                section => section.LogicalIdentity == "context/subscription-preflight.md"));
    }

    private static GateTestSelectorDiagnostic.Entry BuildEntry(Fixture f, Goal? goal = null, string? selector = null)
    {
        goal ??= f.Goal;
        if (selector is not null)
            goal = GateTestObligationIntakeTests.Seed(f.Kernel, goal, [selector]).GetGoal(goal.Id);
        var lines = GateTestSelectorDiagnosticPreparation.Build(goal, f.Candidate, f.Root, f.OrchestratorDirectory, []);
        Assert.Equal(2, lines.Count);
        return JsonSerializer.Deserialize<GateTestSelectorDiagnostic.Entry>(lines[1][(GateTestSelectorDiagnostic.Marker.Length + 1)..])!;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-selector-context", Guid.NewGuid().ToString("N"));
        internal string OrchestratorDirectory => Path.Combine(Root, ".orchestrator");
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal TaskSpec Task { get; }
        internal string Candidate { get; }

        internal Fixture(AgentRole role, string selector = Prediction, bool commandOnly = false, bool csharpDeclarations = false)
        {
            Directory.CreateDirectory(Root);
            Assert.Equal(0, GitCli.Run(Root, "init", "--initial-branch=main").ExitCode);
            File.WriteAllText(Path.Combine(Root, "fixture.txt"), "diagnostic context");
            File.WriteAllText(Path.Combine(Root, ".gitignore"), ".orchestrator/\n.orchestrator-context/\nprompts/\n");
            if (commandOnly || csharpDeclarations)
            {
                Directory.CreateDirectory(Path.Combine(Root, "config"));
                File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"), commandOnly
                    ? """{"checks":[{"name":"lint","type":"command","command":"npx","arguments":["eslint","."]}]}"""
                    : """{"checks":[{"name":"tests","type":"dotnet-test","project":"tests/Fixture.csproj"}]}""");
            }
            if (csharpDeclarations)
            {
                Directory.CreateDirectory(Path.Combine(Root, "tests"));
                File.WriteAllText(Path.Combine(Root, "tests", "Fixture.csproj"), "<Project />");
                File.WriteAllText(Path.Combine(Root, "tests", "OtherTests.cs"),
                    "public class OtherTests { [Fact] public void Check() { } }");
            }
            Assert.Equal(0, GitCli.Run(Root, "add", ".").ExitCode);
            Assert.Equal(0, GitCli.Run(Root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--quiet", "-m", "fixture").ExitCode);
            Candidate = GitCli.Run(Root, "rev-parse", "HEAD").Output.Trim();
            var kernel = new AgentOrchestratorKernel();
            var developer = new TaskSpec(TaskId.New(), "Implement the candidate", AgentRole.Developer);
            var target = new TaskSpec(TaskId.New(), "Inspect candidate evidence", role);
            var goal = kernel.CreateGoal("Surface predicted selector mismatch", [developer, target]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var dispatchedAt = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, developer.Id,
                new TaskDispatchRecord("fixture", "implement", Root, dispatchedAt, BaseCommit: Candidate));
            kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord("fixture", Root, 0,
                $"WORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: deferred - {InvalidEvidence}, {Attribution}\n" +
                $"commit: {Candidate}\nblockers: none\nassigned_scope_complete: true\nmodel_fit: adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT", "", DateTimeOffset.UtcNow,
                DispatchStartedAt: dispatchedAt));
            Assert.Equal(WorkTaskStatus.Completed, developer.Status);
            Kernel = GateTestObligationIntakeTests.Seed(kernel, goal, [selector]);
            Goal = Kernel.GetGoal(goal.Id);
            Task = Goal.Tasks.Single(task => task.RequiredRole == role);
            if (!commandOnly && !csharpDeclarations) WriteReceipt(Goal.AuthoritativeBrief.Version, InvalidEvidence, Attribution);
        }

        internal void WriteReceipt(int briefVersion, params string[] identities)
        {
            var directory = Path.Combine(OrchestratorDirectory, "acceptance-gate-attempts", Goal.Id.Value);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "identities.trx"),
                "<TestRun><Results>" + string.Concat(identities.Select(identity =>
                    $"<UnitTestResult testName=\"{identity}\" outcome=\"Passed\"/>")) + "</Results></TestRun>");
            File.WriteAllText(Path.Combine(directory, "fixture.attempt.json"), JsonSerializer.Serialize(new
            {
                attemptId = "fixture", goalId = Goal.Id.Value, branchHeadSha = Candidate,
                kind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
                outcome = ConductorParallelAcceptanceAttemptOutcome.Passed, completedAt = DateTimeOffset.UtcNow,
                briefVersion, testResultPaths = new[] { "identities.trx" }
            }));
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
