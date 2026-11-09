using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;
using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsolePolicySettingsTab : IOwnerConsoleSettingsTab
{
    private static readonly HashSet<string> Editable = new(StringComparer.Ordinal)
    {
        "maxConcurrentPaidWorkers", "acceptanceWidth", "acceptanceAttemptBelowNormalPriority",
        "acceptanceChildrenOptOutExecutionThrottling", "buildGcConserveMemory", "buildGcConserveMemoryLevel",
        "buildGcHeapCount", "buildRootRetentionDays", "maxTotalBudget", "maxCriterionRetries",
        "plannerSampleCount", "cascadeTesterCheapFirst", "autoPromoteRiskThreshold", "boardFillMaxDraftsPerDay"
    };
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip };
    private readonly string _path;
    private readonly List<string> _keys = [];
    private readonly Dictionary<string, JsonElement> _effective = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _defaults = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _lines = [];
    private byte[] _snapshot = [];
    private string _text = "";
    private Encoding _encoding = new UTF8Encoding(false, true);
    private bool _hasPreamble;
    private bool _writable;
    private string? _editingKey;
    private string _input = "";

    internal OwnerConsolePolicySettingsTab(string path) { _path = Path.GetFullPath(path); Refresh(); }
    public string Title => "Conductor policy";
    public bool Editing => _editingKey is not null;
    public int SelectedLine { get; private set; }
    internal string Message { get; private set; } = "";
    public string StatusText => Editing ? $"{_editingKey}: {_input}_  {Message}" : Message;
    public string KeyHint => Editing ? "Enter save  Esc cancel  Backspace edit" :
        "Up/Down highlight  Enter edit  r reload  Left/Right tab  Esc/q back";
    public IReadOnlyList<string> Lines { get { Refresh(); return _lines; } }

    public void Refresh()
    {
        if (Editing) return; // Keep the edit's snapshot for stale-file detection.
        _writable = false;
        var stored = new JsonObject();
        var policy = ConductorAutonomyPolicy.Default;
        try
        {
            _snapshot = File.ReadAllBytes(_path);
            using var reader = new StreamReader(new MemoryStream(_snapshot), new UTF8Encoding(false, true), true);
            _text = reader.ReadToEnd();
            _encoding = reader.CurrentEncoding;
            _hasPreamble = _encoding.GetPreamble().Length > 0 && _snapshot.AsSpan().StartsWith(_encoding.GetPreamble());
            policy = ConductorAutonomyPolicy.ParseJson(_text, _path);
            // JsonDocument accepts duplicate members; preserve them for the token writer to refuse explicitly.
            using var document = JsonDocument.Parse(_text, JsonOptions);
            foreach (var member in document.RootElement.EnumerateObject())
                stored[member.Name] = JsonNode.Parse(member.Value.GetRawText(), documentOptions: JsonOptions);
            _writable = true;
        }
        catch (Exception ex) when (IsFileOrPolicyError(ex))
        {
            Message = File.Exists(_path) ? $"Policy unavailable: {ex.Message}" :
                "Policy file missing; showing Conservative defaults. Changes require an existing valid policy file.";
        }
        LoadValues(policy);
        _keys.Clear();
        _keys.AddRange(stored.Select(pair => pair.Key).Concat(_effective.Keys)
            .Concat(["maxTotalBudget", "perProviderBudgetCaps"]).Distinct(StringComparer.Ordinal));
        _lines = _keys.Select(key =>
        {
            var present = stored.TryGetPropertyValue(key, out var node);
            var current = present ? node?.ToJsonString() ?? "null" :
                _effective.TryGetValue(key, out var value) ? value.GetRawText() + " (absent)" : "absent";
            var fallback = present && _effective.TryGetValue(key, out var effective) &&
                !JsonNode.DeepEquals(node, JsonNode.Parse(effective.GetRawText())) ? $"; effective {effective.GetRawText()}" : "";
            var access = Editable.Contains(key) ? "" : "; read-only: changes only from the command line or by the owner";
            var retired = key is "maxTotalBudget" or "perProviderBudgetCaps" ? "; not read by the conductor" : "";
            return $"{key}: {current} (default {DefaultValue(key)}{fallback}{access}{retired})";
        }).ToArray();
        SelectedLine = Math.Clamp(SelectedLine, 0, _keys.Count - 1);
    }

    private void LoadValues(ConductorAutonomyPolicy policy)
    {
        using var known = JsonDocument.Parse(policy.ToJson());
        _effective.Clear();
        foreach (var member in known.RootElement.EnumerateObject()) _effective[member.Name] = member.Value.Clone();
        // Only required fields are supplied. Optional defaults are resolved by the loader itself.
        var probe = new JsonObject();
        foreach (var key in new[] { "name", "maxConcurrentPaidWorkers", "transitionMap" })
            probe[key] = JsonNode.Parse(_effective[key].GetRawText());
        using var defaults = JsonDocument.Parse(ConductorAutonomyPolicy.ParseJson(probe.ToJsonString()).ToJson());
        _defaults.Clear();
        foreach (var member in defaults.RootElement.EnumerateObject())
            if (!probe.ContainsKey(member.Name)) _defaults[member.Name] = member.Value.Clone();
    }

    internal string DefaultValue(string key) => _defaults.TryGetValue(key, out var value) ? value.GetRawText() : "no loader default";

    public void HandleKey(Key key)
    {
        if (Editing)
        {
            if (key == Key.Esc) { _editingKey = null; Message = "Edit cancelled."; }
            else if (key == Key.Enter) Save();
            else if (key == Key.Backspace) { if (_input.Length > 0) _input = _input[..^1]; }
            else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value is >= 32 and <= 126 && _input.Length < 128)
                _input += (char)key.AsRune.Value;
            return;
        }
        Refresh();
        if (key == Key.CursorUp) SelectedLine = Math.Max(0, SelectedLine - 1);
        else if (key == Key.CursorDown) SelectedLine = Math.Min(_keys.Count - 1, SelectedLine + 1);
        else if (OwnerConsolePaneNavigator.Target(key, SelectedLine, _keys.Count, 10) is { } target) SelectedLine = target;
        else if (key == Key.Enter)
        {
            var selected = _keys[SelectedLine];
            if (!Editable.Contains(selected)) Message = "Read-only: changes only from the command line or by the owner.";
            else if (_writable) { _editingKey = selected; _input = ""; Message = ""; }
        }
    }

    private string ParseInput(string key)
    {
        var input = _input.Trim();
        if (key == "autoPromoteRiskThreshold")
        {
            if (input == "null") return input;
            var name = Enum.GetNames<ChangeRiskTier>().FirstOrDefault(candidate => candidate.Equals(input, StringComparison.OrdinalIgnoreCase));
            return name is not null ? JsonSerializer.Serialize(name) : throw new FormatException(
                $"{key}: valid values are {string.Join(", ", Enum.GetNames<ChangeRiskTier>())}, null.");
        }
        using var document = JsonDocument.Parse(input);
        var value = document.RootElement;
        if (key == "maxTotalBudget")
        {
            if (value.ValueKind == JsonValueKind.Number && decimal.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return input;
            throw new FormatException($"{key} must be a decimal number.");
        }
        if (_effective[key].ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return input;
            throw new FormatException($"{key} must be a boolean (true or false).");
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)) return input;
        throw new FormatException($"{key} must be an integer.");
    }

    private void Save()
    {
        string? temporary = null;
        try
        {
            var key = _editingKey!;
            if (!Editable.Contains(key)) throw new InvalidOperationException("Read-only policy key.");
            var token = ParseInput(key);
            if (!File.ReadAllBytes(_path).AsSpan().SequenceEqual(_snapshot))
                throw new IOException("Policy changed on disk; cancel and reload before saving.");
            var candidate = ReplaceToken(key, token);
            _ = ConductorAutonomyPolicy.ParseJson(candidate, _path);
            if (candidate == _text) { _editingKey = null; Message = "No changes to save."; return; }
            var body = _encoding.GetBytes(candidate);
            var bytes = _hasPreamble ? _encoding.GetPreamble().Concat(body).ToArray() : body;
            temporary = Path.Combine(Path.GetDirectoryName(_path)!, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            if (!File.ReadAllBytes(_path).AsSpan().SequenceEqual(_snapshot))
                throw new IOException("Policy changed on disk; cancel and reload before saving.");
            File.Move(temporary, _path, overwrite: true);
            _editingKey = null;
            Message = $"saved {key}. " + (key == "maxTotalBudget" ? "Not read by the conductor." :
                (key.StartsWith("build", StringComparison.Ordinal) || key == "acceptanceChildrenOptOutExecutionThrottling"
                    ? "Applies on next use." : "File-sourced conductors apply this on the next tick.") +
                " In-flight attempts keep their policy snapshot; running builds keep their environment." +
                " Conductors started with --policy ignore file policy; relaunch without --policy to use it.");
            Refresh();
        }
        catch (Exception ex) when (IsFileOrPolicyError(ex))
        { Message = $"Save refused: {_editingKey}: {ex.Message.ReplaceLineEndings(" ")}"; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message += $" Temporary cleanup failed: {ex.Message}"; }
        }
    }

    private string ReplaceToken(string key, string token)
    {
        var bytes = Encoding.UTF8.GetBytes(_text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        var matches = new List<(int Start, int End)>();
        var lastProperty = 0;
        var lastEnd = 1;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            lastProperty = (int)reader.TokenStartIndex;
            var name = reader.GetString();
            reader.Read();
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            lastEnd = (int)reader.BytesConsumed;
            if (name == key) matches.Add((start, lastEnd));
        }
        if (matches.Count > 1) throw new FormatException($"{key} occurs more than once; resolve duplicate keys before editing.");
        if (matches.Count == 1)
        {
            var (start, end) = matches[0];
            using var previous = JsonDocument.Parse(bytes.AsMemory(start, end - start), JsonOptions);
            using var proposed = JsonDocument.Parse(token);
            if (JsonElement.DeepEquals(previous.RootElement, proposed.RootElement)) return _text;
            return Splice(bytes, start, end, token);
        }
        var prefix = Encoding.UTF8.GetString(bytes.AsSpan(0, lastProperty));
        var lineStart = prefix.LastIndexOf('\n') + 1;
        var indent = new string(prefix[lineStart..].TakeWhile(character => character is ' ' or '\t').ToArray());
        var separator = prefix.Contains('\n') ? (_text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n") + indent : " ";
        return Splice(bytes, lastEnd, lastEnd, "," + separator + JsonSerializer.Serialize(key) + ": " + token);
    }

    private static string Splice(byte[] bytes, int start, int end, string token) =>
        Encoding.UTF8.GetString(bytes.AsSpan(0, start)) + token + Encoding.UTF8.GetString(bytes.AsSpan(end));

    private static bool IsFileOrPolicyError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        FormatException or JsonException or InvalidOperationException or ArgumentException;
}
