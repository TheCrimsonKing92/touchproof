namespace Mcg.AgentOrchestrator.Core;

/// <summary>An immutable, non-empty declaration of a goal's repository keys.</summary>
public sealed class GoalRepositorySet
{
    private readonly HashSet<string> keys = new(StringComparer.Ordinal);

    public GoalRepositorySet(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var suppliedKey in keys)
        {
            var key = suppliedKey is null ? null : NormalizeKey(suppliedKey);
            if (!IsCanonicalKey(key))
                throw new ArgumentException("Repository key must match [a-z0-9][a-z0-9._-]{0,63} after ASCII lower-casing.", nameof(keys));
            if (!this.keys.Add(key!))
                throw new ArgumentException("Repository keys must be unique after normalization.", nameof(keys));
        }

        if (this.keys.Count == 0)
            throw new ArgumentException("At least one repository is required.", nameof(keys));
        Keys = Array.AsReadOnly(this.keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<string> Keys { get; }

    public bool Contains(string? key)
    {
        if (key is null) return false;
        var normalized = NormalizeKey(key);
        return IsCanonicalKey(normalized) && keys.Contains(normalized);
    }

    public bool CoversExactly(RepositoryCandidateTuple? tuple) =>
        tuple is not null && Keys.SequenceEqual(tuple.Entries.Select(entry => entry.Key), StringComparer.Ordinal);

    public static bool IsCanonicalKey(string? key) =>
        key is { Length: >= 1 and <= 64 } && IsLetterOrDigit(key[0])
        && key.All(character => IsLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsLetterOrDigit(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';

    // Fold ASCII only: Unicode casing must not turn an invalid key into a valid one.
    private static string NormalizeKey(string key) =>
        new(key.Select(character => character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character).ToArray());
}
