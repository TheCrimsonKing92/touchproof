using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleAgentsSettingsStore(string workersPath, Action<string, string>? replaceFile = null)
{
    internal Catalog ReadCatalog(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var root = Parse(bytes);
        var efforts = Efforts(root).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(root, bytes, efforts);
    }

    internal string[] ReadProfiles()
    {
        var root = Parse(File.ReadAllBytes(workersPath));
        var profiles = root["Profiles"] as JsonArray ?? throw new JsonException("Missing Profiles array.");
        return profiles.Select(profile => profile?["Name"]?.GetValue<string>() ??
                throw new JsonException("Worker profile is missing Name."))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal bool IsCurrent(string path, byte[] bytes) => File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);

    internal SaveResult Save(string path, byte[] bytes, JsonObject root)
    {
        string? temporary = null;
        var saved = false;
        var message = "";
        try
        {
            var original = Decode(bytes);
            var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var json = root.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, IndentSize = 2, NewLine = newline, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (original.EndsWith('\n')) json += newline;
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(HasBom(bytes)));
            if (replaceFile is null) File.Replace(temporary, path, null);
            else replaceFile(temporary, path);
            saved = true;
            message = "Saved. The conductor applies seat changes on its next relaunch.";
        }
        catch (Exception ex) when (IsConfigurationError(ex)) { message = $"Save failed: {ex.Message}"; }
        finally
        {
            if (temporary is not null)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (IsConfigurationError(ex)) { message += $" Temporary file cleanup failed: {ex.Message}"; }
            }
        }
        return new(saved, message);
    }

    internal static bool IsConfigurationError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException;

    private static IEnumerable<string> Efforts(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(pair => pair.Key == "ReasoningEffort" && pair.Value is not null
            ? new[] { pair.Value.GetValue<string>() } : Efforts(pair.Value)),
        JsonArray array => array.SelectMany(Efforts),
        _ => []
    };

    private static bool HasBom(byte[] bytes) => bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes.AsSpan(HasBom(bytes) ? 3 : 0));
    private static JsonObject Parse(byte[] bytes) => JsonNode.Parse(Decode(bytes)) as JsonObject ?? throw new JsonException("Expected a JSON object.");

    internal sealed record Catalog(JsonObject Root, byte[] Bytes, string[] Efforts);
    internal sealed record SaveResult(bool Saved, string Message);
}
