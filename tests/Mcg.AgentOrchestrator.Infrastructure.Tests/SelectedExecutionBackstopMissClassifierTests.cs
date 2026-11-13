using Mcg.AgentOrchestrator.App.Orchestration;

// Pure classification; no shared state or external gate.
public sealed class SelectedExecutionBackstopMissClassifierTests
{
    [Theory]
    [InlineData("Ns.C", "Ns.C.Method", true)]
    [InlineData("Ns.C", "Other.C.Method", false)]
    [InlineData("Ns.C", "ns.C.Method", false)]
    [InlineData("Ns.C", "Ns.C.Method(\"a.b\")", true)]
    [InlineData("Ns.C", "  Ns.C.Method  ", true)]
    [InlineData("Ns.Outer+Inner", "Ns.Outer.Inner.Method", true)]
    [InlineData("Ns.Outer.Inner", "Ns.Outer+Inner.Method", true)]
    [InlineData("Ns.Outer+Inner", "Ns.Outer+Inner.Method", true)]
    [InlineData("Ns.C", "Ns.C<T>.Method", false)]
    [InlineData("Ns.C", "Ns.C`1.Method", false)]
    [InlineData("Ns.C<T>", "Ns.C<T>.Method", true)]
    [InlineData("Ns.C`1", "Ns.C`1.Method", true)]
    [InlineData("Ns.C`1", "Ns.C<T>.Method", false)]
    [InlineData("C", "C.Method", true)]
    [InlineData("Ns.C", "Method", false)]
    [InlineData("Ns.C", "", false)]
    public void ClassIdentityRequiresExactOrdinalMembership(string skipped, string identity, bool expected)
    {
        var result = SelectedExecutionBackstopMissClassifier.Classify([identity],
            [new(1, "goal", "commit", true) { SkippedClasses = [skipped] }]);
        Assert.Equal(expected, result.IsMiss);
        Assert.Equal(expected ? Array.Empty<string>() : [identity], result.UnmatchedTests);
    }

    [Theory]
    [InlineData("no-identities")]
    [InlineData("no-skipped-classes")]
    [InlineData("non-applied")]
    public void MissingPositiveEvidenceCannotEstablishMiss(string evidence)
    {
        string[] identities = evidence == "no-identities" ? [] : ["Ns.C.Method"];
        var result = SelectedExecutionBackstopMissClassifier.Classify(identities,
            [new(1, "goal", "commit", evidence != "non-applied")
                { SkippedClasses = evidence == "no-skipped-classes" ? [] : ["Ns.C"] }]);
        Assert.False(result.IsMiss);
        Assert.Equal(identities, result.UnmatchedTests);
    }

    [Fact]
    public void AnyCoveredMatchEstablishesMissAndUnmatchedTextKeepsItsOrder()
    {
        var result = SelectedExecutionBackstopMissClassifier.Classify(
            [" Other.C.First(\"a.b\") ", "Ns.C.Method", "unresolvable"],
            [new(1, "first", "c1", true), new(2, "second", "c2", true) { SkippedClasses = ["Ns.C"] }]);
        Assert.True(result.IsMiss);
        Assert.Equal(new[] { " Other.C.First(\"a.b\") ", "unresolvable" }, result.UnmatchedTests);
    }
}
