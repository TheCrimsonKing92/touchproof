using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>Immutable intake membership and checkout roots, independent of later registry edits.</summary>
public sealed class GoalRepositoryDeclaration
{
    public GoalRepositoryDeclaration(IReadOnlyList<string> keys, IReadOnlyDictionary<string, string> checkoutRoots,
        IReadOnlyList<string>? declaredOrder = null)
    {
        Repositories = new GoalRepositorySet(keys);
        if (Repositories.Keys.Count < 2 || !Repositories.Contains("default"))
            throw new ArgumentException("A repository declaration requires default and another repository.", nameof(keys));
        if (!Repositories.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(checkoutRoots.Keys))
            throw new ArgumentException("Checkout roots must match the declared keys.", nameof(checkoutRoots));
        Keys = Repositories.Keys;
        var order = (declaredOrder ?? keys).Select(key => key.ToLowerInvariant()).ToArray();
        if (order.Length != Keys.Count || !Keys.ToHashSet(StringComparer.Ordinal).SetEquals(order))
            throw new ArgumentException("Declared order must contain each repository exactly once.", nameof(declaredOrder));
        DeclaredOrder = Array.AsReadOnly(order);
        CheckoutRoots = new ReadOnlyDictionary<string, string>(Repositories.Keys.ToDictionary(
            key => key, key => Path.GetFullPath(checkoutRoots[key]), StringComparer.Ordinal));
    }

    public IReadOnlyList<string> Keys { get; }
    public IReadOnlyList<string> DeclaredOrder { get; }
    public IReadOnlyDictionary<string, string> CheckoutRoots { get; }
    [JsonIgnore]
    public GoalRepositorySet Repositories { get; }
}
