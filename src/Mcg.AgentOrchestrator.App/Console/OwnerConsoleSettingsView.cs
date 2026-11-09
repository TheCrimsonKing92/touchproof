using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleSettingsView : FrameView
{
    private readonly IReadOnlyList<IOwnerConsoleSettingsTab> _tabs;
    private readonly Label _strip = new() { Width = Dim.Fill(), Height = 1 };
    private readonly ListView _body = new() { Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2) };
    private readonly Label _message = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Label _footer = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private readonly OwnerConsoleListRenderer _renderer = new();
    private int _active;
    internal bool IsOpen { get; private set; }
    internal OwnerConsolePane ReturnPane { get; private set; }
    internal IReadOnlyList<string> Lines => _tabs.Count == 0 ? ["Settings unavailable in this session."] : _tabs[_active].Lines;
    internal int ActiveTab => _active;

    internal OwnerConsoleSettingsView(IReadOnlyList<IOwnerConsoleSettingsTab> tabs)
    {
        _tabs = tabs.ToArray();
        Title = "SETTINGS";
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        Visible = false;
        Add(_strip, _body, _message, _footer);
    }

    internal void Open(OwnerConsolePane returnPane)
    {
        ReturnPane = returnPane;
        IsOpen = Visible = true;
        Refresh();
        _body.SetFocus();
    }

    internal void Refresh()
    {
        if (!IsOpen) return;
        _strip.Text = string.Join("  ", _tabs.Select((tab, index) => index == _active ? $"[{tab.Title}]" : tab.Title));
        var lines = Lines;
        _renderer.Apply(_body, lines);
        _message.Text = _tabs.Count == 0 ? "" : _tabs[_active].StatusText;
        _footer.Text = _tabs.Count == 0 ? "Esc/q back" : _tabs[_active].KeyHint;
        if (_tabs.Count > 0)
        {
            _body.SelectedItem = _tabs[_active].SelectedLine;
            _body.EnsureSelectedItemVisible();
        }
    }

    internal bool HandleKey(Key key)
    {
        key.Handled = true;
        var editing = _tabs.Count > 0 && _tabs[_active].Editing;
        if (!editing && (key == Key.Esc || !key.IsCtrl && !key.IsAlt && char.ToLowerInvariant((char)key.AsRune.Value) == 'q'))
        { IsOpen = Visible = false; return true; }
        if (!editing && _tabs.Count > 0 && (key == Key.CursorLeft || key == Key.CursorRight))
            _active = (_active + _tabs.Count + (key == Key.CursorLeft ? -1 : 1)) % _tabs.Count;
        else if (_tabs.Count > 0) _tabs[_active].HandleKey(key);
        Refresh();
        return false;
    }
}
