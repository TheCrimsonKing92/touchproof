using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned databases and result files; AsyncLocal console capture.
public sealed class ObligationRebindExactSelectorTests
{
    [Fact]
    public async Task VitestDrop_LeavesPassingSpacedSelector_AndPersistsOperatorDecision()
    {
        using var f = new Fixture(["applyIncomingDamage layer order", "engine"]);
        f.WriteCtrf(("applyIncomingDamage layer order", "passed"));
        var baseline = Assert.Single(f.Evaluate());
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, baseline.Outcome);
        Assert.Equal(new[] { "engine" }, baseline.Selectors);

        await f.Apply(await f.Submit("--selector", "engine", "--drop"));

        var request = Assert.Single(f.Kernel.HumanInputRequests);
        Assert.Equal("selector \"engine\" dropped in [\"obligation-gate-test\"]", request.AuthoritativeAnswer!.Text);
        Assert.Equal(HumanInputAnswerOrigin.Operator, request.AuthoritativeAnswer.Origin);
        Assert.Contains("reason from operator", request.Question);
        Assert.Contains(f.Goal.Timeline, entry => entry.Kind == ProgressKind.HumanInputReceived &&
            entry.Message == request.AuthoritativeAnswer.Text);
        Assert.True(request.IsCompleted);
        Assert.Empty(f.Kernel.GetPendingHumanInput(f.Goal.Id));
        var result = Assert.Single(f.Evaluate());
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(new[] { "applyIncomingDamage layer order" }, result.Obligation.Selectors);
        var restored = await SqliteOrchestratorStateRepository.OpenReadOnly(f.Workspace.SqliteStatePath).LoadAsync();
        Assert.Equal(request.AuthoritativeAnswer.Text, Assert.Single(restored.HumanInputRequests).AuthoritativeAnswer!.Text);
    }

    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData(null)]
    public async Task SpacedRebind_RequiresReplacementEvidence(string? status)
    {
        using var f = new Fixture(["battle engine"]);
        f.WriteCtrf(("unrelated", "passed"));
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, Assert.Single(f.Evaluate()).Outcome);
        await f.Apply(await f.Submit("--selector", "battle engine", "--to", "battle engine extended"));
        // Later evidence for the old selector cannot satisfy the edited obligation.
        f.WriteCtrf(status is null ? ("battle engine", "passed") : ("battle engine extended", status));
        var result = Assert.Single(f.Evaluate());
        Assert.Equal(status switch
        {
            "passed" => GateTestObligationEvaluator.Outcome.Satisfied,
            "failed" => GateTestObligationEvaluator.Outcome.Failed,
            _ => GateTestObligationEvaluator.Outcome.SelectorNotFound
        }, result.Outcome);
        Assert.Equal(new[] { "battle engine extended" }, result.Obligation.Selectors);
        Assert.Equal(new[] { "battle engine extended" }, result.Selectors);
    }

    [Fact]
    public async Task LastSelector_RejectsWithoutDecision()
    {
        using var f = new Fixture(["engine"]);
        await f.Reject(await f.Submit("--selector", "engine", "--drop"), "last-selector");
        Assert.Equal(new[] { "engine" }, Assert.Single(f.Evaluate()).Obligation.Selectors);
    }

    [Theory]
    [InlineData("engine", "engin")]
    [InlineData("engine", "Engine")]
    [InlineData("engine", "engine ")]
    [InlineData("Suite.engine", "engine")]
    public async Task WholeSelectorOrdinalMatching_RejectsUnknownText(string stored, string supplied)
    {
        using var f = new Fixture([stored]);
        await f.Reject(await f.Submit("--selector", supplied, "--drop"), "no-matching-obligation");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoObligations_DropIsAtomic(bool wouldEmpty)
    {
        using var f = new Fixture(["applyIncomingDamage layer order", "engine"],
            wouldEmpty ? ["engine"] : ["engine", "battle engine"]);
        f.WriteCtrf(("applyIncomingDamage layer order", "passed"), ("battle engine", "passed"));
        var before = f.Evaluate().Select(result => result.Obligation.Selectors.ToArray()).ToArray();
        Assert.All(f.Evaluate(), result => Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome));
        var intent = await f.Submit("--selector", "engine", "--drop");
        if (wouldEmpty)
        {
            await f.Reject(intent, "last-selector");
            var after = f.Evaluate();
            Assert.Equal(before[0], after[0].Obligation.Selectors);
            Assert.Equal(before[1], after[1].Obligation.Selectors);
        }
        else
        {
            await f.Apply(intent);
            var results = f.Evaluate();
            Assert.All(results, result => Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome));
            Assert.Equal(new[] { "applyIncomingDamage layer order" }, results[0].Obligation.Selectors);
            Assert.Equal(new[] { "battle engine" }, results[1].Obligation.Selectors);
            var edit = AnsweredSelectorRebinding.ParseSelectorEdit(Assert.Single(f.Kernel.HumanInputRequests).AuthoritativeAnswer!.Text);
            Assert.NotNull(edit);
            Assert.Equal(results.Select(result => result.Obligation.Id), edit.ObligationIds);
        }
    }

    [Fact]
    public async Task ClassForm_KeepsLegacyAnswerAndProvenance()
    {
        using var f = new Fixture(["Fixture.OldTests.Check"]);
        f.Evidence.WriteAttempt([("Fixture.NewTests.Check", "Passed")]);
        await f.Apply(await f.Submit("OldTests", "NewTests"));
        var request = Assert.Single(f.Kernel.HumanInputRequests);
        Assert.Equal("OldTests becomes NewTests", request.AuthoritativeAnswer!.Text);
        Assert.Null(AnsweredSelectorRebinding.ParseSelectorEdit(request.AuthoritativeAnswer.Text));
        var result = Assert.Single(f.Evaluate());
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(request.Id.Value, Assert.Single(result.AnswerRebinds!).RequestId);
        Assert.Contains("old=OldTests new=NewTests", result.AnswerRebindProvenance);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("new \"name\" \\ café > Foo becomes Bar")]
    public async Task EscapedPayload_RoundTripsWithoutLegacyMappings(string? replacement)
    {
        const string selector = "old \"name\" \\ café > Foo becomes Bar";
        using var f = new Fixture([selector, "retained"]);
        await f.Apply(await f.Submit(replacement is null ? ["--selector", selector, "--drop"] :
            ["--selector", selector, "--to", replacement]));
        var answer = Assert.Single(f.Kernel.HumanInputRequests).AuthoritativeAnswer!.Text;
        Assert.Empty(AnsweredSelectorRebinding.Parse(answer));
        var edit = AnsweredSelectorRebinding.ParseSelectorEdit(answer);
        Assert.NotNull(edit);
        Assert.Equal(selector, edit.Selector);
        Assert.Equal(replacement, edit.Replacement);
        Assert.Equal(replacement is null ? new[] { "retained" } : [replacement, "retained"],
            Assert.Single(f.Evaluate()).Obligation.Selectors);
    }

    [Fact]
    public async Task ConsecutiveDecisions_TargetEffectiveSelectors_AndChangeOneOccurrence()
    {
        using var f = new Fixture(["engine", "engine", "retained"]);
        await f.Apply(await f.Submit("--selector", "engine", "--to", "battle engine"));
        Assert.Equal(new[] { "battle engine", "engine", "retained" }, Assert.Single(f.Evaluate()).Obligation.Selectors);
        await f.Apply(await f.Submit("--selector", "battle engine", "--drop"));
        Assert.Equal(new[] { "engine", "retained" }, Assert.Single(f.Evaluate()).Obligation.Selectors);
    }

    [Fact]
    public async Task SatisfiedObligation_IsNeverTargetedAlongsideUnsatisfiedMatch()
    {
        using var f = new Fixture(["engine"], ["engine", "missing"]);
        f.WriteCtrf(("engine", "passed"));
        await f.Apply(await f.Submit("--selector", "engine", "--drop"));
        var results = f.Evaluate();
        Assert.Equal(new[] { "engine" }, results[0].Obligation.Selectors);
        Assert.Equal(new[] { "missing" }, results[1].Obligation.Selectors);
        var answer = Assert.Single(f.Kernel.HumanInputRequests).AuthoritativeAnswer!.Text;
        Assert.Equal(new[] { results[1].Obligation.Id }, AnsweredSelectorRebinding.ParseSelectorEdit(answer)!.ObligationIds);
        await f.Reject(await f.Submit("--selector", "engine", "--drop"), "no-matching-obligation", expectedAnswers: 1);
    }

    [Fact]
    public async Task Revision_ExcludesOldDecision_AndReplayCannotEmptyObligation()
    {
        using var f = new Fixture(["engine", "retained"]);
        await f.Apply(await f.Submit("--selector", "engine", "--drop"));
        // A stale/manual answer still cannot remove the final requirement on replay.
        f.Kernel.RecordAnsweredOperatorHumanInput(f.Goal.Id, "operator correction",
            AnsweredSelectorRebinding.FormatSelectorEdit("retained", null, ["obligation-gate-test"]));
        Assert.Equal(new[] { "retained" }, Assert.Single(f.Evaluate()).Obligation.Selectors);
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "version two");
        f.Reseed(["engine", "retained"]);
        Assert.Equal(new[] { "engine", "retained" }, Assert.Single(f.Evaluate()).Obligation.Selectors);
    }

    [Theory]
    [InlineData("--selector", "engine", "--drop", "--to", "other")]
    [InlineData("--selector", "engine")]
    [InlineData("--drop")]
    [InlineData("--selector", " ", "--drop")]
    [InlineData("--selector", "engine", "--to", " ")]
    [InlineData("--selector", "engine", "--to")]
    [InlineData("--selector", "engine", "--drop", "--drop")]
    [InlineData("--selector", "engine", "--drop", "OldTests", "NewTests")]
    public async Task InvalidCliPayload_RejectsWithoutEnqueue(params string[] options)
    {
        using var f = new Fixture(["engine", "retained"]);
        var exit = -1;
        var error = InfrastructureTestSupport.CaptureConsoleError(() =>
            InfrastructureTestSupport.CaptureConsole(() => exit = f.Run(options)));
        Assert.Equal(1, exit);
        Assert.Contains("invalid-payload", error);
        Assert.Empty(await f.Store.ListForGoalAsync(f.Goal.Id.Value));
    }

    [Theory]
    [InlineData("{\"selector\":\"engine\",\"to\":\"new\",\"drop\":true,\"reason\":\"why\"}")]
    [InlineData("{\"selector\":\"engine\",\"reason\":\"why\"}")]
    [InlineData("{\"drop\":true,\"reason\":\"why\"}")]
    [InlineData("{\"selector\":\"engine\",\"drop\":true,\"reason\":\" \"}")]
    [InlineData("{\"selector\":\"engine\",\"drop\":true,\"reason\":\"why\",\"oldClass\":\"OldTests\"}")]
    [InlineData("{\"selector\":\"engine\",\"selector\":\"other\",\"drop\":true,\"reason\":\"why\"}")]
    [InlineData("{\"selector\":")]
    public async Task InvalidQueuedPayload_RejectsWithoutDecision(string json)
    {
        using var f = new Fixture(["engine", "retained"]);
        await f.Reject(await f.Queue(json), "invalid-payload");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("unreadable")]
    public async Task InvalidReasonFile_RejectsWithoutEnqueue(string mode)
    {
        using var f = new Fixture(["engine", "retained"]);
        if (mode == "missing") File.Delete(f.ReasonPath);
        if (mode == "empty") File.WriteAllText(f.ReasonPath, " \r\n");
        using var locked = mode == "unreadable"
            ? File.Open(f.ReasonPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var exit = -1;
        var error = InfrastructureTestSupport.CaptureConsoleError(() =>
            InfrastructureTestSupport.CaptureConsole(() => exit = f.Run("--selector", "engine", "--drop")));
        Assert.Equal(1, exit);
        Assert.Contains("invalid-payload", error);
        Assert.Empty(await f.Store.ListForGoalAsync(f.Goal.Id.Value));
    }

    private sealed class Fixture : IDisposable
    {
        internal ProspectiveEvidenceGateFixture Evidence { get; } = new();
        internal OrchestratorWorkspace Workspace { get; }
        internal AgentOrchestratorKernel Kernel { get; private set; }
        internal Goal Goal => Kernel.GetGoal(Evidence.Goal.Id);
        internal SqliteOperatorIntentStore Store { get; }
        internal OperatorIntentCoordinator Coordinator { get; }
        internal string ReasonPath => Path.Combine(Evidence.Root, "reason.txt");

        internal Fixture(string[] selectors, string[]? second = null)
        {
            Workspace = OrchestratorWorkspace.ForDirectory(Evidence.Root);
            Kernel = GateTestObligationIntakeTests.Seed(Evidence.Kernel, Evidence.Goal, selectors);
            if (second is not null)
            {
                var snapshot = Kernel.ExportSnapshot();
                var first = Assert.Single(Goal.GateTestObligations);
                Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
                {
                    Goals = snapshot.Goals.Select(goal => goal with
                    {
                        GateTestObligations = [first, first with { Id = "second-obligation", Selectors = second }]
                    }).ToArray()
                });
            }
            InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
            _ = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            Coordinator = new(Store, goalHeadResolver: _ => Evidence.CandidateSha,
                orchestratorDirectory: Evidence.OrchestratorDirectory);
            File.WriteAllText(ReasonPath, "reason from operator");
        }

        internal void Reseed(string[] selectors) => Kernel = GateTestObligationIntakeTests.Seed(Kernel, Goal, selectors);
        internal IReadOnlyList<GateTestObligationEvaluator.Result> Evaluate() => GateTestObligationEvaluator.Evaluate(
            Goal, Evidence.OrchestratorDirectory, Evidence.CandidateSha, answeredRequests: Kernel.HumanInputRequests);
        internal int Run(params string[] options) => CliObligationRebindCommand.Run(
            ["obligation-rebind", Goal.Id.Value[..8], .. options, "--text-file", ReasonPath], Workspace);
        internal async Task<OperatorIntentRecord> Submit(params string[] options)
        {
            InfrastructureTestSupport.CaptureConsole(() => Assert.Equal(0, Run(options)));
            return (await Store.ListForGoalAsync(Goal.Id.Value)).Single(intent => intent.Status == OperatorIntentStatus.Pending);
        }
        internal Task<OperatorIntentRecord> Queue(string json)
        {
            var id = Guid.NewGuid().ToString("N");
            return Store.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.ObligationRebind,
                Goal.Id.Value, null, json, [], "operator", "cli", "local", DateTimeOffset.UtcNow));
        }
        internal async Task Apply(OperatorIntentRecord intent)
        {
            var result = Coordinator.ExecutePending(Kernel, Goal);
            Assert.True(result.MutatedGoalState, string.Join(Environment.NewLine, result.ProgressLines));
            await new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).SaveAsync(Kernel);
            Coordinator.CompletePersisted([Goal.Id]);
            Assert.Equal(OperatorIntentStatus.Applied, (await Store.GetAsync(intent.Id))!.Status);
        }
        internal async Task Reject(OperatorIntentRecord intent, string reason, int expectedAnswers = 0)
        {
            var result = Coordinator.ExecutePending(Kernel, Goal);
            Assert.Contains(result.ProgressLines, line => line.Contains($"reason={reason}", StringComparison.Ordinal));
            Assert.Equal(OperatorIntentStatus.Rejected, (await Store.GetAsync(intent.Id))!.Status);
            Assert.Equal(expectedAnswers, Kernel.HumanInputRequests.Count);
        }
        internal void WriteCtrf(params (string Name, string Status)[] rows)
        {
            var directory = Path.Combine(Evidence.OrchestratorDirectory, "acceptance-gate-attempts", Goal.Id.Value);
            Directory.CreateDirectory(directory);
            var id = Guid.NewGuid().ToString("N");
            var path = Path.Combine(directory, id + ".ctrf.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                results = new { tests = rows.Select(row => new
                {
                    testId = "src/game/engine.test.ts::" + row.Name, name = row.Name, status = row.Status
                }) }
            }));
            File.WriteAllText(Path.Combine(directory, id + ".attempt.json"), JsonSerializer.Serialize(new
            {
                attemptId = id, goalId = Goal.Id.Value, kind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
                branchHeadSha = Evidence.CandidateSha, outcome = ConductorParallelAcceptanceAttemptOutcome.Passed,
                completedAt = DateTimeOffset.UtcNow, testResultPaths = new[] { Path.GetFileName(path) }
            }));
        }
        public void Dispose() => Evidence.Dispose();
    }
}
