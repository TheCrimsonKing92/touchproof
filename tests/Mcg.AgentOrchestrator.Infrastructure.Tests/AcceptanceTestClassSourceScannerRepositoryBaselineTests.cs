using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: immutable source snapshot and manifest reads; no test hosts or shared mutations.
public sealed class AcceptanceTestClassSourceScannerRepositoryBaselineTests
{
    [Fact]
    public void RepositoryScan_DerivedSshFact_PreservesBaselineMetadataAndOwners()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var files = LaneTestSourceRootResolver.SourceFiles(root, LaneTestSourceRootResolver.Resolve(root));
        Assert.NotEmpty(files);
        var baseline = LegacyScan(files.Select(path => snapshot.GetSyntaxTree(path).GetRoot()).ToArray());
        var actual = AcceptanceTestClassSourceScanner.ScanSourceTexts(files.Select(snapshot.GetText));
        Assert.NotEmpty(baseline);
        var byName = actual.ToDictionary(item => item.FullName, StringComparer.Ordinal);
        foreach (var previous in baseline)
        {
            Assert.True(byName.TryGetValue(previous.FullName, out var retained), previous.FullName);
            Assert.Equal(previous, retained);
        }

        const string sshClass = "SshRemoteLaneExecutorRealTransportTests";
        // A later addition marked only by a derived attribute must deliberately extend this expectation.
        var added = actual.ExceptBy(baseline.Select(item => item.FullName), item => item.FullName).ToArray();
        Assert.Equal(new AcceptanceTestClassDescriptor(sshClass, null), Assert.Single(added));

        var settings = AcceptanceGateEngineSettings.Load(root);
        var rawBefore = AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, baseline);
        var rawAfter = AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, actual);
        Assert.Empty(TestCollectionGuardRules.FindLaneViolations(settings.InfrastructureTestLanes, actual));
        Assert.Equal("Remainder", Assert.Single(AcceptanceLaneMembership.LanesIncluding(rawAfter, sshClass)).Name);
        foreach (var previous in baseline)
            Assert.Equal(Owner(rawBefore, previous.FullName), Owner(rawAfter, previous.FullName));

        var poolSettings = Assert.IsType<AcceptanceBalancedLanePoolSettings>(settings.BalancedLanePool);
        var before = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, baseline,
            new Dictionary<string, double>(), poolSettings);
        var after = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, actual,
            new Dictionary<string, double>(), poolSettings);
        Assert.StartsWith(poolSettings.LaneNamePrefix + " ", Owner(after.Lanes, sshClass), StringComparison.Ordinal);
        var beforePool = before.Layout!.Lanes.SelectMany(lane => lane.Members).ToHashSet(StringComparer.Ordinal);
        var afterPool = after.Layout!.Lanes.SelectMany(lane => lane.Members).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(beforePool);
        Assert.Equal(beforePool.Append(sshClass).Order(StringComparer.Ordinal), afterPool.Order(StringComparer.Ordinal));
        foreach (var previous in baseline)
        {
            // Pool packing can change lane numbers; membership is its stable ownership contract.
            var afterOwner = Owner(after.Lanes, previous.FullName);
            if (beforePool.Contains(previous.FullName))
                Assert.StartsWith(poolSettings.LaneNamePrefix + " ", afterOwner, StringComparison.Ordinal);
            else
                Assert.Equal(Owner(before.Lanes, previous.FullName), afterOwner);
        }
    }

    private static string Owner(IReadOnlyList<AcceptanceTestLane> lanes, string name) =>
        Assert.Single(AcceptanceLaneMembership.LanesIncluding(lanes, name)).Name;

    // Independent oracle of b5bedfa16's exact-name membership and collection rules. It never
    // consults the new ancestry resolver, so omitting derived markers cannot satisfy the delta.
    private static AcceptanceTestClassDescriptor[] LegacyScan(IReadOnlyList<SyntaxNode> roots)
    {
        var nodes = roots.SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>()).ToArray();
        var constants = nodes.SelectMany(node => node.Members.OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword) && field.Declaration.Type.ToString() == "string")
                .SelectMany(field => field.Declaration.Variables)
                .Where(variable => variable.Initializer?.Value is LiteralExpressionSyntax)
                .Select(variable => (Key: node.Identifier.ValueText + "." + variable.Identifier.ValueText,
                    Value: ((LiteralExpressionSyntax)variable.Initializer!.Value).Token.ValueText)))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Value)
                .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var classes = nodes.Select(node => new BaselineClass(
                AcceptanceTestClassSourceScanner.NameOf(node), node.Identifier.ValueText,
                node.Modifiers.Any(SyntaxKind.AbstractKeyword),
                node.BaseList?.Types.FirstOrDefault()?.Type.ToString().Split('.').Last(),
                node.Members.OfType<MethodDeclarationSyntax>().Any(method => method.AttributeLists
                    .SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString().Split('.').Last()
                        is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute")),
                CollectionOf(node, constants)))
            .GroupBy(item => item.FullName, StringComparer.Ordinal)
            .Select(group => group.Aggregate((left, right) => left with
            {
                Abstract = left.Abstract || right.Abstract,
                HasFacts = left.HasFacts || right.HasFacts,
                BaseName = left.BaseName ?? right.BaseName,
                Collection = left.Collection ?? right.Collection
            })).ToArray();
        var byShortName = classes.GroupBy(item => item.ShortName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        bool HasFacts(BaselineClass item, HashSet<string> visited) => visited.Add(item.FullName) &&
            (item.HasFacts || item.BaseName is not null && byShortName.TryGetValue(item.BaseName, out var parents) &&
                parents.Any(parent => HasFacts(parent, visited)));
        string? Collection(BaselineClass item, HashSet<string> visited)
        {
            if (item.Collection is not null || !visited.Add(item.FullName)) return item.Collection;
            return item.BaseName is not null && byShortName.TryGetValue(item.BaseName, out var parents)
                ? parents.Select(parent => Collection(parent, visited)).FirstOrDefault(value => value is not null) : null;
        }
        return classes.Where(item => !item.Abstract && HasFacts(item, []))
            .Select(item => new AcceptanceTestClassDescriptor(item.FullName, Collection(item, [])))
            .OrderBy(item => item.FullName, StringComparer.Ordinal).ToArray();
    }

    private static string? CollectionOf(ClassDeclarationSyntax node, IReadOnlyDictionary<string, string[]> constants)
    {
        var attribute = node.AttributeLists.SelectMany(list => list.Attributes).FirstOrDefault(item =>
            item.Name.ToString().Split('.').Last() is "Collection" or "CollectionAttribute");
        if (attribute is null) return null;
        var expression = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText;
            case InvocationExpressionSyntax invocation when invocation.Expression.ToString() == "nameof":
                return invocation.ArgumentList.Arguments.First().Expression.ToString().Split('.').Last();
            case MemberAccessExpressionSyntax member when constants.TryGetValue(member.ToString(), out var values):
                return values.Length == 1 ? values[0] : null;
            case IdentifierNameSyntax identifier:
                var matches = constants.Where(item => item.Key.EndsWith("." + identifier.Identifier.ValueText,
                    StringComparison.Ordinal)).ToArray();
                if (matches.Any(item => item.Value.Length != 1)) return null;
                var unique = matches.Select(item => item.Value[0]).Distinct(StringComparer.Ordinal).ToArray();
                if (unique.Length == 1) return unique[0];
                break;
        }
        throw new InvalidDataException($"Baseline collection cannot be resolved on {node.Identifier.ValueText}.");
    }

    private sealed record BaselineClass(string FullName, string ShortName, bool Abstract, string? BaseName,
        bool HasFacts, string? Collection);
}
