using Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGoalChangeSummaryDialogs : IOwnerConsoleDialogs
{
    internal List<string> Texts = [];
    public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
    public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("No writes");
    public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("No writes");
}
