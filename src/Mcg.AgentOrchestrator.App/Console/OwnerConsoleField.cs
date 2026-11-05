using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Null means unavailable; an available empty string is a real value.
internal sealed record OwnerConsoleField(string Key, string Label, string? Value)
{
    internal bool Available => Value is not null;
}

internal sealed record OwnerConsoleFieldSnapshot(string Identity, ImmutableArray<OwnerConsoleField> Fields)
{
    internal static OwnerConsoleFieldSnapshot Create(string identity, params OwnerConsoleField[] fields) =>
        new(identity, [.. fields]);
}

// Maps source lines while the producer renders them, before wrapping/fitting.
internal sealed record OwnerConsoleStructuredText(OwnerConsoleFieldSnapshot? Detail,
    IReadOnlyDictionary<int, OwnerConsoleFieldSnapshot>? Rows = null,
    IReadOnlyDictionary<int, string>? FieldKeys = null, int ContinuationIndent = 0, string? FooterHint = null)
{
    internal OwnerConsoleFieldSnapshot? At(int line) => Rows?.GetValueOrDefault(line) ?? Detail;
}
