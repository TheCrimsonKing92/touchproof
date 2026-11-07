using System.Globalization;
using Mcg.AgentOrchestrator.Core;

public sealed class RepositoryCandidateTupleTests
{
    private static readonly CandidateIdentity Alpha = new("a1", "b1", "c1");
    private static readonly CandidateIdentity Beta = new("a2", "b2", "c2");
    private const string Prefix = "candidate-set:v1:";

    [Fact]
    public void Canonical_ReversedInsertionOrder_PreservesValueAndEntryOrder()
    {
        var first = new RepositoryCandidateTuple([Pair("alpha", Alpha), Pair("beta", Beta)]);
        var reversed = new RepositoryCandidateTuple([Pair("beta", Beta), Pair("alpha", Alpha)]);

        Assert.Equal(Prefix + Frame("alpha", Alpha) + ";" + Frame("beta", Beta), first.Canonical);
        Assert.Equal(first.Canonical, reversed.Canonical);
        Assert.Equal(first.Entries.ToArray(), reversed.Entries.ToArray());
        Assert.True(RepositoryCandidateTuple.AreSameCandidateSet(first, reversed));
        Assert.Equal(first, reversed);
        Assert.Equal(first.GetHashCode(), reversed.GetHashCode());
    }

    [Theory]
    [InlineData("alpha", 0)]
    [InlineData("alpha", 1)]
    [InlineData("alpha", 2)]
    [InlineData("beta", 0)]
    [InlineData("beta", 1)]
    [InlineData("beta", 2)]
    public void Canonical_OneIdentityComponentChanges_ChangesCandidateSet(string key, int component)
    {
        var original = new RepositoryCandidateTuple([Pair("alpha", Alpha), Pair("beta", Beta)]);
        var identity = key == "alpha" ? Alpha : Beta;
        var changed = new CandidateIdentity(
            component == 0 ? "changed" : identity.PatchId,
            component == 1 ? "changed" : identity.BaseClosureHash,
            component == 2 ? "changed" : identity.ManifestIdentity);
        var revised = new RepositoryCandidateTuple([
            Pair("alpha", key == "alpha" ? changed : Alpha),
            Pair("beta", key == "beta" ? changed : Beta)]);

        Assert.NotEqual(original.Canonical, revised.Canonical);
        Assert.False(RepositoryCandidateTuple.AreSameCandidateSet(original, revised));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TryParse_CanonicalValues_RestoresEveryEntry(int count)
    {
        var entries = new[] {
            Pair("gamma", new CandidateIdentity("patch;=##", "base=;#", "manifest#=;😀")),
            Pair("alpha", Alpha), Pair("beta", Beta)
        }.Take(count).ToArray();
        var original = new RepositoryCandidateTuple(entries);

        Assert.True(RepositoryCandidateTuple.TryParse(original.Canonical, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray(), parsed.Entries.ToArray());
        Assert.Equal(original.Canonical, parsed.Canonical);
        Assert.True(RepositoryCandidateTuple.AreSameCandidateSet(original, parsed));
    }

    [Theory]
    [MemberData(nameof(MalformedValues))]
    public void TryParse_MalformedInput_ReturnsFalseAndClearsResult(string? value)
    {
        Assert.False(RepositoryCandidateTuple.TryParse(value, out var parsed));
        Assert.Null(parsed);
    }

    public static IEnumerable<object?[]> MalformedValues()
    {
        var alpha = Frame("alpha", Alpha);
        var beta = Frame("beta", Beta);
        yield return [null];
        yield return [""];
        yield return [Alpha.Canonical];
        yield return [Prefix];
        yield return ["candidate-set:v2:" + alpha];
        yield return [Prefix + alpha + ";"];
        yield return [Prefix + Frame("", Alpha)];
        yield return [Prefix + Frame(" ", Alpha)];
        yield return [Prefix + alpha + ";" + alpha];
        yield return [Prefix + alpha + ";" + Frame("ALPHA", Beta)];
        yield return [Prefix + beta + ";" + alpha];
        yield return [Prefix + Frame("Alpha", Alpha)];
        yield return [Prefix + Frame(" alpha", Alpha)];
        yield return [Prefix + Frame("a:b", Alpha)];
        yield return [Prefix + Frame("a=b", Alpha)];
        yield return [Prefix + Frame("a;b", Alpha)];
        yield return [Prefix + Frame("a#b", Alpha)];
        yield return [Prefix + Frame("répo", Alpha)];
        yield return [Prefix + Frame(new string('a', 65), Alpha)];
        yield return [Prefix + "alpha"];
        yield return [Prefix + "alpha=#" + Alpha.Canonical];
        yield return [Prefix + "alpha=01#" + Alpha.Canonical];
        yield return [Prefix + "alpha=0#"];
        yield return [Prefix + "alpha=-1#" + Alpha.Canonical];
        yield return [Prefix + "alpha=+1#" + Alpha.Canonical];
        yield return [Prefix + "alpha= 1#" + Alpha.Canonical];
        yield return [Prefix + "alpha=x#" + Alpha.Canonical];
        yield return [Prefix + "alpha=" + Alpha.Canonical.Length + "\0#" + Alpha.Canonical];
        yield return [Prefix + "alpha=2147483648#" + Alpha.Canonical];
        yield return [Prefix + "alpha=2147483647#" + Alpha.Canonical];
        yield return [Prefix + "alpha=1"];
        yield return [Prefix + "alpha=1#x"];
        yield return [Prefix + alpha[..^1]];
        yield return [Prefix + alpha + "trailing"];
        yield return [Prefix + alpha + ";trailing"];
        yield return [Prefix + "alpha=" + Alpha.Canonical.Length + "#candidate:v1:A1:B1:C1"];
    }

    [Fact]
    public void Constructor_EmptyOrNullCollection_RejectsInput()
    {
        Assert.Throws<ArgumentException>(() => new RepositoryCandidateTuple([]));
        Assert.Throws<ArgumentNullException>(() => new RepositoryCandidateTuple(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("a=b")]
    [InlineData("a;b")]
    [InlineData("a#b")]
    [InlineData("a:b")]
    [InlineData("répo")]
    [InlineData("_repo")]
    public void Constructor_InvalidKey_RejectsInput(string? key)
    {
        Assert.Throws<ArgumentException>(() => new RepositoryCandidateTuple([Pair(key!, Alpha)]));
    }

    [Fact]
    public void Constructor_DuplicateKeysIgnoringCase_RejectsInput()
    {
        Assert.Throws<ArgumentException>(() =>
            new RepositoryCandidateTuple([Pair("alpha", Alpha), Pair("ALPHA", Beta)]));
    }

    [Fact]
    public void Constructor_InvalidIdentity_RejectsInput()
    {
        Assert.Throws<ArgumentException>(() => new RepositoryCandidateTuple([Pair("alpha", null!)]));
        Assert.Throws<ArgumentException>(() => new RepositoryCandidateTuple([
            Pair("alpha", new CandidateIdentity("a:b", "c", "d"))]));
    }

    [Fact]
    public void Entries_CallerMutatesInput_PreservesSnapshot()
    {
        var entries = new[] { Pair("alpha", Alpha) };
        var tuple = new RepositoryCandidateTuple(entries);
        var canonical = tuple.Canonical;
        entries[0] = Pair("beta", Beta);

        Assert.Equal(Pair("alpha", Alpha), Assert.Single(tuple.Entries));
        Assert.Equal(canonical, tuple.Canonical);
        var list = Assert.IsAssignableFrom<IList<KeyValuePair<string, CandidateIdentity>>>(tuple.Entries);
        Assert.Throws<NotSupportedException>(() => list[0] = Pair("beta", Beta));
    }

    [Fact]
    public void Canonical_OneEntry_RemainsDistinctFromBareIdentity()
    {
        var tuple = new RepositoryCandidateTuple([Pair("alpha", Alpha)]);

        Assert.StartsWith(Prefix, tuple.Canonical);
        Assert.NotEqual(Alpha.Canonical, tuple.Canonical);
        Assert.False(RepositoryCandidateTuple.TryParse(Alpha.Canonical, out _));
    }

    [Fact]
    public void AreSameCandidateSet_MissingOperand_ReturnsFalse()
    {
        var tuple = new RepositoryCandidateTuple([Pair("alpha", Alpha)]);

        Assert.False(RepositoryCandidateTuple.AreSameCandidateSet(tuple, null));
        Assert.False(RepositoryCandidateTuple.AreSameCandidateSet(null, tuple));
        Assert.False(RepositoryCandidateTuple.AreSameCandidateSet(null, null));
    }

    private static KeyValuePair<string, CandidateIdentity> Pair(string key, CandidateIdentity identity) => new(key, identity);

    private static string Frame(string key, CandidateIdentity identity) =>
        key + "=" + identity.Canonical.Length.ToString(CultureInfo.InvariantCulture) + "#" + identity.Canonical;
}
