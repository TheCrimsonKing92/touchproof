using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure wire and identity contracts.
public sealed class FindingEvidenceRepositoryRequestTests
{
    private const string Legacy = "{\"selections\":[{\"test_project\":\"Core.Tests\",\"test_class\":\"ProbeTests\"}]}";

    [Fact]
    public void Serialize_OmittedOrNullRepository_PreservesLegacyWireAndIdentity()
    {
        foreach (var json in new[] { Legacy, Legacy[..^1] + ",\"repository\":null}" })
        {
            var request = JsonSerializer.Deserialize<FindingEvidenceRequest>(json)!;
            Assert.Null(request.Repository);
            Assert.Equal(Legacy, JsonSerializer.Serialize(request));
            Assert.Equal("Core.Tests:ProbeTests", FindingEvidenceExecutionClassifier.BuildRequestIdentity(request));
        }
    }

    [Theory]
    [InlineData("crosswalk")]
    [InlineData("Crosswalk")]
    [InlineData("default")]
    [InlineData("")]
    public void Parse_SuppliedRepository_RoundTripsRawKey(string repository)
    {
        var request = JsonSerializer.Deserialize<FindingEvidenceRequest>(Legacy)! with { Repository = repository };
        var findings = "[{\"stable_id\":\"probe\",\"state\":\"open\",\"location\":{\"file\":\"src/A.cs\",\"region\":\"A.Run\"}," +
            "\"description\":\"Prove focused evidence\",\"evidence_request\":" + JsonSerializer.Serialize(request) + "}]";
        Assert.True(ReviewFindingConvergence.TryParseJson(findings, "[]", out var round, out var diagnostic), diagnostic);
        Assert.Equal(repository, Assert.Single(round.Findings).EvidenceRequest!.Repository);
        Assert.True(ReviewFindingConvergence.TryParseJson(JsonSerializer.Serialize(round.Findings), "[]", out var replay, out diagnostic), diagnostic);
        Assert.Equal(repository, Assert.Single(replay.Findings).EvidenceRequest!.Repository);
    }

    [Fact]
    public void Identity_RawKeys_AreDistinctIncludingExplicitDefault()
    {
        var request = JsonSerializer.Deserialize<FindingEvidenceRequest>(Legacy)!;
        var identities = new string?[] { null, "default", "crosswalk", "Crosswalk", "", "x|repository=\"y\"" }
            .Select(key => FindingEvidenceExecutionClassifier.BuildRequestIdentity(request with { Repository = key })).ToArray();
        Assert.Equal(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Core.Tests:ProbeTests", identities[0]);
        Assert.Equal("Core.Tests:ProbeTests|repository=\"default\"", identities[1]);
    }

    [Fact]
    public void Merge_RepositoryChanged_DropsPriorOutcome()
    {
        var request = JsonSerializer.Deserialize<FindingEvidenceRequest>(Legacy)!;
        var prior = new ReviewFinding("probe", ReviewFindingState.Open, new("src/A.cs", "A.Run"), "Prove evidence",
            EvidenceRequest: request, EvidenceOutcome: new FindingEvidenceOutcome(true, "home-receipt"));
        var next = prior with { EvidenceRequest = request with { Repository = "crosswalk" }, EvidenceOutcome = null };
        var merged = Assert.Single(ReviewFindingConvergence.ApplyRound([prior], new ReviewFindingRound([next], [])));
        Assert.Equal("crosswalk", merged.EvidenceRequest!.Repository);
        Assert.Null(merged.EvidenceOutcome);
    }
}
