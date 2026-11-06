using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Committed inputs to test processes, never permission to inherit conductor environment.
internal static class AcceptanceDeclaredTestSettings
{
    internal static ImmutableSortedDictionary<string, string> Empty =>
        ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> Read(JsonElement root,
        string propertyName = "testSettings", string description = "Acceptance manifest testSettings")
    {
        if (!root.TryGetProperty(propertyName, out var element)) return Empty;
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{description} must be an object.");
        var settings = Empty.ToBuilder();
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            if (!Regex.IsMatch(name, "\\AMCG_TEST_[A-Z0-9_]+\\z", RegexOptions.CultureInvariant) ||
                name.StartsWith("MCG_TEST_INPUT_", StringComparison.Ordinal) ||
                name is LaneSlotTimingReader.VariableName or "MCG_TEST_BUILD_OUTPUT_ROOT" or
                    "MCG_TEST_ONLY_ACCEPTANCE_LEGACY_START_THEN_ATTACH")
                throw new InvalidDataException($"{description} name '{name}' is invalid or reserved.");
            if (settings.ContainsKey(name))
                throw new InvalidDataException($"{description} name '{name}' is duplicated.");
            if (property.Value.ValueKind != JsonValueKind.String ||
                property.Value.GetString() is not { Length: > 0 } value || value.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw new InvalidDataException($"{description} value for '{name}' must be a non-empty string without NUL, CR or LF.");
            settings.Add(name, value);
        }
        return settings.ToImmutable();
    }

    internal static void Apply(IDictionary<string, string?> environment, IReadOnlyDictionary<string, string>? settings)
    {
        foreach (var pair in settings ?? Empty) environment[pair.Key] = pair.Value;
    }

    internal static bool Equal(IReadOnlyDictionary<string, string>? expected, IReadOnlyDictionary<string, string>? observed) =>
        (expected?.Count ?? 0) == (observed?.Count ?? 0) && (expected ?? Empty).All(pair =>
            (observed ?? Empty).Any(other => other.Key.Equals(pair.Key, StringComparison.Ordinal) &&
                other.Value.Equals(pair.Value, StringComparison.Ordinal)));
}
