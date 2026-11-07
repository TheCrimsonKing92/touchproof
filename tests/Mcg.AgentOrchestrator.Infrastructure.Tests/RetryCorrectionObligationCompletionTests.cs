using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static DeveloperResumedDeferredNoChangeQualifierTests;

// Parallel-safe: every filesystem and SQLite resource is under a unique fixture root.
public sealed class RetryCorrectionObligationCompletionTests
{
    private static readonly string OldFindingOutput = Output.Replace(Rationale,
        "NO_CHANGE: the old rename finding was repaired at the earlier commit.", StringComparison.Ordinal);

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void SizeRatchet_NoChangeRationale_RequiresPassingCandidate(bool passes, bool oldFinding)
    {
        var root = TempRoot();
        try
        {
            WriteRatchet(root, passes);
            var (_, goal, task) = Scenario(RetryCorrectionObligation.ForSourceSizeRatchet(Candidate));
            var calls = 0;
            var runner = new BackgroundDispatchRunner
            {
                RetryCorrectionConditionOracle = path =>
                {
                    Xunit.Assert.Equal(root, path);
                    calls++;
                    return SourceSizeRatchetObligationOracle.Evaluate(path);
                }
            };
            var output = oldFinding ? OldFindingOutput : Output;
            Xunit.Assert.True(WorkerResultParser.TryParseResult(output, out _, out _));
            var (_, baselineGoal, baselineTask) = Scenario(null);
            Xunit.Assert.True(Qualify(baselineGoal, baselineTask, output, string.Empty, out _));
            string? diagnostic = null;
            var accepted = runner.AllowsNoChangeCompletion(
                goal, task, Evidence(), output, output, string.Empty, root, ref diagnostic);

            Xunit.Assert.Equal(passes, accepted);
            Xunit.Assert.Equal(1, calls);
            Xunit.Assert.Contains(passes ? "outcome=passed" : "outcome=failed", diagnostic);
            if (!passes) Xunit.Assert.Contains("reason=structural-obligation-unsatisfied", diagnostic);
            Xunit.Assert.Equal(passes, Qualify(goal, task, output, diagnostic!, out var decline));
            Xunit.Assert.Equal(passes ? null : RetryCorrectionObligationEvidence.DeclineCode, decline);
            if (passes)
                Xunit.Assert.Contains("DEFERRED_NO_CHANGE", diagnostic);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("absent")]
    [Xunit.InlineData("mismatch")]
    [Xunit.InlineData("semantic")]
    public void InapplicableObligation_UsesBaselineWithoutOracle(string scenario)
    {
        var (_, baselineGoal, baselineTask) = Scenario(null);
        var (_, goal, task) = Scenario(Obligation(scenario));
        var calls = 0;
        var runner = new BackgroundDispatchRunner
        {
            RetryCorrectionConditionOracle = _ => { calls++; return RetryCorrectionConditionVerdict.Failed; }
        };
        string? diagnostic = null;
        var accepted = runner.AllowsNoChangeCompletion(
            goal, task, Evidence(), Output, Output, string.Empty, "unused", ref diagnostic);

        Xunit.Assert.True(Qualify(baselineGoal, baselineTask, Output, string.Empty, out _));
        Xunit.Assert.True(accepted);
        Xunit.Assert.Equal(Qualify(baselineGoal, baselineTask, Output, string.Empty, out _),
            Qualify(goal, task, Output, string.Empty, out _));
        Xunit.Assert.Equal(0, calls);
        Xunit.Assert.DoesNotContain(RetryCorrectionObligationEvidence.Marker, diagnostic ?? string.Empty);
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing-oracle")]
    [Xunit.InlineData("missing-authority")]
    [Xunit.InlineData("exception")]
    public void EvaluationUnavailable_DeclinesInsteadOfAssumingPass(string scenario)
    {
        var root = TempRoot();
        try
        {
            var (_, goal, task) = Scenario(RetryCorrectionObligation.ForSourceSizeRatchet(Candidate));
            var runner = new BackgroundDispatchRunner
            {
                RetryCorrectionConditionOracle = scenario switch
                {
                    "missing-oracle" => null,
                    "exception" => _ => throw new IOException("measurement unreadable"),
                    _ => SourceSizeRatchetObligationOracle.Evaluate
                }
            };
            // Even a worker-supplied passing marker must lose to the runner's newest verdict.
            var forged = RetryCorrectionObligationEvidence.Format(task.AcceptedRetryFeedback!.Obligation!,
                RetryCorrectionConditionVerdict.Passed);
            string? diagnostic = forged;
            Xunit.Assert.False(runner.AllowsNoChangeCompletion(
                goal, task, Evidence(), Output, Output, forged, root, ref diagnostic));
            Xunit.Assert.Contains("outcome=unavailable", diagnostic);
            Xunit.Assert.Contains("reason=structural-obligation-unsatisfied", diagnostic);
            Xunit.Assert.False(RetryCorrectionObligationEvidence.IsSatisfied(task, diagnostic));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("matching")]
    [Xunit.InlineData("mismatch")]
    [Xunit.InlineData("legacy")]
    [Xunit.InlineData("semantic")]
    public async Task SqliteReload_PreservesApplicabilityIncludingLegacyJson(string scenario)
    {
        var root = TempRoot();
        try
        {
            var (kernel, goal, task) = Scenario(scenario == "matching" || scenario == "legacy"
                ? RetryCorrectionObligation.ForSourceSizeRatchet(Candidate) : Obligation(scenario));
            if (scenario == "legacy")
            {
                var json = JsonNode.Parse(JsonSerializer.Serialize(kernel.ExportSnapshot()))!;
                var accepted = json["Goals"]![0]!["Tasks"]![0]!["AcceptedRetryFeedback"]!.AsObject();
                Xunit.Assert.True(accepted.Remove("Obligation"));
                kernel = AgentOrchestratorKernel.FromSnapshot(
                    JsonSerializer.Deserialize<OrchestratorSnapshot>(json.ToJsonString())!);
            }
            var databasePath = Path.Combine(root, "state.db");
            _ = StateDbMigrations.EnsureUpToDate(databasePath);
            var repository = new SqliteOrchestratorStateRepository(databasePath);
            await repository.SaveAsync(kernel);
            var loaded = await new SqliteOrchestratorStateRepository(databasePath).LoadAsync();
            var restored = loaded.GetTask(goal.Id, task.Id);
            Xunit.Assert.NotNull(restored.AcceptedRetryFeedback);
            Xunit.Assert.Equal(scenario != "legacy", restored.AcceptedRetryFeedback.Obligation is not null);
            if (scenario != "legacy")
                Xunit.Assert.Equal(task.AcceptedRetryFeedback!.Obligation, restored.AcceptedRetryFeedback.Obligation);
            var calls = 0;
            var runner = new BackgroundDispatchRunner
            {
                RetryCorrectionConditionOracle = _ => { calls++; return RetryCorrectionConditionVerdict.Failed; }
            };
            string? diagnostic = null;
            Xunit.Assert.Equal(scenario != "matching", runner.AllowsNoChangeCompletion(
                loaded.GetGoal(goal.Id), restored, Evidence(), Output, Output, string.Empty, root, ref diagnostic));
            Xunit.Assert.Equal(scenario == "matching" ? 1 : 0, calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void LaterAcceptedFeedback_DoesNotInheritAnOlderObligation()
    {
        var (kernel, goal, task) = Scenario(RetryCorrectionObligation.ForSourceSizeRatchet(Candidate));
        var dispatch = task.LastDispatch!;
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
            new TaskProcessRecord(123, dispatch.Command, dispatch.WorkingDirectory, "out", "err", "exit",
                dispatch.DispatchedAt, dispatch.DispatchedAt, 0), null);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Correction round finished.");
        Xunit.Assert.NotEqual(WorkTaskStatus.Running, task.Status);
        kernel.RetryTaskAutomatically(goal.Id, task.Id, "Later accepted review correction", RetryCause.NewSourceFinding,
            authoritativeRetryFeedback: true);
        Xunit.Assert.NotNull(task.AcceptedRetryFeedback);
        Xunit.Assert.Null(task.AcceptedRetryFeedback.Obligation);
    }

    private static RetryCorrectionObligation? Obligation(string scenario) => scenario switch
    {
        "mismatch" => RetryCorrectionObligation.ForSourceSizeRatchet(new string('c', 40)),
        "semantic" => new(RetryCorrectionObligationKind.Semantic, Candidate, null, string.Empty),
        _ => null
    };

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) Scenario(RetryCorrectionObligation? obligation)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Apply the current correction", AgentRole.Developer);
        var goal = kernel.CreateGoal("Candidate-bound correction", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTaskAutomatically(goal.Id, task.Id, "alpha.cs exceeds its ceiling", RetryCause.NewSourceFinding,
            authoritativeRetryFeedback: true, obligation: obligation);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "unused", DateTimeOffset.Parse("2026-11-07T10:00:00Z"), BaseCommit: Candidate));
        return (kernel, goal, task);
    }

    private static GoalWorktreeDispatchEvidence Evidence() => new("goal/test", Candidate, "clean", true, "clean", "", 0, [], []);

    private static bool Qualify(Goal goal, TaskSpec task, string output, string diagnostic, out string? code)
    {
        var classifier = new WorkerDispatchCompletionClassifier(
            dispatch => WorkerProviderCatalog.Default().ResolveProfile(dispatch.WorkerName), new FixedClock(),
            _ => false, _ => throw new FileNotFoundException());
        return DeveloperDeferredNoChangeQualifier.TryQualify(goal, task, Candidate, true, false,
            output, diagnostic, classifier, out _, out code);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-obligation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteRatchet(string root, bool passes)
    {
        var authority = Path.Combine(root, SourceSizeRatchet.SourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(authority)!);
        File.WriteAllText(authority, "new SourceSizeCeiling(\"alpha.cs\", 2)");
        File.WriteAllLines(Path.Combine(root, "alpha.cs"), Enumerable.Repeat("line", passes ? 2 : 3));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-11-07T10:01:00Z");
    }
}
