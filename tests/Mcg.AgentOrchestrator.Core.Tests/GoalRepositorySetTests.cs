using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class GoalRepositorySetTests
{
    [Fact]
    public void Constructor_EmptyOrNullInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GoalRepositorySet([]));
        Assert.Throws<ArgumentNullException>(() => new GoalRepositorySet(null!));
        Assert.Throws<ArgumentException>(() => new GoalRepositorySet([null!]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" alpha")]
    [InlineData("alpha ")]
    [InlineData("-a")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("\u212A")]
    [InlineData("\u0130")]
    [InlineData("é")]
    public void Constructor_InvalidKey_ThrowsWithoutTrimmingOrUnicodeFolding(string key)
    {
        Assert.Throws<ArgumentException>(() => new GoalRepositorySet([key]));
    }

    [Fact]
    public void Constructor_KeyLengthBoundary_Accepts64AndRejects65()
    {
        var longest = new string('a', 64);
        Assert.Equal(longest, Assert.Single(new GoalRepositorySet([longest]).Keys));
        Assert.Throws<ArgumentException>(() => new GoalRepositorySet([longest + "a"]));
    }

    [Fact]
    public void Constructor_NormalizationCollision_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GoalRepositorySet(["Alpha", "alpha"]));
        Assert.Throws<ArgumentException>(() => new GoalRepositorySet(["alpha", "alpha"]));
    }

    [Fact]
    public void Keys_UnsortedMixedCaseInput_ExposesImmutableOrdinalSnapshot()
    {
        var input = new List<string> { "beta", "Alpha_", "ALPHA", "alpha-", "alpha.", "0" };
        var repositories = new GoalRepositorySet(input);
        input.Clear();
        input.Add("gamma");

        Assert.Equal(new[] { "0", "alpha", "alpha-", "alpha.", "alpha_", "beta" }, repositories.Keys);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)repositories.Keys)[0] = "mutated");
        Assert.True(repositories.Contains("ALPHA"));
        Assert.False(repositories.Contains("gamma"));
    }

    [Theory]
    [InlineData("ALPHA", true)]
    [InlineData("alpha", true)]
    [InlineData("beta", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" alpha", false)]
    [InlineData("a b", false)]
    [InlineData("\u212A", false)]
    public void Contains_SuppliedKey_NormalizesAsciiAndRejectsInvalid(string? key, bool expected)
    {
        var repositories = new GoalRepositorySet(["Alpha", "k"]);
        Assert.Equal(expected, repositories.Contains(key));
    }

    [Fact]
    public void CoversExactly_TupleKeys_MustMatchWithoutMissingOrExtraKeys()
    {
        var repositories = new GoalRepositorySet(["Beta", "Alpha"]);
        Assert.True(repositories.CoversExactly(Tuple("beta", "alpha")));
        Assert.False(repositories.CoversExactly(Tuple("alpha")));
        Assert.False(repositories.CoversExactly(Tuple("alpha", "beta", "gamma")));
        Assert.False(repositories.CoversExactly(Tuple("alpha", "gamma")));
        Assert.False(repositories.CoversExactly(null));
    }

    private static RepositoryCandidateTuple Tuple(params string[] keys) =>
        new(keys.Select(key => new KeyValuePair<string, CandidateIdentity>(key, new("patch", "base", key))));

    [Fact]
    public void Declaration_JsonRoundTrip_PreservesDeclaredOrderSeparatelyFromKeys()
    {
        var order = new List<string> { "default", "zeta", "alpha" };
        var roots = order.ToDictionary(key => key, _ => Path.GetTempPath());
        var declaration = new GoalRepositoryDeclaration(order, roots);
        order.Clear();
        var restored = System.Text.Json.JsonSerializer.Deserialize<GoalRepositoryDeclaration>(
            System.Text.Json.JsonSerializer.Serialize(declaration))!;
        Assert.Equal(new[] { "default", "zeta", "alpha" }, restored.DeclaredOrder);
        Assert.Equal(new[] { "alpha", "default", "zeta" }, restored.Keys);
        Assert.Throws<ArgumentException>(() => new GoalRepositoryDeclaration(restored.Keys, roots,
            ["default", "alpha", "alpha"]));
    }
}
