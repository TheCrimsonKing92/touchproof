public sealed class MtpFilterArgumentSemanticsTests
{
    [Xunit.Fact]
    public void Select_ClassFilterMatchesNamespaceInHostCatalog()
    {
        const string discovery = """
            {
              "tests": [
                { "uid": "global", "displayName": "Global test",
                  "type": { "typeName": "ProcessTreeGuiSuppressionTests", "methodName": "Test" } },
                { "uid": "namespace-match", "displayName": "Namespace match",
                  "type": { "namespace": "Fixtures.ProcessTreeGuiSuppressionTests",
                            "typeName": "ProcessErrorModeEventObserverTests", "methodName": "Test" } },
                { "uid": "unrelated", "displayName": "Unrelated test",
                  "type": { "namespace": "Fixtures", "typeName": "OtherTests", "methodName": "Test" } }
              ]
            }
            """;
        var catalog = MtpFilterArgumentSemantics.ReadHostCatalog(discovery);
        string[] include = ["--filter-class", "*ProcessTreeGuiSuppressionTests*"];
        string[] exclude = ["--filter-not-class", "*ProcessTreeGuiSuppressionTests*"];

        Assert.Equal(["global", "namespace-match"], MtpFilterArgumentSemantics.Select(include, catalog).Select(test => test.Uid));
        Assert.Equal(["unrelated"], MtpFilterArgumentSemantics.Select(exclude, catalog).Select(test => test.Uid));
        Assert.Equal("Fixtures.ProcessTreeGuiSuppressionTests.ProcessErrorModeEventObserverTests", catalog[1].TypeName);

        var simpleNames = catalog.Select(test => test with
        {
            TypeName = test.TypeName[(test.TypeName.LastIndexOf('.') + 1)..]
        }).ToArray();
        Assert.Equal(["global"], MtpFilterArgumentSemantics.Select(include, simpleNames).Select(test => test.Uid));
    }

    [Xunit.Fact]
    public void UnknownArgumentKindFailsLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MtpFilterArgumentSemantics.KindSignature(["--filter-trait", "Category=CrossTick"]));
        Assert.Contains("--filter-trait", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OddLengthArgumentVectorFailsLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MtpFilterArgumentSemantics.KindSignature(["--filter-class"]));
        Assert.Contains("option/value pairs", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MissingRealRunnerSignatureIsReported()
    {
        Assert.Equal(["--filter-method"], MtpFilterArgumentSemantics.UncoveredSignatures(
            ["--filter-class", "--filter-method"], ["--filter-class"]));
    }
}
