using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;

// Per-test state and an uninitialized headless app; no shared resources.
internal sealed class OwnerConsoleRefreshReuseFixture : IDisposable
{
    internal readonly OwnerConsoleTestClock Clock = new();
    internal readonly string Root = Directory.CreateTempSubdirectory("owner-read-reuse-").FullName;
    internal readonly OwnerConsoleHarness Harness;
    internal readonly Query State;
    internal readonly Questions Source = new();
    internal readonly OwnerConsoleRefreshGoalSnapshots Snapshots;
    internal readonly OwnerConsoleRefreshTimings Timings;
    internal readonly OwnerConsoleEventRefreshFilter Filter = new();
    internal OwnerConsoleViewModelBuilder Builder;
    internal OwnerRefreshCoordinator? Refresh;
    internal OwnerConsoleScreenController? Controller;
    internal string? Marker = "initial";
    internal string? TimingLine;
    internal int LifecycleReads;
    internal int GateReads;
    internal int TickReads;
    internal int ScanReads;
    internal int ScanCount;
    internal int EpicReads;
    internal string? BoardMarker = "state:1";
    internal string? EpicMarker = "portfolio:1";
    private IApplication? _app;
    private OwnerConsoleFullScreenView? _view;

    internal OwnerConsoleRefreshReuseFixture(bool marker = true, bool boardReuse = false)
    {
        Harness = new(Clock);
        Harness.AddGoal("11111111", "First", AgentRole.Developer);
        Harness.AddGoal("22222222", "Second", AgentRole.Developer);
        var snapshot = Harness.Kernel.ExportSnapshot();
        Harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
        {
            Status = GoalStatus.Verifying,
            Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray(),
            Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "gate", Clock.Now.AddMinutes(-2))]
        }).ToArray() });
        State = new(Harness.Kernel);
        Snapshots = new(State);
        Timings = new(Clock);
        Builder = new(State, Source, Harness.Liveness, new Epics(() => EpicReads++), Clock, snapshots: Snapshots,
            decisionsMarker: marker ? () => Marker : null,
            boardMarker: boardReuse ? () => BoardMarker : null, epicMarker: boardReuse ? () => EpicMarker : null,
            latestTick: () => { TickReads++; return Clock.Now; },
            gateProgress: _ => { GateReads++; return null; });
    }

    internal OwnerRefreshCoordinator Coordinator(OwnerConsoleLandedTodayScan? suppliedScan = null)
    {
        _app = Application.Create();
        // Preview reads belong to the view, outside the refresh snapshot cache being counted.
        Controller = OwnerConsoleControllerFactory.Create(Source, Harness.Answers, new Dialogs(),
            Harness.State, Harness.Tail, Harness.Conductor, Harness.DigestReport, Harness.Digest, Clock);
        _view = new(_app, Controller, () => Task.CompletedTask, clock: Clock);
        var scan = suppliedScan ?? new OwnerConsoleLandedTodayScan(() => { ScanReads++; return ScanCount; }, Clock);
        return Refresh = new(Clock, TestContext.Current.CancellationToken, Builder, null, Snapshots, Timings, Filter, null,
            (_, _) => { LifecycleReads++; return []; }, scan.Current,
            line => TimingLine = line, action => action(), (_, action) => action(), () => _view);
    }

    public void Dispose() { Refresh?.Dispose(); _view?.Dispose(); _app?.Dispose(); SharedTestSupport.RemoveTempDirectory(Root); }

    internal sealed class Query(AgentOrchestratorKernel kernel) : IOrchestratorStateQueries
    {
        internal readonly Dictionary<string, string> Versions = new() { ["11111111"] = "a", ["22222222"] = "a" };
        internal readonly Dictionary<string, int> Loads = new();
        internal readonly HashSet<string> Visible = new(["11111111", "22222222"]);
        internal int Boards;
        internal int RequestReads;
        internal readonly List<GoalBoardRowQuery> Queries = [];
        internal bool FailLoad;
        internal Func<Task>? BeforeLoad;
        internal Func<Task>? AfterLoad;
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(CancellationToken token = default)
        { RequestReads++; return Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>([]); }
        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> ids, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>([]);
        public async Task<IReadOnlyList<GoalSummary>> ListGoalBoardRowsAsync(GoalBoardRowQuery query, CancellationToken token = default)
        { Queries.Add(query); return await ListGoalMetadataAsync(token); }
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            Boards++;
            return Task.FromResult<IReadOnlyList<GoalSummary>>(kernel.Goals.Where(goal => Visible.Contains(goal.Id.Value))
                .Select(goal => new GoalSummary(goal.Id.Value, goal.Status.ToString(), goal.Objective, Versions[goal.Id.Value])).ToArray());
        }
        public async Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken cancellationToken = default)
        {
            if (BeforeLoad is not null) await BeforeLoad();
            if (FailLoad) throw new IOException("snapshot read failed");
            foreach (var id in ids) Loads[id.Value] = Loads.GetValueOrDefault(id.Value) + 1;
            var selected = ids.Select(id => id.Value).ToHashSet();
            var snapshot = kernel.ExportSnapshot();
            if (AfterLoad is not null) await AfterLoad();
            return AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Where(goal => selected.Contains(goal.Id)).ToArray(),
                HumanInputRequests = snapshot.HumanInputRequests.Where(request => selected.Contains(request.GoalId)).ToArray()
            });
        }
    }

    internal sealed class Questions : IOwnerQuestionSource
    {
        internal int Reads;
        internal bool Fail;
        internal readonly List<OwnerQuestion> Items = [];
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (Fail) throw new IOException("question read failed");
            return Task.FromResult<IReadOnlyList<OwnerQuestion>>(Items.ToArray());
        }
    }
    private sealed class Epics(Action read) : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
        public Task<IReadOnlyDictionary<string, string>> GetTitlesAsync(IReadOnlyCollection<string> ids, CancellationToken token)
        { read(); return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()); }
    }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
    }
}
