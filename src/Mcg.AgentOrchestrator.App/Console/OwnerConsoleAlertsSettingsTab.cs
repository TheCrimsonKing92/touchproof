using System.Globalization;
using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleAlertsSettingsTab(OwnerConsoleSoundCommand sound) : IOwnerConsoleSettingsTab
{
    private readonly List<Row> _rows = [];
    private IReadOnlyList<string> _lines = [];
    private string? _editingKey;
    private string _input = "";
    public string Title => "Alerts & sounds";
    public bool Editing => _editingKey is not null;
    public int SelectedLine { get; private set; }
    internal string Message { get; private set; } = "";
    public string StatusText => Editing ? $"{_editingKey}: {_input}_  {Message}" : Message;
    public string KeyHint => Editing ? "Enter save  Esc cancel  Backspace edit" :
        "Up/Down highlight  p preview  Enter select/edit  s system sound  m mute  Left/Right tab  Esc/q back";
    public IReadOnlyList<string> Lines { get { Refresh(); return _lines; } }

    public void Refresh()
    {
        _rows.Clear();
        var snapshot = sound.Settings.Current;
        foreach (var tier in new[] { OwnerConsoleNotificationTier.Calm, OwnerConsoleNotificationTier.Attention, OwnerConsoleNotificationTier.Alarm, OwnerConsoleNotificationTier.Decision })
        {
            var selected = OwnerConsoleSoundCommand.Selected(snapshot, tier);
            var name = tier.ToString().ToLowerInvariant();
            var selection = string.IsNullOrWhiteSpace(selected) ? "system sound" : Path.GetFileName(selected);
            _rows.Add(new($"{name}: {selection} ({(snapshot.BellTiers.IsOn(tier) ? "on" : "muted")})", tier));
            var output = new OwnerConsoleCapturedOutput();
            var clips = sound.Clips(tier, output);
            foreach (var clip in clips) _rows.Add(new($"  {Path.GetFileName(clip)}", tier, Path.GetFileName(clip)));
            if (clips.Length == 0) _rows.Add(new("  (no clips)", tier));
            foreach (var line in output.Text.TrimEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                _rows.Add(new("  " + line.TrimEnd('\r'), tier));
        }
        Threshold("attentionThresholdMinutes", snapshot.AttentionThreshold.TotalMinutes, 30, "minutes");
        Threshold("staleTickThresholdMinutes", snapshot.StaleTickThreshold.TotalMinutes, 15, "minutes");
        Threshold("calmCoalesceSeconds", snapshot.CalmCoalesce.TotalSeconds, 60, "seconds");
        SelectedLine = Math.Clamp(SelectedLine, 0, _rows.Count - 1);
        _lines = _rows.Select(row => row.Text).ToArray();
    }

    private void Threshold(string key, double value, int defaultValue, string unit) =>
        _rows.Add(new($"{key}: {value.ToString(CultureInfo.InvariantCulture)} {unit} (default {defaultValue})", Threshold: key));

    public void HandleKey(Key key)
    {
        if (Editing)
        {
            if (key == Key.Esc) { _editingKey = null; _input = ""; Message = "Edit cancelled."; }
            else if (key == Key.Enter)
            {
                if (!int.TryParse(_input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
                    Message = "Enter a positive whole number within the supported range.";
                else
                {
                    var output = new OwnerConsoleCapturedOutput();
                    if (sound.SetThreshold(_editingKey!, value, output)) _editingKey = null;
                    Message = output.Text.TrimEnd();
                }
            }
            else if (key == Key.Backspace) { if (_input.Length > 0) _input = _input[..^1]; }
            else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value is >= 32 and <= 126 && _input.Length < 64)
                _input += (char)key.AsRune.Value;
            Refresh();
            return;
        }

        Refresh();
        if (key == Key.CursorUp) SelectedLine = Math.Max(0, SelectedLine - 1);
        else if (key == Key.CursorDown) SelectedLine = Math.Min(_rows.Count - 1, SelectedLine + 1);
        else if (OwnerConsolePaneNavigator.Target(key, SelectedLine, _rows.Count, 10) is { } target) SelectedLine = target;
        else
        {
            var row = _rows[SelectedLine];
            var output = new OwnerConsoleCapturedOutput();
            var character = key.IsCtrl || key.IsAlt ? '\0' : char.ToLowerInvariant((char)key.AsRune.Value);
            if (key == Key.Enter && row.Threshold is { } threshold)
            { _editingKey = threshold; _input = ""; Message = ""; }
            else if (row.Tier is { } tier)
            {
                if (key == Key.Enter && row.Selector is { } selector) sound.Select(tier, selector, output);
                else if (character == OwnerConsoleBindings.SettingsPreviewKey && row.Selector is { } preview) sound.Preview(tier, preview, output);
                else if (character == OwnerConsoleBindings.SettingsSystemKey) sound.Select(tier, "system", output);
                else if (character == OwnerConsoleBindings.SettingsMuteKey)
                {
                    var tiers = sound.Settings.Current.BellTiers;
                    if (sound.SetBellTiers(tiers.With(tier, !tiers.IsOn(tier)), output))
                        output.WriteLine($"{tier.ToString().ToLowerInvariant()}: {(sound.Settings.Current.BellTiers.IsOn(tier) ? "on" : "muted")}");
                }
                if (output.Text.Length > 0) Message = output.Text.TrimEnd();
            }
        }
        Refresh();
    }

    private sealed record Row(string Text, OwnerConsoleNotificationTier? Tier = null,
        string? Selector = null, string? Threshold = null);
}
