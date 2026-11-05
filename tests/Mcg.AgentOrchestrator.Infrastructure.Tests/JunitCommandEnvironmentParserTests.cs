using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: parsing and immutable snapshots only.
public sealed class JunitCommandEnvironmentParserTests
{
    internal static Check Parse(string environment = "", string type = "junit-command") =>
        JsonSerializer.Deserialize<Check>("{\"type\":\"" + type + "\",\"command\":\"suite\",\"reports\":[\"reports/*.xml\"]" + environment + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void OmittedAndEmptyInputs_ProduceNoOverrides()
    {
        Assert.Null(Parse().Environment);
        Assert.Empty(Parse(",\"environment\":{}").Environment!);
        Assert.Null(JunitCommandEnvironmentInputs.FromCheck(Parse()));
        Assert.Null(JunitCommandEnvironmentInputs.FromCheck(Parse(",\"environment\":{}")));
    }

    [Fact]
    public void LiteralInputs_ArePreservedAndSnapshotIsImmutable()
    {
        const string literal = "  ../fixture ${HOME} %PATH% $(whoami) \u96ea  ";
        var source = new Dictionary<string, string> { ["fixture input"] = literal, ["EMPTY_INPUT"] = "" };
        var check = new Check { Type = "junit-command", Environment = source };
        source["fixture input"] = "changed";
        Assert.Equal(literal, check.Environment!["fixture input"]);
        Assert.Equal("", check.Environment["EMPTY_INPUT"]);
        var parsed = Parse(",\"environment\":" + JsonSerializer.Serialize(check.Environment));
        Assert.Equal(literal, parsed.Environment!["fixture input"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)check.Environment).Add("OTHER", "value"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{\"INPUT\":null}")]
    [InlineData("{\"INPUT\":false}")]
    [InlineData("{\"INPUT\":{}}")]
    [InlineData("{\"\":\"value\"}")]
    [InlineData("{\"A=B\":\"value\"}")]
    [InlineData("{\"A\\u0000B\":\"value\"}")]
    [InlineData("{\"INPUT\":\"a\\u0000b\"}")]
    [InlineData("{\"INPUT\":\"a\",\"INPUT\":\"b\"}")]
    public void InvalidMap_IsRejectedWithoutValues(string json) =>
        Assert.Throws<JsonException>(() => Parse(",\"environment\":" + json));

    [Fact]
    public void CaseVariantDuplicates_FollowPlatformNameSemantics()
    {
        const string map = ",\"environment\":{\"Fixture\":\"one\",\"FIXTURE\":\"two\"}";
        if (OperatingSystem.IsWindows()) Assert.Throws<JsonException>(() => Parse(map));
        else Assert.Equal(2, Parse(map).Environment!.Count);
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("DOTNET_CLI_HOME")]
    [InlineData("MCG_GATE_INPUT")]
    [InlineData("mcg_gate_input")]
    [InlineData("GIT_CONFIG_GLOBAL")]
    [InlineData("CODEX_HOME")]
    [InlineData("CLAUDE_CONFIG_DIR")]
    public void ProtectedName_IsRefused(string name) =>
        Assert.Throws<JsonException>(() => Parse(",\"environment\":{\"" + name + "\":\"private-input\"}"));

    [Theory]
    [InlineData("command")]
    [InlineData("dotnet-test")]
    [InlineData("crosswalk-test")]
    [InlineData("no-op")]
    public void UnsupportedCheck_RejectsEvenEmptyMap(string type) =>
        Assert.Throws<ArgumentException>(() => Parse(",\"environment\":{}", type));

    [Fact]
    public void DeclaredToolHome_IsProtectedAtLaunch()
    {
        var inputs = JunitCommandEnvironmentInputs.FromCheck(Parse(",\"environment\":{\"JAVA_HOME\":\"fixture\"}"))!;
        Assert.Throws<ArgumentException>(() => inputs.Validate([new("JAVA_HOME", "owner-tool-home")]));
    }
}
