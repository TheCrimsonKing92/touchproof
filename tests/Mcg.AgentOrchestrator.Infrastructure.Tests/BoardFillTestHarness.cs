using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Every file belongs to this fixture's unique root. No model, Git or shared store is used.
internal sealed class BoardFillTestHarness : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-board-fill-" + Guid.NewGuid().ToString("N"));
    internal string StorePath => Path.Combine(Root, "board-fill.db");
    internal string EventsPath => Path.Combine(Root, "conduct-events.log");
    internal PanelTestHarness.Clock Clock { get; } = new();
    internal AgentOrchestratorKernel Kernel { get; }
    internal ConductorBoardFillDraftStore Store { get; }
    internal ConductorBoardFillHost Host { get; }
    internal ConductorAutonomyPolicy Policy { get; set; } = ConductorAutonomyPolicy.Permissive;
    internal IReadOnlyList<BacklogItem> Items { get; set; } = [BoardFillReadyItemSelectorTests.Item()];
    internal Func<IReadOnlyList<BacklogItem>, IReadOnlySet<string>> EpicMembership { get; set; } =
        items => items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
    internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    internal int Calls => Volatile.Read(ref _calls);
    internal bool Held { get; set; }
    internal Func<AuthorBriefDraftOutcome>? Reply { get; set; }
    internal string MainHead { get; set; } = new string('c', 40);
    private int _mainMoves;
    internal void MoveMain() => MainHead = (++_mainMoves).ToString("x40");

    internal BoardFillTestHarness(Action? beforeRequiredEventDrain = null)
    {
        Directory.CreateDirectory(Root);
        Clock.UtcNow = BoardFillReadyItemSelectorTests.Now;
        Kernel = new(Clock);
        Store = new(StorePath);
        Host = NewHost(beforeRequiredEventDrain: beforeRequiredEventDrain);
    }

    internal ConductorBoardFillHost NewHost(Func<string?>? mainHead = null,
        Action? beforeRequiredEventDrain = null) => new(Store, (_, token) =>
    {
        Interlocked.Increment(ref _calls);
        Started.TrySetResult(true);
        if (Held) _release.Task.WaitAsync(token).GetAwaiter().GetResult();
        var outcome = Reply?.Invoke() ?? Draft();
        return outcome.Kind == "held" && outcome.HeldMainHead is null
            ? outcome with { HeldMainHead = MainHead }
            : outcome;
    }, () => Items,
        (_, _) => item => BacklogDependencyReadiness.Evaluate(item, _ => null, _ => null, _ => new(null), _ => "Running"),
        () => Policy, new(EventsPath, utcNow: () => Clock.UtcNow, beforeRequiredEventDrain: beforeRequiredEventDrain),
        items => EpicMembership(items),
        () => Clock.UtcNow, mainHead: mainHead ?? (() => MainHead));

    internal AuthorBriefDraftOutcome Draft() => new("draft", 0, new string('c', 40),
        Path.Combine(Root, "draft.md"), Path.Combine(Root, "receipt.json"),
        [new("sections", true, "ok"), new("citations", true, "ok")]);
    internal void Release() => _release.TrySetResult(true);
    internal string[] Events() => LifecycleEvents(EventsPath);
    internal static string[] LifecycleEvents(string path) => File.Exists(path) ? File.ReadLines(path).Where(line =>
    {
        using var entry = Event(line);
        return entry.RootElement.GetProperty("eventKind").GetString() != "BOARD_FILL_READINESS";
    }).ToArray() : [];
    internal static JsonDocument Event(string text) => JsonDocument.Parse(text);
    public void Dispose()
    {
        Release();
        Host.Stop();
        Directory.Delete(Root, recursive: true);
    }
}
