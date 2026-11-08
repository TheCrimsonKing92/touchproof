using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleBindingSurface
{
    bool ActionRunning { get; }
    OwnerConsolePane FocusedPane { get; }
    TextField CommandLine { get; }
    ListView ActivityPane { get; }
    OwnerConsoleActivityPane ActivityModel { get; }
    OwnerConsoleEpicDialog EpicView { get; }
    bool EditingCommand { set; }
    View? CommandReturnFocus { set; }
    bool Rendering { set; }
    int ActivityWidth { get; }
    bool RefuseSelectedBoardRow();
    void StartAction();
    void ShowNotice(string message, OwnerConsoleNoticeSeverity severity, OwnerConsoleNoticeSource source);
    Task ActAsync(Func<CancellationToken, Task> action);
    void OpenGate(string? goalId);
    void FocusPane(OwnerConsolePane pane);
    void FollowLatest();
    void RenderHints();
    void RenderOverflow();
    void RequestStop();
    IDisposable? BeginActivitySourceReplacement();
}

internal sealed class OwnerConsoleBindingRunner(IOwnerConsoleBindingSurface surface,
    OwnerConsoleScreenController controller, OwnerConsoleScreenOperation operation,
    Func<string?, OwnerConsoleScopeCommandResult>? handleScope, Func<Task> refresh, CancellationToken token)
{
    internal async Task RunAsync(OwnerConsoleBinding binding, string? argument = null, bool fromKey = false)
    {
        if (binding == OwnerConsoleBindings.Quit)
        {
            await controller.HandleKeyAsync(0, binding.Key!.Value, cancellationToken: token);
            surface.RequestStop();
            return;
        }
        if (surface.ActionRunning) return;
        if (OwnerConsoleBindings.RequiresSelectedBoardRow(binding) && surface.RefuseSelectedBoardRow()) return;
        if (binding == OwnerConsoleBindings.ProjectScope)
        {
            surface.StartAction();
            var result = handleScope?.Invoke(argument) ?? new OwnerConsoleScopeCommandResult(
                "scope: project registry unavailable in this session", false, false);
            surface.ShowNotice(result.Message, result.Success ? OwnerConsoleNoticeSeverity.Success : OwnerConsoleNoticeSeverity.Failure,
                OwnerConsoleNoticeSource.Action);
            if (result.Changed) await refresh();
        }
        else if (binding.Owner == OwnerConsoleCommandOwner.Controller)
            await surface.ActAsync(ct => controller.RunCommandAsync(binding.Command, operation, ct));
        else if (binding == OwnerConsoleBindings.Gate)
        { surface.StartAction(); surface.OpenGate(argument); }
        else if (binding == OwnerConsoleBindings.Epics)
        { surface.StartAction(); await surface.EpicView.OpenAsync(surface.FocusedPane); }
        else if (binding == OwnerConsoleBindings.Help)
            await surface.ActAsync(_ => controller.ShowHelpAsync());
        else if (binding.Scope == OwnerConsoleScope.Decisions)
        {
            if (surface.FocusedPane == OwnerConsolePane.Decisions && controller.SelectedIndex >= 0)
                await surface.ActAsync(ct => controller.HandleKeyAsync(0, binding.Key!.Value, operation, ct));
            else if (surface.FocusedPane != OwnerConsolePane.Decisions)
            {
                surface.StartAction();
                surface.ShowNotice(OwnerConsoleKeyHints.UnavailableKey(binding.Key!.Value, surface.FocusedPane), OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
            }
        }
        else if (binding == OwnerConsoleBindings.Follow)
        { surface.StartAction(); surface.FocusPane(OwnerConsolePane.Activity); surface.FollowLatest(); }
        else if (binding == OwnerConsoleBindings.Filter)
        {
            if (fromKey)
            {
                surface.CommandReturnFocus = surface.ActivityPane;
                surface.EditingCommand = true;
                surface.CommandLine.Text = OwnerConsoleActivityFilter.CommandText(surface.ActivityModel.Filter);
                surface.CommandLine.InsertionPoint = surface.CommandLine.Text.Length;
                surface.CommandLine.SetFocus();
                surface.RenderHints();
                return;
            }
            surface.StartAction();
            surface.FocusPane(OwnerConsolePane.Activity);
            surface.Rendering = true;
            try
            {
                using var trace = surface.BeginActivitySourceReplacement();
                surface.ActivityModel.Refilter(controller.Model, argument, surface.ActivityWidth);
            }
            finally { surface.Rendering = false; }
            surface.RenderHints();
            surface.RenderOverflow();
            surface.ShowNotice(surface.ActivityModel.Filter is { } filter ? OwnerConsoleActivityFilter.AppliedNotice(filter,
                surface.ActivityModel.Rows.Count(row => row.ItemIndex is not null),
                Math.Min(controller.Model?.Activity.Length ?? 0, OwnerConsoleViewModelBuilder.MaxActivityItems))
                : OwnerConsoleActivityFilter.ClearedNotice, OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action);
        }
        else throw new InvalidOperationException($"No view action for binding {binding.Command}.");
    }
}
