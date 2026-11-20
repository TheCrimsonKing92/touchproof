using System.Collections.Frozen;
using System.Collections.Immutable;

internal sealed record DependencyType(string AssemblyName, string Namespace, string Name, string FullName)
{
    public string OutermostName
    {
        get
        {
            var separator = FullName.IndexOf('+');
            return separator >= 0 ? FullName[..separator] : FullName;
        }
    }
}

internal sealed record DependencyMember(DependencyType DeclaringType, string Name, int MetadataToken);

internal sealed record DependencyMethod(DependencyMember Source, ImmutableArray<DependencyMember> Calls);

internal sealed record DependencyDeclaration(
    DependencyType Type,
    ImmutableArray<DependencyType> References,
    ImmutableArray<DependencyMethod> Methods)
{
    public string AssemblyName => Type.AssemblyName;
    public string Namespace => Type.Namespace;
    public string FullName => Type.FullName;
    public string OutermostName => Type.OutermostName;
}

internal sealed class DependencyIndex
{
    public DependencyIndex(ImmutableArray<DependencyDeclaration> types)
    {
        Types = types;
        DefinedTypes = types.Select(type => type.Type).ToFrozenSet();
    }

    public ImmutableArray<DependencyDeclaration> Types { get; }
    public FrozenSet<DependencyType> DefinedTypes { get; }

    public ImmutableArray<ImmutableArray<string>> NamespaceCycles(string assemblyName)
    {
        var graph = Types.Where(type => type.AssemblyName == assemblyName)
            .GroupBy(type => type.Namespace, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group
                .SelectMany(type => type.References)
                .Where(target => target.AssemblyName == assemblyName && target.Namespace != group.Key
                    && DefinedTypes.Contains(target))
                .Select(target => target.Namespace)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var incoming = graph.Keys.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (source, targets) in graph)
        {
            foreach (var target in targets)
                incoming[target].Add(source);
        }

        var cycles = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        foreach (var start in graph.Keys.Order(StringComparer.Ordinal))
        {
            // Each cycle starts at its least namespace. Prune vertices that cannot return to it.
            var canReturn = new HashSet<string>(StringComparer.Ordinal) { start };
            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.TryPop(out var current))
            {
                foreach (var previous in incoming[current])
                {
                    if (StringComparer.Ordinal.Compare(previous, start) > 0 && canReturn.Add(previous))
                        pending.Push(previous);
                }
            }

            Visit(start);

            void Visit(string current)
            {
                visited.Add(current);
                path.Add(current);
                foreach (var next in graph[current])
                {
                    if (next == start)
                        cycles.Add([.. path]);
                    else if (canReturn.Contains(next) && !visited.Contains(next))
                        Visit(next);
                }
                path.RemoveAt(path.Count - 1);
                visited.Remove(current);
            }
        }
        return cycles.ToImmutable();
    }
}
