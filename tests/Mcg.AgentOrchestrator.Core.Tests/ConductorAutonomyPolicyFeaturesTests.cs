using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class ConductorAutonomyPolicyFeaturesTests
{
    private static JsonObject PolicyJson()
    {
        var root = JsonNode.Parse(ConductorAutonomyPolicy.Default.ToJson())!.AsObject();
        Assert.True(root.Remove("features"));
        return root;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public void ParseJson_OmittedFeatures_PreservesEveryLegacyDefault(string? section)
    {
        var root = PolicyJson();
        if (section is not null) root["features"] = JsonNode.Parse(section);
        var features = ConductorAutonomyPolicy.ParseJson(root.ToJsonString()).Features;

        Assert.Equal(new ConductorFeatureSwitches(true, true, true, true, false), features);
        Assert.All(ConductorAutonomyPolicy.All, policy => Assert.Equal(features, policy.Features));
    }

    [Fact]
    public void ParseJson_PartialFeatures_DefaultsEachOmittedKey()
    {
        var root = PolicyJson();
        root["features"] = JsonNode.Parse("""{"steward":false}""");
        Assert.Equal(new ConductorFeatureSwitches(false, true, true, true, false),
            ConductorAutonomyPolicy.ParseJson(root.ToJsonString()).Features);
    }

    public static IEnumerable<object[]> InvalidFeatureValues()
    {
        foreach (var key in new[] { "steward", "author", "selfRelaunch", "mergeTrains", "archivedSnapshotCompression" })
        foreach (var value in new[] { "\"true\"", "1", "null", "{}", "[]" })
            yield return [key, value];
    }

    [Theory]
    [MemberData(nameof(InvalidFeatureValues))]
    public void ParseJson_NonBooleanFeature_RejectsWithKeyKindAndAcceptedValues(string key, string value)
    {
        var root = PolicyJson();
        root["features"] = JsonNode.Parse($"{{\"{key}\":{value}}}");
        using var document = JsonDocument.Parse(value);

        var error = Assert.Throws<FormatException>(() =>
            ConductorAutonomyPolicy.ParseJson(root.ToJsonString(), "test-policy.json"));
        Assert.Contains($"features.{key}", error.Message);
        Assert.Contains(document.RootElement.ValueKind.ToString(), error.Message);
        Assert.Contains("true or false", error.Message);
        Assert.Contains("test-policy.json", error.Message);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("\"enabled\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[]")]
    public void ParseJson_NonObjectFeatures_RejectsWithSectionAndKind(string value)
    {
        var root = PolicyJson();
        root["features"] = JsonNode.Parse(value);
        using var document = JsonDocument.Parse(value);
        var error = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(root.ToJsonString()));
        Assert.Contains("features must be an object", error.Message);
        Assert.Contains(document.RootElement.ValueKind.ToString(), error.Message);
    }

    [Fact]
    public void ParseJson_UnknownFeature_IgnoresItLikeUnknownRootProperties()
    {
        var root = PolicyJson();
        root["futureRoot"] = true;
        root["features"] = JsonNode.Parse("""{"futureFeature":null,"steward":false}""");
        Assert.Equal(new ConductorFeatureSwitches(false, true, true, true, false),
            ConductorAutonomyPolicy.ParseJson(root.ToJsonString()).Features);
    }

    [Fact]
    public void ToJson_NonDefaultFeatures_RoundTripsEveryKey()
    {
        var features = new ConductorFeatureSwitches(false, false, false, false, true);
        var policy = ConductorAutonomyPolicy.Default with { Features = features };
        Assert.Equal(features, ConductorAutonomyPolicy.ParseJson(policy.ToJson()).Features);
    }
}
