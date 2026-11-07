using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: immutable policies and private JSON values.
public sealed class ConductorAutonomyPolicyTestsAdoptedBuildPush
{
    [Fact]
    public void PresetsAndAbsentProperty_DefaultOff()
    {
        foreach (var preset in new[] { ConductorAutonomyPolicy.Conservative,
            ConductorAutonomyPolicy.Permissive, ConductorAutonomyPolicy.Manual })
        {
            Assert.False(preset.AdoptedBuildPushEnabled);
            var json = JsonNode.Parse(preset.ToJson())!.AsObject();
            Assert.True(json.Remove("adoptedBuildPushEnabled"));
            Assert.False(ConductorAutonomyPolicy.ParseJson(json.ToJsonString()).AdoptedBuildPushEnabled);
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    public void BooleanValue_ParsesAndRoundTrips(string value, bool expected)
    {
        var json = JsonNode.Parse(ConductorAutonomyPolicy.Permissive.ToJson())!.AsObject();
        json["adoptedBuildPushEnabled"] = JsonNode.Parse(value);
        var policy = ConductorAutonomyPolicy.ParseJson(json.ToJsonString());
        Assert.Equal(expected, policy.AdoptedBuildPushEnabled);
        Assert.Equal(expected, ConductorAutonomyPolicy.ParseJson(policy.ToJson()).AdoptedBuildPushEnabled);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"true\"")]
    [InlineData("{}")]
    public void NonBoolean_RejectsWithKey(string value)
    {
        var json = JsonNode.Parse(ConductorAutonomyPolicy.Permissive.ToJson())!.AsObject();
        json["adoptedBuildPushEnabled"] = JsonNode.Parse(value);
        Assert.Contains("adoptedBuildPushEnabled", Assert.Throws<FormatException>(() =>
            ConductorAutonomyPolicy.ParseJson(json.ToJsonString())).Message);
    }
}
