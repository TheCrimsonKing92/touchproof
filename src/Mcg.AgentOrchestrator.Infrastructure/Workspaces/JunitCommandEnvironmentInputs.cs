using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// An immutable input to one declared command, never a process-wide environment or a probe contract.
internal sealed class JunitCommandEnvironmentInputs
{
    internal static StringComparer NameComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private JunitCommandEnvironmentInputs(IReadOnlyDictionary<string, string> values) =>
        Overrides = new ReadOnlyDictionary<string, string?>(values.ToDictionary(pair => pair.Key,
            pair => (string?)pair.Value, NameComparer));

    internal IReadOnlyDictionary<string, string?> Overrides { get; }

    internal static void ValidateCheck(GoalAcceptanceVerifier.AcceptanceManifestCheck check)
    {
        if (check.Environment is not null && !check.Type.Equals("junit-command", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The environment field is supported only on junit-command checks.");
    }

    internal static JunitCommandEnvironmentInputs? FromCheck(GoalAcceptanceVerifier.AcceptanceManifestCheck check)
    {
        check.ValidateEnvironment();
        return check.Environment is { Count: > 0 } values ? new(values) : null;
    }

    internal void Validate(IReadOnlyList<DeclaredToolHome>? toolHomes)
    {
        foreach (var pair in Overrides)
            AcceptanceHermeticEnvironment.ValidateCommandInputName(pair.Key, toolHomes);
    }

    internal static IReadOnlyDictionary<string, string> Snapshot(IEnumerable<KeyValuePair<string, string>>? values)
    {
        if (values is null) throw new ArgumentException("The environment field must be an object of literal strings.");
        var copy = new Dictionary<string, string>(NameComparer);
        foreach (var pair in values)
        {
            AcceptanceHermeticEnvironment.ValidateCommandInputName(pair.Key);
            if (pair.Value is null || pair.Value.Contains('\0'))
                throw new ArgumentException("Environment values must be strings without embedded NULs.");
            if (!copy.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("The environment field contains duplicate variable names.");
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }

    internal static string Fingerprint(GoalAcceptanceVerifier.AcceptanceManifestCheck check)
    {
        check.ValidateEnvironment();
        var canonical = (check.Environment ?? new Dictionary<string, string>())
            .Select(pair => new { Name = OperatingSystem.IsWindows() ? pair.Key.ToUpperInvariant() : pair.Key, Value = pair.Value })
            .OrderBy(pair => pair.Name, StringComparer.Ordinal).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }
}

internal sealed class JunitCommandEnvironmentConverter : JsonConverter<IReadOnlyDictionary<string, string>>
{
    public JunitCommandEnvironmentConverter() { }

    public override bool HandleNull => true;

    public override IReadOnlyDictionary<string, string> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The environment field must be an object of literal strings.");
        var entries = new List<KeyValuePair<string, string>>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new JsonException("Environment values must be literal strings.");
            entries.Add(new(property.Name, property.Value.GetString()!));
        }
        try { return JunitCommandEnvironmentInputs.Snapshot(entries); }
        catch (ArgumentException) { throw new JsonException("Invalid, duplicate or protected environment variable input."); }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var pair in value) writer.WriteString(pair.Key, pair.Value);
        writer.WriteEndObject();
    }
}
