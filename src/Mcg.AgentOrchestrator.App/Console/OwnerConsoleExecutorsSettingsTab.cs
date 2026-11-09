using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleExecutorsSettingsTab : IOwnerConsoleSettingsTab
{
    private readonly string _storePath;
    private readonly ExecutorSettingIntentSubmitter _submitter;
    private IReadOnlyList<RemoteLaneExecutorEntry> _executors = [];
    private IReadOnlyList<string> _lines = [];
    private int _selected;

    internal OwnerConsoleExecutorsSettingsTab(OrchestratorWorkspace workspace, ExecutorSettingIntentSubmitter submitter)
    {
        _storePath = RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.ExecutionDirectory);
        _submitter = submitter;
        Refresh();
    }

    public string Title => "Executors";
    public bool Editing => false;
    public int SelectedLine => _executors.Count == 0 ? -1 : _selected * 2;
    public string StatusText { get; private set; } = "";
    public string KeyHint => "Up/Down highlight  Space enable/disable  Left/Right tab  Esc/q back (slots/poll/lease read-only)";
    public IReadOnlyList<string> Lines { get { Refresh(); return _lines; } }

    public void Refresh()
    {
        var selectedId = _executors.Count == 0 ? null : _executors[_selected].Id;
        _executors = RemoteLaneExecutorConfiguration.LoadExecutors(_storePath);
        if (_executors.Count == 0)
        {
            _selected = 0;
            _lines = ["Executors unavailable: executor store missing, invalid, unreadable or empty."];
            return;
        }

        var preserved = _executors.ToList().FindIndex(entry => entry.Id == selectedId);
        _selected = preserved >= 0 ? preserved : Math.Min(_selected, _executors.Count - 1);
        var decisions = ReadDecisions();
        var lines = new List<string>();
        foreach (var executor in _executors)
        {
            lines.Add($"{executor.Id}: {(executor.AdmissionEnabled ? "enabled" : "disabled")} slots={executor.Slots} " +
                $"transport={executor.Transport ?? "local"} pollSeconds={executor.PollSeconds} " +
                $"leaseSeconds={executor.LeaseSeconds} occupancyProbe={executor.OccupancyProbe.ToString().ToLowerInvariant()}");
            lines.Add(decisions?.GetValueOrDefault(executor.Id) is { } record
                ? $"  controller: proposed slots={record.Decision.ProposedSlots} " +
                  $"{(record.Decision.Pinned ? "pinned" : "not pinned")} application={record.ApplicationStatus}"
                : "  controller: no decision yet");
        }
        _lines = lines;
    }

    public void HandleKey(Key key)
    {
        if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value == OwnerConsoleBindings.SettingsToggleKey)
        {
            // Keep the identity the owner saw; refreshing first could select a different executor.
            var id = _executors.Count == 0 ? null : _executors[_selected].Id;
            if (id is null) StatusText = "No executor selected; nothing queued.";
            else
            {
                try
                {
                    var submission = _submitter.SubmitToggle(id);
                    StatusText = $"Queued executor '{submission.ExecutorId}' {(submission.Enabled ? "enabled" : "disabled")}; awaiting conductor.";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { StatusText = $"Executor toggle refused: {ex.Message}"; }
            }
        }
        else
        {
            Refresh();
            if (_executors.Count > 0)
            {
                if (key == Key.CursorUp) _selected = (_selected + _executors.Count - 1) % _executors.Count;
                else if (key == Key.CursorDown) _selected = (_selected + 1) % _executors.Count;
            }
        }
        Refresh();
    }

    private IReadOnlyDictionary<string, ExecutorSlotDecisionRecord>? ReadDecisions()
    {
        try
        {
            return ExecutorSlotDecisionLedger.ReadLatest(Path.Combine(
                Path.GetDirectoryName(_storePath)!, ExecutorSlotDecisionLedger.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or
            InvalidOperationException or KeyNotFoundException or NotSupportedException or ArgumentException)
        { return null; }
    }
}
