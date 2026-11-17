namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceExclusiveResourceDescriptions
{
    internal static void Validate(IReadOnlyDictionary<string, string?>? descriptions)
    {
        if (descriptions is null)
            return;

        foreach (var (key, description) in descriptions)
        {
            if (string.IsNullOrWhiteSpace(description))
                throw new InvalidDataException(
                    $"Acceptance manifest engine exclusiveResourceDescriptions entry '{key.Trim()}' must have a non-empty description.");
        }
    }

    internal static string? Find(IReadOnlyDictionary<string, string?>? descriptions, string resourceKey)
    {
        return descriptions?.FirstOrDefault(entry =>
            entry.Key.Trim().Equals(resourceKey.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(entry.Value)).Value;
    }
}
