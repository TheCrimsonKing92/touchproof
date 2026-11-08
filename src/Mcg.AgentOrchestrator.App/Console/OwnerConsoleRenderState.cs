namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleRenderState
{
    internal bool Rendering { get; set; }
    internal OwnerConsoleViewModel? ArrangedBoard { get; set; }
    internal string? SelectedFieldKey { get; set; } = "goal";
    internal int ActivityWidth { get; set; }
    internal int BoardTitleWidth { get; set; }
    internal DateTimeOffset? RefreshedAt { get; set; }
}
