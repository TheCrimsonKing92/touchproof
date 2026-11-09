using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: immutable policies and private JSON values.
public sealed class ConductorAutonomyPolicyTestsObsoleteKeys
{
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("123")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ParseJson_ObsoleteFollowerKey_LeavesEffectivePolicyUnchanged(string value)
    {
        var baselineJson = ConductorAutonomyPolicy.Permissive.ToJson();
        var baseline = ConductorAutonomyPolicy.ParseJson(baselineJson);
        var json = JsonNode.Parse(baselineJson)!.AsObject();
        json["followerGatesEnabled"] = JsonNode.Parse(value);

        var policy = ConductorAutonomyPolicy.ParseJson(json.ToJsonString());

        Assert.Equal(baseline.ToJson(), policy.ToJson());
        Assert.DoesNotContain("followerGatesEnabled", policy.ToJson());
        Assert.False(ConductorPolicyBooleanFlags.IsAllowed("followerGatesEnabled"));
    }
}
