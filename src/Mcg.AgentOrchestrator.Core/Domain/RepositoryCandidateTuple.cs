using System.Globalization;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>An immutable, non-empty set of repository revisions in canonical key order.</summary>
public sealed class RepositoryCandidateTuple : IEquatable<RepositoryCandidateTuple>
{
    private const string Prefix = "candidate-set:v1:";

    public RepositoryCandidateTuple(IEnumerable<KeyValuePair<string, CandidateIdentity>> entries)
        : this(NormalizeEntries(entries))
    {
    }

    // Both callers supply a private, validated, sorted snapshot.
    private RepositoryCandidateTuple(KeyValuePair<string, CandidateIdentity>[] entries)
    {
        Entries = Array.AsReadOnly(entries);
        Canonical = Prefix + string.Join(';', entries.Select(entry =>
            entry.Key + "=" + entry.Value.Canonical.Length.ToString(CultureInfo.InvariantCulture)
            + "#" + entry.Value.Canonical));
    }

    public IReadOnlyList<KeyValuePair<string, CandidateIdentity>> Entries { get; }
    public string Canonical { get; }

    public static bool AreSameCandidateSet(RepositoryCandidateTuple? left, RepositoryCandidateTuple? right) =>
        left is not null && right is not null && left.Equals(right);

    public static bool TryParse(string? value, out RepositoryCandidateTuple? tuple)
    {
        tuple = null;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal) || value.Length == Prefix.Length)
            return false;

        var entries = new List<KeyValuePair<string, CandidateIdentity>>();
        var position = Prefix.Length;
        string? previousKey = null;
        while (position < value.Length)
        {
            var equals = value.IndexOf('=', position);
            if (equals < 0)
                return false;
            var key = value[position..equals];
            if (!IsCanonicalKey(key) || (previousKey is not null && string.CompareOrdinal(previousKey, key) >= 0))
                return false;

            var hash = value.IndexOf('#', equals + 1);
            if (hash < 0)
                return false;
            var lengthText = value.AsSpan(equals + 1, hash - equals - 1);
            if (lengthText.IsEmpty || lengthText[0] == '0'
                || !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length <= 0 || length > value.Length - hash - 1)
                return false;

            var identityText = value.Substring(hash + 1, length);
            if (!CandidateIdentity.TryParse(identityText, out var identity) || identity is null
                || !string.Equals(identity.Canonical, identityText, StringComparison.Ordinal))
                return false;

            entries.Add(new(key, identity));
            previousKey = key;
            position = hash + 1 + length;
            if (position == value.Length)
                break;
            if (value[position] != ';' || ++position == value.Length)
                return false;
        }

        var parsedTuple = new RepositoryCandidateTuple(entries.ToArray());
        if (!string.Equals(value, parsedTuple.Canonical, StringComparison.Ordinal))
            return false;
        tuple = parsedTuple;
        return true;
    }

    public bool Equals(RepositoryCandidateTuple? other) =>
        other is not null && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is RepositoryCandidateTuple other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical);
    public override string ToString() => Canonical;

    private static KeyValuePair<string, CandidateIdentity>[] NormalizeEntries(
        IEnumerable<KeyValuePair<string, CandidateIdentity>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var snapshot = new List<KeyValuePair<string, CandidateIdentity>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = entry.Key?.Trim().ToLowerInvariant();
            if (key is null || !IsCanonicalKey(key))
                throw new ArgumentException("Repository key must match [a-z0-9][a-z0-9._-]{0,63}.", nameof(entries));
            if (!keys.Add(key))
                throw new ArgumentException("Repository keys must be unique after normalization.", nameof(entries));
            if (entry.Value is null || !CandidateIdentity.TryParse(entry.Value.Canonical, out var parsed)
                || !CandidateIdentity.AreSameCandidate(entry.Value, parsed))
                throw new ArgumentException("Repository identity must round-trip its canonical representation.", nameof(entries));
            snapshot.Add(new(key, entry.Value));
        }

        if (snapshot.Count == 0)
            throw new ArgumentException("At least one repository is required.", nameof(entries));
        return snapshot.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
    }

    private static bool IsCanonicalKey(string key) =>
        key.Length is >= 1 and <= 64 && IsLetterOrDigit(key[0])
        && key.All(character => IsLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsLetterOrDigit(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
