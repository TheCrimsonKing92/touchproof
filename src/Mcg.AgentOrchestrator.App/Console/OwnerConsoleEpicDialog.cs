using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// An overlay inside the owner window keeps production and headless input on the same route.
internal sealed class OwnerConsoleEpicDialog : FrameView
{
    private readonly OwnerConsoleScreenController _controller;
    private readonly OwnerEpicLoadSession _loads;
    private readonly Label _header = new() { Width = Dim.Fill(), Height = 1 };
    private readonly ListView _body = new() { Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2) };
    private readonly Label _hint = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 2 };
    private readonly Label _fieldsHint = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, Text = "F2 fields (snapshot)" };
    internal OwnerConsoleEpicViewModel? _model;
    private readonly OwnerConsoleListRenderer _source = new();
    private readonly OwnerConsoleLastGood<(OwnerConsoleEpicWindow, string?), OwnerConsoleEpicViewModel> _lastGood = new();
    private (OwnerConsoleEpicWindow, string?)? _subject;
    private string? _failure;
    internal string? _loadingDetailTitle;
    private bool _refreshing;
    private bool _disposed;
    private bool _formatting;
    internal int _scroll;
    internal int _selectedIndex;

    internal bool IsOpen { get; private set; }
    internal bool ShowingDetail { get; set; }
    internal OwnerConsoleEpicWindow TimeWindow { get; set; } = OwnerConsoleEpicWindow.Day;
    internal string? SelectedEpicId { get; set; }
    internal OwnerConsolePane ReturnPane { get; private set; }
    internal IReadOnlyList<string> Lines { get; private set; } = [];
    internal string HeaderText => OwnerConsoleEpicFormatter.Header(TimeWindow);
    internal OwnerConsoleEpicLoadState LoadState { get; private set; } = OwnerConsoleEpicLoadState.Loading;
    internal string HeaderLine => _header.Text.ToString();
    internal string HintText => _hint.Text.ToString();
    internal string FieldsHintText => _fieldsHint.Text;
    internal void FieldsUnavailable() => _fieldsHint.Text = "No selected structured item. F2 fields";
    private string HeaderWithStatus => HeaderText + " · " + (LoadState switch
    {
        OwnerConsoleEpicLoadState.Loading when _model is not null => "refreshing…",
        OwnerConsoleEpicLoadState.Loading => "loading…",
        OwnerConsoleEpicLoadState.Loaded when _refreshing => "refreshing…",
        OwnerConsoleEpicLoadState.Loaded => $"updated {_model!.LoadedAt:HH:mm:ss}",
        OwnerConsoleEpicLoadState.Failed when _model is not null => "failed · stale",
        OwnerConsoleEpicLoadState.Failed => "failed",
        _ => "unavailable"
    }) + (_model is not null && (LoadState != OwnerConsoleEpicLoadState.Loaded || _refreshing)
        ? $" · showing prior {(_model.SelectedDetail?.Epic.Title ?? "epic list")} / {OwnerConsoleEpicFormatter.Header(_model.Window)} · updated {_model.LoadedAt:HH:mm:ss}" : "")
        + (ShowingDetail && (LoadState == OwnerConsoleEpicLoadState.Loading || _refreshing || LoadState == OwnerConsoleEpicLoadState.Failed)
            ? $" · requested {_loadingDetailTitle ?? SelectedEpicId}" : "");
    private string HintWithRetry => ShowingDetail ? OwnerConsoleEpicFormatter.DetailHint(LoadState == OwnerConsoleEpicLoadState.Failed) :
        OwnerConsoleEpicFormatter.ListHint(LoadState == OwnerConsoleEpicLoadState.Failed);
    internal ListView BodyPane => _body;
    internal Task LastLoad => _loads.LastLoad;
    private IReadOnlyDictionary<int, OwnerConsoleFieldSnapshot> _fieldRows = new Dictionary<int, OwnerConsoleFieldSnapshot>();
    internal OwnerConsoleFieldSnapshot? SelectedFields
    {
        get
        {
            var snapshot = _model?.SelectedDetail is { } detail
                ? _fieldRows.GetValueOrDefault(_body.SelectedItem ?? _scroll) ?? detail.Fields()
                : _fieldRows.GetValueOrDefault(_body.SelectedItem ?? -1);
            return snapshot is null ? null : snapshot with { Fields = [.. snapshot.Fields,
                new("view/window", "Time window", _model is null ? null : OwnerConsoleEpicFormatter.WindowLabel(_model.Window)),
                new("view/loaded", "Loaded at", _model?.LoadedAt?.ToString("O"))] };
        }
    }

    internal OwnerConsoleEpicDialog(OwnerConsoleScreenController controller, Action<Action> invoke, CancellationToken token)
    {
        _controller = controller;
        _loads = new(controller.LoadEpicViewAsync, invoke, ApplyLoad, token);
        Title = "Epics";
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        Visible = false;
        Add(_header, _body, _hint, _fieldsHint);
        ViewportChanged += (_, _) => Format();
        _body.ViewportChanged += (_, _) => Format();
        _body.ValueChanged += (_, _) =>
        {
            if (_formatting || _body.SelectedItem is not { } row) return;
            if (ShowingDetail) { _scroll = row; return; }
            if (_fieldRows.TryGetValue(row, out var fields) && _model is { } model)
            {
                var index = model.Epics.ToList().FindIndex(item => item.Epic.Id == fields.Identity);
                if (index >= 0) { _selectedIndex = index; SelectedEpicId = fields.Identity; Format(); }
            }
        };
    }

    internal async Task OpenAsync(OwnerConsolePane returnPane, string? epicId = null)
    {
        if (_disposed || IsOpen) return;
        ReturnPane = returnPane;
        _loads.Open();
        IsOpen = true;
        ShowingDetail = epicId is not null;
        if (epicId is not null) SelectedEpicId = epicId;
        _loadingDetailTitle = null;
        _scroll = 0;
        BeginLoading();
        Visible = true;
        _body.SetFocus();
        _controller.ModelApplied += ModelApplied;
        await ReloadAsync();
    }

    internal Task<bool> HandleKeyAsync(Key key) => OwnerConsoleEpicDialogKeys.HandleAsync(this, key);

    private void ModelApplied() => _ = ReloadAsync();

    internal void BeginLoading()
    {
        var subject = (TimeWindow, ShowingDetail ? SelectedEpicId : null);
        _loads.Select(subject.TimeWindow, subject.Item2);
        if (_subject != subject)
        {
            if (_lastGood.TryGet(subject, out var prior)) _model = prior;
            _scroll = 0;
            _subject = subject;
        }
        LoadState = OwnerConsoleEpicLoadState.Loading;
        _failure = null;
        _refreshing = _model is not null;
        Format();
    }

    internal Task ReloadAsync()
    {
        if (_disposed || !IsOpen) return LastLoad;
        if (LoadState == OwnerConsoleEpicLoadState.Loaded)
        {
            _refreshing = true;
            Format();
        }
        return _loads.RefreshAsync();
    }

    private void ApplyLoad(OwnerEpicLoadResult result)
    {
        _failure = result.Failure;
        _refreshing = result.Refreshing;
        LoadState = result.State;
        if (result.Model is { } loaded)
        {
            _model = loaded;
            _lastGood.Remember((loaded.Window, loaded.SelectedDetail?.Epic.Id), loaded);
        }
        if (result.Model is { } model)
        {
            var index = Array.FindIndex(model.Epics.ToArray(), row => row.Epic.Id == SelectedEpicId);
            _selectedIndex = model.Epics.Count == 0 ? 0 : index >= 0 ? index : Math.Clamp(_selectedIndex, 0, model.Epics.Count - 1);
            SelectedEpicId = model.Epics.Count == 0 ? null : model.Epics[_selectedIndex].Epic.Id;
            if (ShowingDetail && model.SelectedDetail is null) ShowingDetail = false;
        }
        Format();
    }

    internal void Format(bool select = false)
    {
        if (_disposed || !IsOpen || _formatting) return;
        _formatting = true;
        try
        {
            var rendered = OwnerConsoleEpicDialogRenderer.Format(_header, _hint, _body, _source,
                HeaderWithStatus, HintWithRetry, _model, LoadState, ShowingDetail, _loadingDetailTitle,
                TimeWindow, _failure, SelectedEpicId, _selectedIndex, _scroll, select, _refreshing, Frame.Width);
            Lines = rendered.Lines;
            _fieldRows = rendered.FieldRows;
            _scroll = rendered.Scroll;
            if (Title != rendered.Title) Title = rendered.Title;
        }
        finally { _formatting = false; }
    }

    internal void Close()
    {
        _loads.Close();
        IsOpen = false;
        _model = null;
        _subject = null;
        _failure = null;
        _refreshing = false;
        Visible = false;
        _controller.ModelApplied -= ModelApplied;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loads.Dispose();
            Close();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
