using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static InfrastructureTestSupport;
using Custody = Mcg.AgentOrchestrator.App.Orchestration.GoalRefinementExecutorCustodyReceipt;

// Parallel-safe: each test owns its workspace/stores; console capture is flow-local and launchers are parameters.
public sealed class GoalRefinementExecutorCustodyReceiptTests
{
    private const string Stamp = "20261113180000000";
    private static readonly DateTimeOffset LaunchInstant = new(2026, 11, 13, 18, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void LaunchExecutor_Success_RecordsReturnedIdentityBesideLogs()
    {
        using var fixture = new WorkspaceFixture();
        ConductLoopLaunchRequest? observed = null;
        var result = GoalRefinementWorkCoordinator.LaunchExecutor(fixture.Workspace, fixture.GoalId, request =>
        {
            observed = request;
            return Launched(request);
        });

        Assert.Equal(new GoalRefinementWorkLaunchResult(true, 1234, "detached-test"), result);
        Assert.NotNull(observed);
        var stamp = observed.Args[^1];
        var path = Custody.GetPath(fixture.Workspace.LogDirectory, fixture.GoalId, stamp);
        Assert.Equal(observed.StdoutPath.Replace(".out.log", ".custody.json", StringComparison.Ordinal), path);
        Assert.Equal(observed.StderrPath.Replace(".err.log", ".custody.json", StringComparison.Ordinal), path);
        var receipt = Custody.Read(fixture.Workspace.LogDirectory, fixture.GoalId, stamp);
        Assert.Equal(Custody.Status.LaunchedButNeverStarted, receipt.Status);
        Assert.Equal(new Custody.Launch(1234, "detached-test", LaunchInstant.ToUniversalTime()), receipt.Receipt!.Launch);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        AssertUtcInstant(json.RootElement.GetProperty("launch").GetProperty("launchInstant"));
    }

    [Fact]
    public void LaunchExecutor_LaunchThrows_PreservesFailureAndWritesNoReceipt()
    {
        using var fixture = new WorkspaceFixture();
        var result = GoalRefinementWorkCoordinator.LaunchExecutor(fixture.Workspace, fixture.GoalId,
            _ => throw new InvalidOperationException("launcher failed"));

        Assert.Equal(new GoalRefinementWorkLaunchResult(false, null,
            "executor-launch-failed:InvalidOperationException:launcher failed", LaunchThrew: true), result);
        Assert.Empty(Directory.GetFiles(fixture.Workspace.LogDirectory, "*.custody.json"));
    }

    [Fact]
    public void LaunchExecutor_UnwritableDirectory_PreservesSuccessfulResult()
    {
        using var control = new WorkspaceFixture();
        using var broken = new WorkspaceFixture();
        var expected = GoalRefinementWorkCoordinator.LaunchExecutor(control.Workspace, control.GoalId, Launched);
        var actual = GoalRefinementWorkCoordinator.LaunchExecutor(broken.Workspace, broken.GoalId, request =>
        {
            // Fail directory access only after the launch block has created it.
            Directory.Delete(broken.Workspace.LogDirectory);
            File.WriteAllText(broken.Workspace.LogDirectory, "directory blocked by file");
            return Launched(request);
        });

        Assert.True(File.Exists(broken.Workspace.LogDirectory));
        Assert.Single(Directory.GetFiles(control.Workspace.LogDirectory, "*.custody.json"));
        Assert.Equal(new GoalRefinementWorkLaunchResult(true, 1234, "detached-test"), expected);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, "claim-miss")]
    [InlineData(true, "completed")]
    public async Task Child_CommandOutcome_RecordsStartAndExit(bool claimed, string outcome)
    {
        using var fixture = new WorkspaceFixture();
        if (claimed)
            await fixture.SeedMissingGoalMessage();
        var result = fixture.Execute(Stamp);

        AssertCommandOutcome(result, claimed);
        AssertChildReceipt(fixture, Stamp, outcome);
        Assert.Equal(Custody.Status.Exited, fixture.Read().Status);
        using var json = JsonDocument.Parse(File.ReadAllText(fixture.ReceiptPath));
        var child = json.RootElement.GetProperty("child");
        AssertUtcInstant(child.GetProperty("startInstant"));
        AssertUtcInstant(child.GetProperty("exit").GetProperty("exitInstant"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_WorkThrows_RecordsExceptionTypeOrPreservesExceptionWhenDirectoryIsUnwritable(bool blockedDirectory)
    {
        using var fixture = new WorkspaceFixture();
        await fixture.SeedPendingGoal();
        const string invalidWorkStamp = "not-a-17-digit-stamp";
        if (blockedDirectory)
            File.WriteAllText(fixture.Workspace.LogDirectory, "directory blocked by file");

        var result = fixture.Execute(invalidWorkStamp);

        var error = Assert.IsType<InvalidOperationException>(result.Error);
        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Contains("phase=create-service", error.Message, StringComparison.Ordinal);
        Assert.Null(result.Returned);
        Assert.Empty(result.Stdout);
        Assert.Empty(result.Stderr);
        if (blockedDirectory)
            Assert.Equal(Custody.Status.Unavailable, Custody.Read(fixture.Workspace.LogDirectory, fixture.GoalId, invalidWorkStamp).Status);
        else
            AssertChildReceipt(fixture, invalidWorkStamp, error.GetType().Name);
    }

    [Fact]
    public async Task Child_ProviderEntry_SeesStartBeforeWorkAndExitOnlyAfterReturn()
    {
        using var fixture = new WorkspaceFixture();
        await fixture.SeedPendingGoal();
        ModelFunctionCatalogStore.Save(fixture.Workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("custody-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var registry = new ObservingRegistry(fixture);

        var result = fixture.Execute(Stamp, registry);

        AssertCommandOutcome(result, claimed: true);
        Assert.Equal(1, registry.Calls);
        Assert.NotNull(registry.Observed);
        Assert.Equal(Custody.Status.StartedButDidNotExit, registry.Observed.Status);
        Assert.Null(registry.Observed.Receipt!.Child!.Exit);
        AssertChildReceipt(fixture, Stamp, "completed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_UnwritableDirectory_PreservesExitCodeAndOutput(bool claimed)
    {
        using var control = new WorkspaceFixture();
        using var broken = new WorkspaceFixture();
        if (claimed)
        {
            await control.SeedMissingGoalMessage();
            await broken.SeedMissingGoalMessage();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(broken.Workspace.LogDirectory)!);
        File.WriteAllText(broken.Workspace.LogDirectory, "directory blocked by file");

        var expected = control.Execute(Stamp);
        var actual = broken.Execute(Stamp);

        AssertCommandOutcome(expected, claimed);
        AssertCommandOutcome(actual, claimed);
        Assert.Equal(expected.Returned, actual.Returned);
        Assert.Equal(Normalize(expected.Stdout, control), Normalize(actual.Stdout, broken));
        Assert.Equal(Normalize(expected.Stderr, control), Normalize(actual.Stderr, broken));
        Assert.True(File.Exists(broken.Workspace.LogDirectory));
        Assert.Equal(Custody.Status.Exited, control.Read().Status);
        Assert.Equal(Custody.Status.Unavailable, broken.Read().Status);
    }

    [Fact]
    public async Task Child_NoStamp_SkipsCustodyWithoutChangingWork()
    {
        using var fixture = new WorkspaceFixture();
        await fixture.SeedMissingGoalMessage();

        AssertCommandOutcome(fixture.Execute(null), claimed: true);

        Assert.False(Directory.Exists(fixture.Workspace.LogDirectory));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("")]
    [InlineData("\0")]
    public void Recording_UnusableStamp_IsSilentAndDoesNotThrow(string stamp)
    {
        using var fixture = new WorkspaceFixture();
        var stdout = string.Empty;
        var stderr = AsyncLocalConsoleRouter.CaptureError(() => stdout = AsyncLocalConsoleRouter.Capture(() =>
        {
            Custody.RecordLaunch(fixture.Workspace.LogDirectory, fixture.GoalId, stamp, fixture.LaunchResult());
            var scope = Custody.BeginChild(fixture.Workspace.LogDirectory, fixture.GoalId, stamp, fixture.Workspace.SqliteStatePath);
            scope.RecordExit();
        }));

        Assert.Empty(stdout);
        Assert.Empty(stderr);
        Assert.False(Directory.Exists(fixture.Workspace.LogDirectory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Receipt_WriterOrder_PreservesLaunchAndCompleteChild(int launchPhase)
    {
        using var fixture = new WorkspaceFixture();
        if (launchPhase == 0)
            fixture.RecordLaunch();
        var scope = fixture.BeginChild();
        if (launchPhase == 1)
            fixture.RecordLaunch();
        scope.RecordExit();
        if (launchPhase == 2)
            fixture.RecordLaunch();

        Assert.Equal(Custody.Status.Exited, fixture.Read().Status);
        Assert.Equal(1234, fixture.Read().Receipt!.Launch!.ProcessId);
        AssertChildReceipt(fixture, Stamp, "completed");
    }

    [Fact]
    public void Receipt_LaunchOnly_ReadsLaunchedButNeverStarted()
    {
        using var fixture = new WorkspaceFixture();
        fixture.RecordLaunch();

        var read = fixture.Read();

        Assert.Equal(Custody.Status.LaunchedButNeverStarted, read.Status);
        Assert.NotNull(read.Receipt!.Launch);
        Assert.Null(read.Receipt.Child);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Receipt_StartWithoutExit_ReadsStartedButDidNotExit(bool withLaunch)
    {
        using var fixture = new WorkspaceFixture();
        if (withLaunch)
            fixture.RecordLaunch();
        _ = fixture.BeginChild();

        var read = fixture.Read();

        Assert.Equal(Custody.Status.StartedButDidNotExit, read.Status);
        Assert.NotNull(read.Receipt!.Child);
        Assert.Null(read.Receipt.Child.Exit);
    }

    [Fact]
    public void Receipt_Missing_ReadsUnavailable()
    {
        using var fixture = new WorkspaceFixture();
        Assert.Equal(Custody.Status.Unavailable, fixture.Read().Status);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"launch\":{\"processId\":1234}}")]
    [InlineData("{\"child\":{\"exit\":{\"exitInstant\":\"2026-11-13T18:00:00Z\",\"outcome\":\"completed\"}}}")]
    public void Receipt_MalformedOrInconsistent_ReadsInvalidAndIsPreservedByWriters(string contents)
    {
        using var fixture = new WorkspaceFixture();
        Directory.CreateDirectory(fixture.Workspace.LogDirectory);
        File.WriteAllText(fixture.ReceiptPath, contents);

        fixture.RecordLaunch();
        fixture.BeginChild().RecordExit();

        Assert.Equal(Custody.Status.Invalid, fixture.Read().Status);
        Assert.Equal(contents, File.ReadAllText(fixture.ReceiptPath));
    }

    private static ConductLoopLaunchResult Launched(ConductLoopLaunchRequest request) =>
        new(1234, request.StdoutPath, request.StderrPath, "detached-test", LaunchInstant);

    private static Custody.Child AssertChildReceipt(WorkspaceFixture fixture, string stamp, string outcome)
    {
        var read = Custody.Read(fixture.Workspace.LogDirectory, fixture.GoalId, stamp);
        Assert.Equal(Custody.Status.Exited, read.Status);
        var child = Assert.IsType<Custody.Child>(read.Receipt!.Child);
        Assert.Equal(Environment.ProcessId, child.ProcessId);
        Assert.Equal(Path.GetFullPath(fixture.Workspace.SqliteStatePath), child.StateStorePath);
        Assert.NotNull(child.Exit);
        Assert.Equal(outcome, child.Exit.Outcome);
        return child;
    }

    private static void AssertUtcInstant(JsonElement element)
    {
        var instant = DateTimeOffset.Parse(element.GetString()!, CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, instant.Offset);
    }

    private static void AssertCommandOutcome(CommandResult result, bool claimed)
    {
        if (claimed)
        {
            Assert.Null(result.Error);
            Assert.False(result.Returned);
            Assert.Contains("SPEC_REFINEMENT_WORK_COMPLETE", result.Stdout, StringComparison.Ordinal);
            Assert.Empty(result.Stderr);
        }
        else
        {
            Assert.Equal(1, Assert.IsType<CliExitException>(result.Error).ExitCode);
            Assert.Null(result.Returned);
            Assert.Empty(result.Stdout);
            Assert.Contains("reason=claim-miss", result.Stderr, StringComparison.Ordinal);
        }
    }

    private static string Normalize(string output, WorkspaceFixture fixture) =>
        Regex.Replace(output.Replace(Path.GetFullPath(fixture.Workspace.SqliteStatePath), "<store>", StringComparison.Ordinal)
            .Replace(fixture.GoalId.Value, "<goal>", StringComparison.Ordinal), @"observed_at=\S+", "observed_at=<instant>");

    private sealed record CommandResult(bool? Returned, Exception? Error, string Stdout, string Stderr);

    private sealed class ObservingRegistry(WorkspaceFixture fixture) : IModelProviderRegistry
    {
        public int Calls { get; private set; }
        public Custody.ReadResult? Observed { get; private set; }
        public IModelProvider GetRequired(string providerName)
        {
            Calls++;
            Observed = fixture.Read();
            return new FakeSmokeProvider(text: """
                ```json
                {"behavioralContract":"Record executor custody.","acceptanceCriteria":["Persist custody."],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
                ```
                """, providerName: providerName);
        }
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-custody-" + Guid.NewGuid().ToString("N"));
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOrchestratorStateRepository Repository { get; }
        public GoalId GoalId { get; private set; } = GoalId.New();
        public string ReceiptPath => Custody.GetPath(Workspace.LogDirectory, GoalId, Stamp);

        public WorkspaceFixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            _ = StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            Repository = new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath);
        }

        public async Task SeedMissingGoalMessage()
        {
            await Repository.EnsureOutboxMessageAsync(
                GoalRefinementWorkCoordinator.CreateMessage(GoalId), TestContext.Current.CancellationToken);
        }

        public async Task SeedPendingGoal()
        {
            await Repository.TransactWithOutboxAsync((kernel, _) =>
            {
                var goal = kernel.CreateGoal("Record executor custody beside its logs.");
                GoalId = goal.Id;
                GoalRefinementWorkCoordinator.RecordPending(kernel, goal.Id);
                return Task.FromResult((ShouldSave: true, Result: true,
                    OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[GoalRefinementWorkCoordinator.CreateMessage(goal.Id)]));
            }, TestContext.Current.CancellationToken);
        }

        public Custody.ReadResult Read() => Custody.Read(Workspace.LogDirectory, GoalId, Stamp);
        public ConductLoopLaunchResult LaunchResult() => Launched(GoalRefinementWorkCoordinator.CreateLaunchRequest(Workspace, GoalId, Stamp));
        public void RecordLaunch() => Custody.RecordLaunch(Workspace.LogDirectory, GoalId, Stamp, LaunchResult());
        public Custody.ChildScope BeginChild() => Custody.BeginChild(Workspace.LogDirectory, GoalId, Stamp, Workspace.SqliteStatePath);

        public CommandResult Execute(string? stamp, IModelProviderRegistry? providers = null)
        {
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            bool? returned = null;
            Exception? error = null;
            var stdout = string.Empty;
            IReadOnlyList<string> args = stamp is null
                ? [GoalRefinementWorkCoordinator.CommandName, GoalId.Value]
                : [GoalRefinementWorkCoordinator.CommandName, GoalId.Value, stamp];
            var stderr = AsyncLocalConsoleRouter.CaptureError(() => stdout = AsyncLocalConsoleRouter.Capture(() =>
                error = Record.Exception(() =>
                {
                    returned = CliPersistentStateRunner.ExecuteCommand(args, Repository, Workspace, ref agents,
                        providers ?? new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal);
                })));
            return new CommandResult(returned, error, stdout, stderr);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
