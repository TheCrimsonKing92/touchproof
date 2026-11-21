using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Conservative type and static-member names; semantic identity remains the scan's decision.
internal sealed class ImpactReplayChangedSupportNameFilter
{
    private readonly HashSet<string> _names;
    private readonly UsingDirectiveSyntax[] _aliases;

    internal ImpactReplayChangedSupportNameFilter(IEnumerable<INamedTypeSymbol> declared, IEnumerable<SyntaxTree> trees)
        : this(declared, ReadAliases(trees))
    {
    }

    internal ImpactReplayChangedSupportNameFilter(IEnumerable<INamedTypeSymbol> declared, UsingDirectiveSyntax[] aliases)
    {
        _names = new(StringComparer.Ordinal);
        _aliases = aliases;
        Widen(declared);
    }

    internal IReadOnlySet<string> Names => _names;

    internal static UsingDirectiveSyntax[] ReadAliases(IEnumerable<SyntaxTree> trees) =>
        trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
            .Where(directive => directive.Alias is not null).ToArray();

    private void Widen(IEnumerable<INamedTypeSymbol> types)
    {
        foreach (var type in types)
        {
            _names.Add(type.Name);
            foreach (var member in type.GetMembers().Where(member => member.IsStatic))
                _names.Add(member.Name);
        }
        bool added;
        do
        {
            added = false;
            foreach (var alias in _aliases)
            {
                if (alias.NamespaceOrType is { } target && target.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()
                    .Any(name => _names.Contains(name.Identifier.ValueText)))
                    added |= _names.Add(alias.Alias!.Name.Identifier.ValueText);
            }
        } while (added);
    }

    internal bool Matches(NameSyntax name)
    {
        var text = RightmostIdentifier(name);
        return _names.Contains(text) || IsAttributeName(name) && _names.Contains(text + "Attribute");
    }

    internal static string RightmostIdentifier(NameSyntax name) => name switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax qualified => qualified.Name.Identifier.ValueText,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    internal static bool IsAttributeName(NameSyntax name)
    {
        SyntaxNode current = name;
        while (current.Parent is NameSyntax parent &&
               (parent is QualifiedNameSyntax qualified && qualified.Right == current ||
                parent is AliasQualifiedNameSyntax alias && alias.Name == current))
            current = parent;
        return current.Parent is AttributeSyntax attribute && attribute.Name == current;
    }
}
