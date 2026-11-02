using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned state, intent databases and TRX files; AsyncLocal console capture.
public sealed class ObligationRebindVerbTests
{
    [Theory]
    [InlineData("OldTests", true)]
    [InlineData("Fixture.OldTests.Check", true)]
    [InlineData("OldTests", false)]
    public async Task Tick_Rebind_RequiresPassingReplacementEvidence(string selector, bool passing)
    {
        using var f = new Fixture(selector);
        var row = selector.StartsWith("Fixture.", StringComparison.Ordinal) ? "Fixture.NewTests.Check" : "NewTests.Check";
        var (source, _, _) = f.Evidence.WriteAttempt([(passing ? row : "UnrelatedTests.Check", "Passed")]);
        var baseline = f.Evaluate();
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, baseline.Outcome);

        var intent = await f.Submit();
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Empty(f.Kernel.HumanInputRequests);
        await f.Apply(intent);

        var request = Assert.Single(f.Kernel.HumanInputRequests);
        Assert.Equal("OldTests becomes NewTests", request.AuthoritativeAnswer!.Text);
        Assert.Contains("reason from operator", request.Question);
        Assert.Equal(HumanInputAnswerOrigin.Operator, request.AuthoritativeAnswer.Origin);
        var result = f.Evaluate();
        if (passing)
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
            Assert.Equal(source, result.SourceId);
            var rebind = Assert.Single(result.AnswerRebinds!);
            Assert.Equal(request.Id.Value, rebind.RequestId);
            Assert.Equal(request.AuthoritativeAnswer.Id, rebind.AnswerId);
            Assert.Contains($"old=OldTests new=NewTests request={request.Id.Value[..8]}", result.AnswerRebindProvenance);
        }
        else
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
            Assert.Empty(result.AnswerRebinds ?? []);
            Assert.Equal("", result.AnswerRebindProvenance);
        }
    }

    [Theory]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.Verifying)]
    [InlineData(GoalStatus.AcceptanceFailed)]
    public async Task Tick_RecordNeverBlocksOrReopens_AndAttentionShowsNoOpenWait(GoalStatus status)
    {
        using var f = new Fixture(status: status);
        var before = f.Goal.Tasks.Select(task => task.Status).ToArray();
        var intent = await f.Submit();
        await f.Apply(intent);
        Assert.Equal(status, f.Goal.Status);
        Assert.Equal(before, f.Goal.Tasks.Select(task => task.Status));
        Assert.Empty(f.Kernel.GetPendingHumanInput(f.Goal.Id));
        var record = Assert.Single(f.Kernel.HumanInputRequests);
        Assert.True(record.IsCompleted);
        Assert.False(record.IsDismissible);
        Assert.False(record.WasDismissed);
        Assert.Null(record.TaskId);

        await f.Save();
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(f.Workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? selected = null;
        var output = InfrastructureTestSupport.CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(["attention", "show", f.Goal.Id.Value[..8]],
                repository, f.Workspace, new InMemoryModelProviderRegistry([]), null,
                ref agents, ref profiles, ref selected, out var changed));
            Assert.False(changed);
        });
        Assert.Contains("Human waits: 0 active wait(s)", output);
        Assert.DoesNotContain(record.Id.Value[..8], output);
        var restored = await repository.LoadAsync();
        Assert.True(Assert.Single(restored.HumanInputRequests).IsCompleted);
        Assert.Equal("OldTests becomes NewTests", Assert.Single(restored.HumanInputRequests).AuthoritativeAnswer!.Text);
    }

    [Theory]
    [InlineData("MissingTests")]
    [InlineData("OldTestsSuffix")]
    [InlineData("oldTests")]
    public async Task Tick_NoExactRecordedSelectorMatch_Rejects(string selector)
    {
        using var f = new Fixture(selector);
        var intent = await f.Submit();
        var result = f.Coordinator.ExecutePending(f.Kernel, f.Goal);
        Assert.Contains(result.ProgressLines, line => line.Contains("reason=no-matching-obligation", StringComparison.Ordinal));
        Assert.Equal(OperatorIntentStatus.Rejected, (await f.Store.GetAsync(intent.Id))!.Status);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Fact]
    public async Task Tick_AlreadySatisfiedObligation_Rejects()
    {
        using var f = new Fixture();
        f.Evidence.WriteAttempt([("OldTests.Check", "Passed")]);
        var intent = await f.Submit();
        var result = f.Coordinator.ExecutePending(f.Kernel, f.Goal);
        Assert.Contains(result.ProgressLines, line => line.Contains("reason=no-matching-obligation", StringComparison.Ordinal));
        Assert.Equal(OperatorIntentStatus.Rejected, (await f.Store.GetAsync(intent.Id))!.Status);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Theory]
    [InlineData("oldTests", "NewTests")]
    [InlineData("OldTests", "newTests")]
    [InlineData("Fixture.OldTests", "NewTests")]
    [InlineData("OldTests", "Fixture.NewTests")]
    [InlineData("OldTests", "OldTests")]
    [InlineData("\u00C4OldTests", "NewTests")]
    [InlineData("OldTests", "\u00C4NewTests")]
    [InlineData("@OldTests", "NewTests")]
    [InlineData("OldTests ", "NewTests")]
    [InlineData("\\u004FldTests", "NewTests")]
    public async Task InvalidClassNames_RejectAtSubmissionAndTick(string oldClass, string newClass)
    {
        using var f = new Fixture();
        var exit = -1;
        var stderr = InfrastructureTestSupport.CaptureConsoleError(() =>
            InfrastructureTestSupport.CaptureConsole(() => exit = f.Run(oldClass, newClass)));
        Assert.Equal(1, exit);
        Assert.Contains("invalid-class-name", stderr);
        Assert.Empty(await f.Store.ListForGoalAsync(f.Goal.Id.Value));

        var intent = await f.Queue(new(oldClass, newClass, "operator reason"));
        var result = f.Coordinator.ExecutePending(f.Kernel, f.Goal);
        Assert.Contains(result.ProgressLines, line => line.Contains("reason=invalid-class-name", StringComparison.Ordinal));
        Assert.Equal(OperatorIntentStatus.Rejected, (await f.Store.GetAsync(intent.Id))!.Status);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWhitespaceReason_RejectsWithoutEnqueue(bool missing)
    {
        using var f = new Fixture();
        if (missing) File.Delete(f.ReasonPath);
        else File.WriteAllText(f.ReasonPath, " \r\n ");
        var exit = -1;
        var stderr = InfrastructureTestSupport.CaptureConsoleError(() =>
            InfrastructureTestSupport.CaptureConsole(() => exit = f.Run()));
        Assert.Equal(1, exit);
        Assert.Contains(missing ? "existing --text-file" : "nonempty --text-file", stderr);
        Assert.Empty(await f.Store.ListForGoalAsync(f.Goal.Id.Value));
    }

    [Fact]
    public async Task Tick_WhitespaceReason_RejectsDirectIntent()
    {
        using var f = new Fixture();
        var intent = await f.Queue(new("OldTests", "NewTests", " \r\n"));
        var result = f.Coordinator.ExecutePending(f.Kernel, f.Goal);
        Assert.Contains(result.ProgressLines, line => line.Contains("reason=invalid-payload", StringComparison.Ordinal));
        Assert.Equal(OperatorIntentStatus.Rejected, (await f.Store.GetAsync(intent.Id))!.Status);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Fact]
    public async Task Tick_RechecksAuthoritativeBrief_AndEarlierRebindDoesNotApplyToRevision()
    {
        using var f = new Fixture();
        f.Evidence.WriteAttempt([("NewTests.Check", "Passed")]);
        var intent = await f.Submit();
        await f.Apply(intent);
        Assert.Equal(1, Assert.Single(f.Kernel.HumanInputRequests).AuthoritativeAnswer!.BriefVersion);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, f.Evaluate().Outcome);

        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Version two");
        f.Reseed();
        Assert.Equal(2, f.Goal.AuthoritativeBrief.Version);
        var result = f.Evaluate();
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Empty(AnsweredSelectorRebinding.For(f.Goal, result.Obligation, f.Kernel.HumanInputRequests));
        Assert.Equal("", result.AnswerRebindProvenance);
    }

    [Fact]
    public async Task Tick_SelectsBriefAtApplication_AndRejectsObsoleteObligations()
    {
        using var f = new Fixture();
        var obsolete = await f.Submit();
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Version two");
        var result = f.Coordinator.ExecutePending(f.Kernel, f.Goal);
        Assert.Contains(result.ProgressLines, line => line.Contains("reason=no-matching-obligation", StringComparison.Ordinal));
        Assert.Equal(OperatorIntentStatus.Rejected, (await f.Store.GetAsync(obsolete.Id))!.Status);
        f.Reseed();
        await f.Save();
        var intent = await f.Submit();
        await f.Apply(intent);
        Assert.Equal(2, Assert.Single(f.Kernel.HumanInputRequests).AuthoritativeAnswer!.BriefVersion);
    }

    [Fact]
    public async Task ReasonContainingRenameProse_AuthorizesOnlyArgumentPair()
    {
        using var f = new Fixture();
        File.WriteAllText(f.ReasonPath, "OldTests becomes WrongTests; OtherTests becomes ExtraTests");
        await f.Apply(await f.Submit());
        var request = Assert.Single(f.Kernel.HumanInputRequests);
        var rebind = Assert.Single(AnsweredSelectorRebinding.For(f.Goal,
            Assert.Single(f.Goal.GateTestObligations), f.Kernel.HumanInputRequests));
        Assert.Equal("OldTests", rebind.OldClass);
        Assert.Equal("NewTests", rebind.NewClass);
        Assert.Contains("OtherTests becomes ExtraTests", request.Question);
    }

    [Fact]
    public void Registration_ExposesVerbHelpAndRejectsUnknownFlags()
    {
        Assert.Contains("obligation-rebind", CliArgumentParser.RecognizedCommands);
        Assert.True(CliHandledVerbRegistry.IsHandled("obligation-rebind"));
        Assert.True(CliObligationRebindCommand.IsCommand(["OBLIGATION-REBIND"]));
        var help = InfrastructureTestSupport.CaptureConsole(() =>
            Assert.True(CliCommandHelp.TryPrintStartupHelp(["help", "obligation-rebind"])));
        Assert.Contains(CliCommandHelp.ObligationRebindUsage, help);
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["obligation-rebind", "--unknown"]));
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
        private readonly string selector;

        internal Fixture(string selector = "OldTests", GoalStatus status = GoalStatus.Active)
        {
            this.selector = selector;
            Workspace = OrchestratorWorkspace.ForDirectory(Evidence.Root);
            Kernel = GateTestObligationIntakeTests.Seed(Evidence.Kernel, Evidence.Goal, [selector]);
            var snapshot = Kernel.ExportSnapshot();
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with { Status = status }).ToArray()
            });
            InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
            _ = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            Coordinator = new(Store, goalHeadResolver: _ => Evidence.CandidateSha,
                orchestratorDirectory: Evidence.OrchestratorDirectory);
            File.WriteAllText(ReasonPath, "reason from operator");
        }

        internal void Reseed() => Kernel = GateTestObligationIntakeTests.Seed(Kernel, Goal, [selector]);
        internal Task Save() => new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).SaveAsync(Kernel);
        internal GateTestObligationEvaluator.Result Evaluate() => Assert.Single(GateTestObligationEvaluator.Evaluate(
            Goal, Evidence.OrchestratorDirectory, Evidence.CandidateSha, answeredRequests: Kernel.HumanInputRequests));
        internal int Run(string oldClass = "OldTests", string newClass = "NewTests") => CliObligationRebindCommand.Run(
            ["obligation-rebind", Goal.Id.Value[..8], oldClass, newClass, "--text-file", ReasonPath], Workspace);
        internal async Task<OperatorIntentRecord> Submit()
        {
            InfrastructureTestSupport.CaptureConsole(() => Assert.Equal(0, Run()));
            return (await Store.ListForGoalAsync(Goal.Id.Value)).Single(intent => intent.Status == OperatorIntentStatus.Pending);
        }
        internal Task<OperatorIntentRecord> Queue(ObligationRebindOperatorIntentPayload payload)
        {
            var id = Guid.NewGuid().ToString("N");
            return Store.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.ObligationRebind,
                Goal.Id.Value, null, JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [],
                "operator", "cli", "local", DateTimeOffset.UtcNow));
        }
        internal async Task Apply(OperatorIntentRecord intent)
        {
            var result = Coordinator.ExecutePending(Kernel, Goal);
            Assert.True(result.MutatedGoalState, string.Join(Environment.NewLine, result.ProgressLines));
            await Save();
            Coordinator.CompletePersisted([Goal.Id]);
            Assert.Equal(OperatorIntentStatus.Applied, (await Store.GetAsync(intent.Id))!.Status);
        }
        public void Dispose() => Evidence.Dispose();
    }
}
