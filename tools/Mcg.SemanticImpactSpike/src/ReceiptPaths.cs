using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mcg.SemanticImpactSpike;

// Paths are execution inputs, but committed receipts must be portable evidence.
public sealed class ReceiptPaths(string repositoryRoot, string userHome)
{
    public string Serialize(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, QueryData.Json)!;
        Redact(node);
        return node.ToJsonString(QueryData.Json) + Environment.NewLine;
    }

    private void Redact(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[pair.Key] = Sanitize(text);
                else if (pair.Value is not null) Redact(pair.Value);
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    array[i] = Sanitize(text);
                else if (array[i] is { } child) Redact(child);
        }
    }

    private string Sanitize(string text)
    {
        // Match native, forward and already-escaped separators, including error text.
        foreach (var (path, label) in new[] { (repositoryRoot, "<repo>"), (userHome, "<user-home>") })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var parts = path.TrimEnd('/', '\\').Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var pattern = string.Join(@"[\\/]+", parts.Select(Regex.Escape)) + @"(?=$|[\\/]|[^\p{L}\p{N}_.-])";
            text = Regex.Replace(text, pattern, _ => label, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return text;
    }
}
