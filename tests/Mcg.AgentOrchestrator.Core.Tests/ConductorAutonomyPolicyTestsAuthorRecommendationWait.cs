using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core.Tests;

// Parallel-safe: policy parsing has no shared state or I/O.
public sealed class ConductorAutonomyPolicyTestsAuthorRecommendationWait
{
    [Xunit.Fact]
    public void DefaultsAndAbsentKey_UseFiveMinutes()
    {
        foreach (var policy in new[] { ConductorAutonomyPolicy.Default,
                     ConductorAutonomyPolicy.Conservative, ConductorAutonomyPolicy.Manual,
                     ConductorAutonomyPolicy.Permissive })
        {
            Assert.Equal(300, policy.AuthorRecommendationWaitSeconds);
            var json = JsonNode.Parse(policy.ToJson())!.AsObject();
            Assert.True(json.Remove("authorRecommendationWaitSeconds"));
            Assert.Equal(300, ConductorAutonomyPolicy.ParseJson(json.ToJsonString()).AuthorRecommendationWaitSeconds);
        }
    }

    [Xunit.Fact]
    public void PositiveWait_RoundTrips()
    {
        var policy = ConductorAutonomyPolicy.Permissive with { AuthorRecommendationWaitSeconds = 17 };
        Assert.Empty(policy.Validate());
        Assert.Equal(17, ConductorAutonomyPolicy.ParseJson(policy.ToJson()).AuthorRecommendationWaitSeconds);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-1)]
    public void NonPositiveWait_IsRejected(int seconds)
    {
        var policy = ConductorAutonomyPolicy.Permissive with { AuthorRecommendationWaitSeconds = seconds };
        Assert.Contains(policy.Validate(), error => error.Contains("authorRecommendationWaitSeconds"));
        Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(policy.ToJson()));
    }

    [Xunit.Theory]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("\"300\"")]
    [Xunit.InlineData("1.5")]
    [Xunit.InlineData("true")]
    [Xunit.InlineData("2147483648")]
    public void NonIntegerWait_IsRejected(string value)
    {
        var json = JsonNode.Parse(ConductorAutonomyPolicy.Permissive.ToJson())!;
        json["authorRecommendationWaitSeconds"] = JsonNode.Parse(value);
        var error = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(json.ToJsonString()));
        Assert.Contains("authorRecommendationWaitSeconds must be an integer", error.Message);
    }
}
