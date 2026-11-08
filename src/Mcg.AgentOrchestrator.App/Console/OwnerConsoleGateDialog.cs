using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateDialog : FrameView
{
    private readonly ListView _body = new() { Width = Dim.Fill(), Height = Dim.Fill(2) };
    private readonly Label _footer = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1,
        Text = OwnerConsoleDialogBindings.GateHelp.HintLabel! };
    private readonly Label _fieldsHint = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, Text = "F2 fields (snapshot)" };
    internal void FieldsUnavailable() => _fieldsHint.Text = "No selected structured item. F2 fields";
    private OwnerConsoleGateProgress? _gate;
    internal OwnerConsoleListRenderer SourceRenderer { get; } = new();
    private string? _goalId;
    private string? _executionId;
    internal bool Expanded { get; private set; }
    internal bool IsOpen { get; private set; }
    internal OwnerConsolePane ReturnPane { get; private set; }
    internal IReadOnlyList<string> Lines { get; private set; } = [];
    internal string FooterText => _footer.Text;
    internal int SelectedLine => _body.SelectedItem ?? 0;
    internal ListView BodyPane => _body;
    private OwnerConsoleStructuredText? _fields;
    internal OwnerConsoleFieldSnapshot? SelectedFields => _fields?.At(SelectedLine);

    internal OwnerConsoleGateDialog()
    {
        Title = "GATE";
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        Visible = false;
        Add(_body, _footer, _fieldsHint);
        _body.ViewportChanged += (_, _) => { if (IsOpen) RenderTitle(); };
    }

    internal void Open(OwnerConsolePane returnPane, OwnerConsoleGateProgress? gate, string? goalId = null)
    {
        ReturnPane = returnPane;
        _goalId = goalId;
        _executionId = goalId is null ? gate?.ExecutionId : null;
        Expanded = false;
        IsOpen = Visible = true;
        Render(gate);
        _body.SetFocus();
    }

    internal void Render(OwnerConsoleGateProgress? gate)
    {
        _gate = gate;
        if (!IsOpen) return;
        if (_goalId is not null)
        {
            if (gate?.ForMember(_goalId) is { } memberGate) gate = memberGate;
            else gate = OwnerConsoleGateProgress.Idle() with { Members = [new(_goalId, "")],
                Participation = "selected goal execution unresolved", LanePlanAvailable = false };
        }
        else if (_executionId is not null) gate = gate?.ForExecution(_executionId) ??
            OwnerConsoleGateProgress.Idle() with { ExecutionId = _executionId, Participation = "selected historical execution unavailable" };
        var selected = _body.SelectedItem ?? 0;
        var fields = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        Lines = OwnerConsoleGateLayout.Lines(gate, OwnerConsoleEpicFormatter.Width(_body.Viewport.Width), Expanded, fields);
        _fields = new(gate is null ? null : new OwnerConsoleGateFieldProjection(gate).FieldSnapshot(), fields);
        if (SourceRenderer.Apply(_body, Lines))
            _body.SelectedItem = Math.Clamp(selected, 0, Math.Max(0, Lines.Count - 1));
        RenderTitle();
    }

    private void RenderTitle()
    {
        var title = OwnerConsoleOverflow.Title("GATE",
            OwnerConsoleOverflow.Of(Lines.Count, _body.Viewport.Y, _body.Viewport.Height),
            Frame.Width - OwnerConsoleOverflow.TitleChrome);
        if (Title != title) Title = title;
    }

    internal bool HandleKey(Key key)
    {
        key.Handled = true;
        var binding = key.IsAlt ? null : OwnerConsoleBindings.ForDialogKey(
            OwnerConsoleScope.GateView, (char)key.NoCtrl.AsRune.Value, key.IsCtrl);
        var back = key == Key.Esc || binding?.Command == "quit";
        if (key == Key.Enter || key == Key.Esc && Expanded)
        {
            Expanded = key == Key.Enter && !Expanded;
            Render(_gate);
            return false;
        }
        if (back) { IsOpen = Visible = false; return true; }
        if (OwnerConsolePaneNavigator.Target(key, _body.SelectedItem ?? 0, Lines.Count, _body.Viewport.Height) is { } target)
        {
            _body.SelectedItem = target;
            _body.EnsureSelectedItemVisible();
            RenderTitle();
        }
        return false;
    }
}
