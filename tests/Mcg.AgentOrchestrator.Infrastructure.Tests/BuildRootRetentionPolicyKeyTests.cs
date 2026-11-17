using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class BuildRootRetentionPolicyKeyTests
{
    [Fact]
    public void KeyDefaultsOffParsesValidatesAndIsDocumented()
    {
        foreach (var preset in new[] { ConductorAutonomyPolicy.Conservative,
                     ConductorAutonomyPolicy.Permissive, ConductorAutonomyPolicy.Manual })
            Assert.Equal(0, preset.BuildRootRetentionDays);
        var json = ConductorAutonomyPolicy.Conservative.ToJson();
        Assert.Contains("\"buildRootRetentionDays\": 0", json);
        var absent = string.Join(Environment.NewLine, json.Split('\n')
            .Where(line => !line.Contains("\"buildRootRetentionDays\"", StringComparison.Ordinal)));
        Assert.Equal(0, ConductorAutonomyPolicy.ParseJson(absent).BuildRootRetentionDays);
        var node = JsonNode.Parse(json)!.AsObject();
        node["buildRootRetentionDays"] = null;
        Assert.Equal(0, ConductorAutonomyPolicy.ParseJson(node.ToJsonString()).BuildRootRetentionDays);
        Assert.Equal(7, ConductorAutonomyPolicy.ParseJson(
            (ConductorAutonomyPolicy.Conservative with { BuildRootRetentionDays = 7 }).ToJson()).BuildRootRetentionDays);
        foreach (var value in new[] { "366", "-1", "\"7\"", "7.5" })
        {
            node["buildRootRetentionDays"] = JsonNode.Parse(value);
            var error = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(node.ToJsonString()));
            Assert.Contains("buildRootRetentionDays", error.Message);
            if (value is "366" or "-1")
                Assert.Contains($"buildRootRetentionDays must be between 0 and 365 (got {value}).", error.Message);
            else
                Assert.Contains("must be an integer.", error.Message);
        }
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "operator-runbook.md"));
        Assert.Contains("buildRootRetentionDays", text);
        Assert.Contains("SWEEP_BUILD_ROOT_RETENTION", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(26181486611L)]
    public void SizeBudget_NullZeroAndInt64_RoundTrip(long? budget)
    {
        var policy = ConductorAutonomyPolicy.Conservative with { TerminalBuildRootMaxBytes = budget };
        var parsed = ConductorAutonomyPolicy.ParseJson(policy.ToJson());
        Assert.Equal(budget, parsed.TerminalBuildRootMaxBytes);
        Assert.Contains($"\"terminalBuildRootMaxBytes\": {budget?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}", policy.ToJson());
    }

    [Fact]
    public void SizeBudget_AbsentOrNull_DefaultsToNoLimit()
    {
        foreach (var preset in new[] { ConductorAutonomyPolicy.Conservative,
                     ConductorAutonomyPolicy.Permissive, ConductorAutonomyPolicy.Manual })
            Assert.Null(preset.TerminalBuildRootMaxBytes);
        var node = JsonNode.Parse(ConductorAutonomyPolicy.Conservative.ToJson())!.AsObject();
        node.Remove("terminalBuildRootMaxBytes");
        Assert.Null(ConductorAutonomyPolicy.ParseJson(node.ToJsonString()).TerminalBuildRootMaxBytes);
        node["terminalBuildRootMaxBytes"] = null;
        Assert.Null(ConductorAutonomyPolicy.ParseJson(node.ToJsonString()).TerminalBuildRootMaxBytes);
    }

    [Theory]
    [InlineData("-1", "terminalBuildRootMaxBytes must be zero or greater (got -1).")]
    [InlineData("\"5\"", "terminalBuildRootMaxBytes must be an integer or null.")]
    [InlineData("1.5", "terminalBuildRootMaxBytes must be an integer or null.")]
    [InlineData("9223372036854775808", "terminalBuildRootMaxBytes must be an integer or null.")]
    [InlineData("true", "terminalBuildRootMaxBytes must be an integer or null.")]
    public void SizeBudget_InvalidValue_IsRejected(string value, string message)
    {
        var node = JsonNode.Parse(ConductorAutonomyPolicy.Conservative.ToJson())!.AsObject();
        node["terminalBuildRootMaxBytes"] = JsonNode.Parse(value);
        var error = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(node.ToJsonString()));
        Assert.Contains(message, error.Message);
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") => VerifiedRepositoryRoot.Find(source);
}
