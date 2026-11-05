using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: immutable fixtures and pure source-span parsing.
public sealed class OwnerActivityDiagnosticParserTests
{
    [Theory]
    [InlineData("task_failure")]
    [InlineData("latest_matching_escalation")]
    public void RetainedRecordsKeepEverySourceValueAndProducerBoundary(string name)
    {
        using var fixture = OwnerActivityDetailFixture.Read();
        var source = fixture.RootElement.GetProperty(name).GetProperty("Message").GetString()!;
        var parsed = OwnerActivityDiagnosticParser.Parse(source);
        Assert.True(parsed.Complete);
        Assert.Equal(12, parsed.Fields.Count);
        Assert.Equal("none", Value("changed_paths"));
        Assert.Equal("None", Value("blockers"));
        Assert.Equal("Deferred", Value("tests_status"));
        Assert.Equal("cf4b9c145ade6ce2ab70a43ddb46e5bc710d6f19", Value("baseline_head"));
        Assert.Equal("2026-11-04T08:35:01.1819368+00:00", Value("dispatched_at"));
        Assert.Equal("\"C:\\\\Users\\\\miles\\\\vcs\\\\mcg-agent-orchestrator\\\\.orchestrator-worktrees\\\\fd7ee5fb\"", Value("worktree"));
        Assert.Equal("\"deferred — `GoalWorktreeRecoveryRefusalTests`, `ConductorMissingWorktreeRecoveryTests` (Acceptance-owned execution). matched no accepted verification pattern\"", Value("detail"));
        Assert.All(parsed.Fields, field => Assert.Contains(field.Label + "=" + field.Value, source));
        string? Value(string label) => Assert.Single(parsed.Fields, field => field.Label == label).Value;
    }

    [Fact]
    public void QuotedMultilineEmptyEqualsDuplicatesAndCollidingKeysRemainOpaque()
    {
        var source = Raw() + " extra= foo_bar=none foo-bar=a=b extra=\"  first = none_\\\"quoted\\\"\r\nsecond  \"";
        var parsed = OwnerActivityDiagnosticParser.Parse(source);
        Assert.True(parsed.Complete);
        var extras = parsed.Fields.Where(field => field.Label == "extra").ToArray();
        Assert.Equal("", extras[0].Value);
        Assert.Equal("\"  first = none_\\\"quoted\\\"\r\nsecond  \"", extras[1].Value);
        Assert.NotEqual(extras[0].Key, extras[1].Key);
        Assert.Equal("none", Assert.Single(parsed.Fields, field => field.Label == "foo_bar").Value);
        Assert.Equal("a=b", Assert.Single(parsed.Fields, field => field.Label == "foo-bar").Value);
    }

    [Theory]
    [InlineData(" unknown=two words")]
    [InlineData(" unknown=\"unterminated")]
    [InlineData(" unknown=\"bad\\q\"")]
    [InlineData(" unknown=\"closed\"junk")]
    [InlineData(" unknown=value\r\nnext")]
    public void AmbiguousOrMalformedTailCannotInventFields(string tail)
    {
        var parsed = OwnerActivityDiagnosticParser.Parse(Raw() + tail);
        Assert.False(parsed.Complete);
        Assert.Empty(parsed.Fields);
    }

    internal static string Raw()
    {
        using var fixture = OwnerActivityDetailFixture.Read();
        return fixture.RootElement.GetProperty("task_failure").GetProperty("Message").GetString()!;
    }
}
