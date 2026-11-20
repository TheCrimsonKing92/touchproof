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
}
