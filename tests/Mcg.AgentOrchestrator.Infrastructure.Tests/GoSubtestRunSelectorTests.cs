using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each fixture owns its directory; no commands are executed.
public sealed class GoSubtestRunSelectorTests
{
    [Theory]
    [InlineData("TestA/value.+([/leaf", @"^TestA$/^value\.\+\(\[$/^leaf$")]
    [InlineData(@"TestA/\.+*?()[]{}^$#", @"^TestA$/^\\\.\+\*\?\(\)\[\]\{\}\^\$#$")]
    [InlineData("TestA/日本語_入力", "^TestA$/^日本語_入力$")]
    public void EvidenceSelectorsAnchorAndEscapeEverySegment(string name, string expected)
    {
        using var fixture = new Fixture();
        var check = Assert.Single(fixture.Build("internal/drift:" + name, out var coverage));

        Assert.Equal(["test", "-count=1", "-v", "-run", expected, "./internal/drift"], check.Arguments);
        Assert.Equal("go focused evidence: internal/drift " + name, check.Name);
        Assert.Equal("go", check.Command);
        Assert.Equal(7, check.TimeoutMinutes);
        Assert.Equal("focused", coverage.ExecutionMode);
        Assert.Equal([check.Name], Assert.Single(coverage.TargetToChecks).CheckNames);
        var segments = name.Split('/');
        var patterns = check.Arguments[4].Split('/');
        Assert.Equal(segments.Length, patterns.Length);
        for (var index = 0; index < segments.Length; index++)
        {
            Assert.Matches(patterns[index], segments[index]);
            Assert.DoesNotMatch(patterns[index], "prefix" + segments[index]);
            Assert.DoesNotMatch(patterns[index], segments[index] + "suffix");
        }
    }

    [Fact]
    public void EscapedDotDoesNotSelectAnUnrelatedSibling()
    {
        using var fixture = new Fixture();
        var check = Assert.Single(fixture.Build("internal/drift:TestA/value.one", out _));
        var pattern = check.Arguments[4].Split('/')[1];

        Assert.True(Regex.IsMatch("value.one", pattern));
        Assert.False(Regex.IsMatch("valueXone", pattern));
    }

    [Fact]
    public void SeparateSubtestsPreserveTheirParentPairsAndDeduplicateChecks()
    {
        using var fixture = new Fixture();
        const string request = "internal/drift:TestB/right|TestA/left|TestB/right; internal/drift:TestA/left";
        var checks = fixture.Build(request, out var coverage);

        Assert.Equal(2, checks.Count);
        Assert.Equal(["test", "-count=1", "-v", "-run", "^TestA$/^left$", "./internal/drift"], checks[0].Arguments);
        Assert.Equal(["test", "-count=1", "-v", "-run", "^TestB$/^right$", "./internal/drift"], checks[1].Arguments);
        Assert.Equal([checks[0].Name, checks[1].Name], coverage.TargetToChecks[0].CheckNames);
        Assert.Equal([checks[0].Name], coverage.TargetToChecks[1].CheckNames);
        Assert.Equal("focused", coverage.ExecutionMode);
    }

    [Fact]
    public void TopLevelUnionKeepsItsExistingArgumentsAlongsideSubtests()
    {
        using var fixture = new Fixture();
        const string topLevel = "internal/drift:TestMissingColumn|TestExtraColumn";
        var original = Assert.Single(fixture.Build(topLevel, out _));
        var checks = fixture.Build(topLevel + "|TestMissingColumn/empty_input", out var coverage);

        Assert.Equal(["test", "-count=1", "-v", "-run", "^(TestExtraColumn|TestMissingColumn)$", "./internal/drift"],
            original.Arguments);
        Assert.Equal(2, checks.Count);
        Assert.Equal(original.Name, checks[0].Name);
        Assert.Equal(original.Arguments, checks[0].Arguments);
        Assert.Equal(["test", "-count=1", "-v", "-run", "^TestMissingColumn$/^empty_input$", "./internal/drift"],
            checks[1].Arguments);
        Assert.Equal(checks.Select(check => check.Name), Assert.Single(coverage.TargetToChecks).CheckNames);
    }

    [Theory]
    [InlineData("TestA/")]
    [InlineData("TestA//leaf")]
    [InlineData("TestA/sub//leaf")]
    [InlineData("TestA/has space")]
    [InlineData("TestA/has,comma")]
    [InlineData("Other/sub")]
    public void InvalidSubtestNamesKeepTheDeclaredCheckFallback(string name)
    {
        using var fixture = new Fixture();
        var check = Assert.Single(fixture.Build("internal/drift:" + name, out var coverage));

        Assert.Equal("go declared-check evidence: go test", check.Name);
        Assert.Equal(["test", "-v", "-count=1", "./..."], check.Arguments);
        Assert.Equal("project", coverage.ExecutionMode);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = InfrastructureTestSupport.CreateTempDirectory();
        private readonly AcceptanceGateEngineSettings settings =
            AcceptanceGateEngineSettings.Parse(GoPackageFocusedEvidenceTests.Manifest);

        internal Fixture()
        {
            var package = Path.Combine(root, "internal", "drift");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "drift_test.go"), "package drift");
        }

        internal IReadOnlyList<AcceptanceManifestCheck> Build(string request, out FocusedEvidenceCoverage coverage)
        {
            Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(request, settings, root,
                out var checks, out coverage, out var rejection), rejection.Detail);
            return checks;
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
