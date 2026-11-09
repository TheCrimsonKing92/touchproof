using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;
using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleProjectsSettingsTab(OrchestratorProjectRegistry registry,
    Func<string?, OwnerConsoleScopeCommandResult> handleScope) : IOwnerConsoleSettingsTab
{
    private IReadOnlyList<string> _lines = [];
    private string _input = "";
    public string Title => "Projects";
    public bool Editing { get; private set; }
    public int SelectedLine { get; private set; }
    internal string Message { get; private set; } = "";
    public string StatusText => Editing ? $"scope: {_input}_  {Message}" : Message;
    public string KeyHint => Editing ? "Enter apply  Esc cancel  Backspace edit" :
        "Up/Down highlight  Enter edit scope  Left/Right tab  Esc/q back";
    public IReadOnlyList<string> Lines { get { Refresh(); return _lines; } }

    public void Refresh()
    {
        var lines = new List<string> { handleScope(null).Message };
        try
        {
            foreach (var project in registry.ListProjects())
                lines.Add($"{project.Name}: branch {project.IntegrationBranch}  push {PushState(project)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { lines.Add($"registry: unreadable: {ex.Message.ReplaceLineEndings(" ")}"); }
        _lines = lines;
        SelectedLine = Math.Clamp(SelectedLine, 0, _lines.Count - 1);
    }

    private static string PushState(OrchestratorProject project)
    {
        try
        {
            var directory = project.ResolveWorkspace().OrchestratorDirectory;
            if (!File.Exists(Path.Combine(directory, "conductor-policy.json"))) return "unreadable";
            return ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(directory))
                .AdoptedBuildPushEnabled ? "on" : "off";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return "unreadable"; }
    }

    public void HandleKey(Key key)
    {
        if (Editing)
        {
            if (key == Key.Esc) { Editing = false; _input = ""; Message = "Edit cancelled."; }
            else if (key == Key.Enter)
            {
                var result = handleScope(_input);
                Message = result.Message;
                if (result.Success) Editing = false;
            }
            else if (key == Key.Backspace) { if (_input.Length > 0) _input = _input[..^1]; }
            else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value is >= 32 and <= 126)
                _input += (char)key.AsRune.Value;
            Refresh();
            return;
        }

        Refresh();
        if (key == Key.CursorUp) SelectedLine = Math.Max(0, SelectedLine - 1);
        else if (key == Key.CursorDown) SelectedLine = Math.Min(_lines.Count - 1, SelectedLine + 1);
        else if (OwnerConsolePaneNavigator.Target(key, SelectedLine, _lines.Count, 10) is { } target) SelectedLine = target;
        else if (key == Key.Enter && SelectedLine == 0)
        { Editing = true; _input = ""; Message = ""; }
    }
}
