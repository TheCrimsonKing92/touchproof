using System.Text.Json;
using System.Text.Json.Nodes;
using Terminal.Gui.Input;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleAgentsSettingsStore;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleAgentsSettingsTab(string agentsPath, string modelFunctionsPath, string workersPath,
    Action<string, string>? replaceFile = null)
    : IOwnerConsoleSettingsTab
{
    private readonly OwnerConsoleAgentsSettingsStore _store = new(workersPath, replaceFile);
    private readonly List<Row> _rows = [];
    private readonly List<Field> _fields = [];
    private string[] _profiles = [];
    private string _profileError = "";
    private Row? _editing;
    private int _field;
    private bool _loaded;
    private string _message = "";
    public string Title => "Agents & models";
    public bool Editing => _editing is not null;
    public int SelectedLine { get; private set; }
    public string StatusText => _message;
    public string KeyHint => Editing
        ? "Up/Down field  type/Backspace edit  Left/Right choices  Enter save  Esc discard  Ctrl+r discard/reload"
        : "Up/Down highlight  Enter edit  r reload  Left/Right tab  Esc/q back";
    public IReadOnlyList<string> Lines
    {
        get
        {
            Refresh();
            if (_editing is null) return _rows.Select(row => row.Text).ToArray();
            return new[] { _editing.Text }.Concat(_fields.Select(input =>
                $"{input.Label}: {(input.Value.Length == 0 ? "(null)" : input.Value)}" +
                (input.Choices.Length == 0 ? "" : $"  [choices: {string.Join(", ", input.Choices.Select(value => value.Length == 0 ? "null" : value))}]")))
                .Concat(["Enter saves all fields; Esc discards pending edits."]).ToArray();
        }
    }

    public void Refresh()
    {
        if (_loaded || Editing) return;
        _loaded = true;
        _rows.Clear();
        Load(agentsPath, "Agents", "Agent seats");
        Load(modelFunctionsPath, "Bindings", "Function bindings");
        LoadProfiles();
        SelectedLine = Math.Clamp(SelectedLine, 0, Math.Max(0, _rows.Count - 1));
    }

    private void Load(string path, string arrayName, string title)
    {
        _rows.Add(new(title));
        try
        {
            var catalog = _store.ReadCatalog(path);
            var root = catalog.Root;
            var records = root[arrayName] as JsonArray ?? throw new JsonException($"Missing {arrayName} array.");
            var loaded = new List<Row>();
            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index] as JsonObject ?? throw new JsonException($"Invalid {arrayName} record {index}.");
                var model = record["Model"] as JsonObject ?? throw new JsonException($"Missing Model at {arrayName}[{index}].");
                var subscription = record["Subscription"] as JsonObject;
                var primary = subscription ?? model;
                var nameKey = subscription is null ? "ModelName" : "ModelAlias";
                if (!primary.ContainsKey(nameKey) || !primary.ContainsKey("ReasoningEffort") ||
                    subscription is not null && !subscription.ContainsKey("WorkerProfileName"))
                    throw new JsonException($"Missing editable properties at {arrayName}[{index}].");
                var details = arrayName == "Agents"
                    ? $"{Text(record, "Name")} | role {Text(record, "Role")} | status {Text(record, "Status")}"
                    : $"{Text(record, "Purpose")} | lane {Text(record, "Lane")}";
                details += $" | provider {Text(model, "ProviderName")} | model {Text(model, "ModelName")}" +
                    $" | alias {Text(subscription, "ModelAlias")} | effort {Text(primary, "ReasoningEffort")}" +
                    $" | mode {Text(model, "SubscriptionMode")} | worker {Text(subscription, "WorkerProfileName")}";
                if (arrayName == "Bindings") details += $" | fallback {record["SubscriptionFallback"]?.ToJsonString() ?? "none"}";
                loaded.Add(new(details, path, arrayName, index, root, catalog.Bytes, catalog.Efforts));
            }
            _rows.AddRange(loaded);
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        { _rows.Add(new($"Cannot load {Path.GetFileName(path)}: {ex.Message} Fix the file and press r to reload.")); }
    }

    private void LoadProfiles()
    {
        try { _profiles = _store.ReadProfiles(); _profileError = ""; }
        catch (Exception ex) when (IsConfigurationError(ex))
        { _profiles = []; _profileError = $"Cannot load workers.json: {ex.Message}"; }
    }

    public void HandleKey(Key key)
    {
        Refresh();
        if (Editing)
        {
            if (key == Key.Esc) { EndEdit("Pending edits discarded."); return; }
            if (key == Key.R.WithCtrl)
            { EndEdit("Pending edits discarded; reloaded."); Reload(); return; }
            if (key == Key.Enter) { Save(); return; }
            if (key == Key.CursorUp) _field = Math.Max(0, _field - 1);
            else if (key == Key.CursorDown) _field = Math.Min(_fields.Count - 1, _field + 1);
            else
            {
                var field = _fields[_field];
                if (key == Key.CursorLeft || key == Key.CursorRight)
                {
                    if (field.Choices.Length > 0)
                    {
                        var current = Array.IndexOf(field.Choices, field.Value);
                        var direction = key == Key.CursorLeft ? -1 : 1;
                        field.Value = field.Choices[(current + direction + field.Choices.Length) % field.Choices.Length];
                    }
                }
                else if (key == Key.Backspace && field.Property != "Status")
                    field.Value = field.Value.Length == 0 ? "" : field.Value[..^1];
                else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value >= 32 && field.Property != "Status")
                    field.Value += key.AsRune.ToString();
            }
            SelectedLine = _field + 1;
            return;
        }
        if (!key.IsCtrl && !key.IsAlt && char.ToLowerInvariant((char)key.AsRune.Value) == 'r') Reload();
        else if (key == Key.CursorUp) SelectedLine = Math.Max(0, SelectedLine - 1);
        else if (key == Key.CursorDown) SelectedLine = Math.Min(_rows.Count - 1, SelectedLine + 1);
        else if (key == Key.Enter && _rows[SelectedLine].Path is not null) BeginEdit(_rows[SelectedLine]);
    }

    private void Reload() { _loaded = false; Refresh(); }

    private void BeginEdit(Row row)
    {
        try
        {
            if (!_store.IsCurrent(row.Path!, row.Bytes!))
            { _message = "Configuration changed on disk. Press r to reload and re-apply the edit."; return; }
            var record = row.Root![row.Array!]![row.Index]!.AsObject();
            var subscription = record["Subscription"] as JsonObject;
            var primary = subscription ?? record["Model"]!.AsObject();
            var block = subscription is null ? "Model" : "Subscription";
            var nameKey = subscription is null ? "ModelName" : "ModelAlias";
            _fields.Clear();
            _fields.Add(new(nameKey, block, nameKey, Text(primary, nameKey, ""), []));
            _fields.Add(new("Reasoning effort", block, "ReasoningEffort", Text(primary, "ReasoningEffort", ""), ["", .. row.Efforts!]));
            if (subscription is not null)
                _fields.Add(new("Worker profile", block, "WorkerProfileName", Text(subscription, "WorkerProfileName", ""), _profiles));
            if (row.Array == "Agents") _fields.Add(new("Status", "", "Status", Text(record, "Status", ""), ["Available", "Offline"]));
            _editing = row;
            _field = 0;
            SelectedLine = 1;
            _message = subscription is null ? "Worker profile disabled: this record has no Subscription block." : _profileError;
        }
        catch (Exception ex) when (IsConfigurationError(ex)) { _message = $"Cannot edit: {ex.Message}"; }
    }

    private void Save()
    {
        var row = _editing!;
        try
        {
            if (string.IsNullOrWhiteSpace(_fields[0].Value))
            { _message = "Model name or alias must be non-empty."; return; }
            if (_fields[1].Value.Length > 0 && !row.Efforts!.Contains(_fields[1].Value, StringComparer.Ordinal))
            { _message = "Reasoning effort must be an existing value from this file, or null (empty)."; return; }
            _profiles = _store.ReadProfiles(); // Revalidate names, never execute worker templates.
            var profile = _fields.Find(field => field.Property == "WorkerProfileName");
            if (profile is not null && !_profiles.Contains(profile.Value, StringComparer.Ordinal))
            { _message = "Worker profile must name a profile in workers.json. Reload to refresh choices."; return; }
            if (!_store.IsCurrent(row.Path!, row.Bytes!))
            { _message = "Configuration changed on disk. Discard and reload (Ctrl+r), then re-apply the edit."; return; }

            var root = row.Root!.DeepClone().AsObject();
            var record = root[row.Array!]![row.Index]!.AsObject();
            foreach (var field in _fields)
            {
                // In particular, preserve a pre-existing Busy status unless explicitly changed.
                if (field.Value == field.Original) continue;
                if (field.Property == "Status" && field.Value is not ("Available" or "Offline"))
                { _message = "Status must be Available or Offline."; return; }
                var block = field.Block.Length == 0 ? record : record[field.Block]!.AsObject();
                if (!block.ContainsKey(field.Property)) throw new JsonException($"Missing property {field.Property}.");
                block[field.Property] = field.Property == "ReasoningEffort" && field.Value.Length == 0 ? null : JsonValue.Create(field.Value);
            }
            var result = _store.Save(row.Path!, row.Bytes!, root);
            if (!result.Saved) { _message = result.Message; return; }
            EndEdit(result.Message);
            Reload();
        }
        catch (Exception ex) when (IsConfigurationError(ex)) { _message = $"Save failed: {ex.Message}"; }
    }

    private void EndEdit(string message)
    {
        if (_editing is { } row) SelectedLine = _rows.IndexOf(row);
        _editing = null;
        _fields.Clear();
        _message = message;
    }

    private static string Text(JsonNode? node, string key, string empty = "none") => node?[key]?.GetValue<string>() ?? empty;

    private sealed record Row(string Text, string? Path = null, string? Array = null, int Index = 0,
        JsonObject? Root = null, byte[]? Bytes = null, string[]? Efforts = null);
    private sealed record Field(string Label, string Block, string Property, string Original, string[] Choices)
    {
        public string Value { get; set; } = Original;
    }
}
