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
    internal string? TargetProvider { get; private set; }

    internal async Task RunBindingAsync(OwnerConsoleScreenController controller, OwnerConsoleBindingRunner runner,
        OwnerConsoleBinding binding, string? argument, bool fromKey, bool actionRunning,
        OwnerConsolePane pane, OwnerConsoleActivityPane activity)
    {
        if (binding != OwnerConsoleBindings.Settings || actionRunning)
        {
            await runner.RunAsync(binding, argument, fromKey);
            return;
        }
        controller.SetSettingsTarget(fromKey ? SelectedSignInProvider(controller.Model, pane, activity) : null);
        try { await runner.RunAsync(binding, argument, fromKey); }
        finally { controller.SetSettingsTarget(null); }
    }

    internal static string? SelectedSignInProvider(OwnerConsoleViewModel? model, OwnerConsolePane pane,
        OwnerConsoleActivityPane activity)
    {
        if (pane != OwnerConsolePane.Activity || model is not { ActivityState: null } ||
            activity.List.SelectedItem is not { } row || row < 0 || row >= activity.Rows.Count ||
            activity.Rows[row].ItemIndex is not { } index || index < 0 || index >= model.Activity.Length) return null;
        return model.Activity[index].SignInProvider;
    }

    internal void OpenRequested(OwnerConsoleScreenController controller, OwnerConsolePane pane, CancellationToken token)
    {
        var provider = controller.TakeSettingsTarget();
        if (controller.TakeSettingsRequested() && !token.IsCancellationRequested) Open(pane, provider);
    }

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

    internal void Open(OwnerConsolePane returnPane, string? provider = null)
    {
        ClearTarget();
        if (!string.IsNullOrWhiteSpace(provider))
        {
            for (var index = 0; index < _tabs.Count; index++)
            {
                if (_tabs[index] is not OwnerConsoleProviderConnectionsTab connections) continue;
                _active = index;
                TargetProvider = provider.Trim();
                connections.Target(TargetProvider);
                break;
            }
        }
        ReturnPane = returnPane;
        IsOpen = Visible = true;
        if (_tabs.Count > 0) _tabs[_active].Activate();
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
        { IsOpen = Visible = false; ClearTarget(); return true; }
        if (!editing && _tabs.Count > 0 && (key == Key.CursorLeft || key == Key.CursorRight))
        {
            _active = (_active + _tabs.Count + (key == Key.CursorLeft ? -1 : 1)) % _tabs.Count;
            _tabs[_active].Activate();
        }
        else if (_tabs.Count > 0) _tabs[_active].HandleKey(key);
        Refresh();
        return false;
    }

    private void ClearTarget()
    {
        TargetProvider = null;
        foreach (var connections in _tabs.OfType<OwnerConsoleProviderConnectionsTab>()) connections.Target(null);
    }

    // A navigation target is consumed once, after the tab has a profile list.
    internal sealed class ProviderTarget(Func<OwnerConsoleProviderRowModel[]> rows, Action<int> select)
    {
        private string? _pending;
        internal string? Notice { get; set; }

        internal void Set(string? provider, bool loaded)
        {
            _pending = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();
            Notice = null;
            if (loaded) Apply();
        }

        internal void Apply()
        {
            if (_pending is not { } provider) return;
            _pending = null;
            var index = Array.FindIndex(rows(), row => row.Members.Any(member =>
                StringComparer.OrdinalIgnoreCase.Equals(member.Report.Provider, provider)));
            if (index < 0) Notice = $"No configured profile found for provider {provider}.";
            else select(index);
        }
    }
}
