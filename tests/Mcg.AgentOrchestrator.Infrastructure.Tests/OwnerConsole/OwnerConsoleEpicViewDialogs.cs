using Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleEpicViewDialogs : IOwnerConsoleDialogs
{
    internal readonly List<(string Title, string Text)> Texts = [];
    public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
    public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
    public Task ShowTextAsync(string title, string text) { Texts.Add((title, text)); return Task.CompletedTask; }
}
