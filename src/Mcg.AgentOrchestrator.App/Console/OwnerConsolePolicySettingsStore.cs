using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns the policy document snapshot and preserves unedited bytes during atomic saves.
internal sealed class OwnerConsolePolicySettingsStore
{
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip };
    private readonly string _path;
    private byte[] _snapshot = [];
    private string _text = "";
    private Encoding _encoding = new UTF8Encoding(false, true);
    private bool _hasPreamble;

    internal OwnerConsolePolicySettingsStore(string path) => _path = Path.GetFullPath(path);

    internal (JsonObject Stored, ConductorAutonomyPolicy Policy, bool Writable, string? Error) Load()
    {
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
            return (stored, policy, true, null);
        }
        catch (Exception ex) when (IsFileOrPolicyError(ex))
        {
            var error = File.Exists(_path) ? $"Policy unavailable: {ex.Message}" :
                "Policy file missing; showing Conservative defaults. Changes require an existing valid policy file.";
            return (stored, policy, false, error);
        }
    }

    internal bool Save(string key, string token, out string? cleanupError)
    {
        string? temporary = null;
        cleanupError = null;
        try
        {
            if (!File.ReadAllBytes(_path).AsSpan().SequenceEqual(_snapshot))
                throw new IOException("Policy changed on disk; cancel and reload before saving.");
            var candidate = ReplaceToken(key, token);
            _ = ConductorAutonomyPolicy.ParseJson(candidate, _path);
            if (candidate == _text) return false;
            var body = _encoding.GetBytes(candidate);
            var bytes = _hasPreamble ? _encoding.GetPreamble().Concat(body).ToArray() : body;
            temporary = Path.Combine(Path.GetDirectoryName(_path)!, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            if (!File.ReadAllBytes(_path).AsSpan().SequenceEqual(_snapshot))
                throw new IOException("Policy changed on disk; cancel and reload before saving.");
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanupError = ex.Message; }
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

    internal static bool IsFileOrPolicyError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        FormatException or JsonException or InvalidOperationException or ArgumentException;
}
