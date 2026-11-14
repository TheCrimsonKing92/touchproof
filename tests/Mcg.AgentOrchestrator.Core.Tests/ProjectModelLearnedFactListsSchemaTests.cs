using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: every snapshot and JSON document is owned in memory by the test.
public sealed class ProjectModelLearnedFactListsSchemaTests
{
    [Fact]
    public void Schema5_RoundTrip_PreservesAllLearnedFacts()
    {
        var model = Snapshot();
        var json = ProjectModelJson.Serialize(model);
        var restored = ProjectModelJson.Deserialize(json);

        Assert.Equal(5, ProjectModel.CurrentSchemaVersion);
        Assert.Equal(5, restored.SchemaVersion);
        Assert.Equal(model.RiskAreas[0], Assert.Single(restored.RiskAreas));
        Assert.Equal(model.Conventions[0], Assert.Single(restored.Conventions));
        Assert.Equal(model.Verifications[0], Assert.Single(restored.Verifications));
        Assert.Contains("\"kind\": \"persistence\"", json);
        Assert.Contains("\"kind\": \"unitTests\"", json);
        Assert.Equal(json, ProjectModelJson.Serialize(restored));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LegacySnapshot_UpgradesAndDiscardsStrayNewLists(int version)
    {
        var document = JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject();
        document["schemaVersion"] = version;
        var restored = ProjectModelJson.Deserialize(document.ToJsonString());

        Assert.Equal(5, restored.SchemaVersion);
        Assert.Empty(restored.RiskAreas);
        Assert.Empty(restored.Conventions);
        Assert.Empty(restored.Verifications);
        Assert.Equal(".", restored.RepositoryRoot);
    }

    [Fact]
    public void Schema4_AbsentLists_UpgradeToEmptyWithoutRewritingInput()
    {
        const string legacy = """
            {"schemaVersion":4,"repositoryRoot":".","units":[],"dependencies":[],
             "testSetups":[],"ownerQuestions":[],"commands":[],"environmentNeeds":[],
             "measurements":[],"hazards":[]}
            """;
        var restored = ProjectModelJson.Deserialize(legacy);
        Assert.Equal(5, restored.SchemaVersion);
        Assert.Empty(restored.RiskAreas);
        Assert.Empty(restored.Conventions);
        Assert.Empty(restored.Verifications);
        Assert.Equal(4, JsonNode.Parse(legacy)!["schemaVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Schema5_MissingOrNullLists_DefaultToEmpty(bool explicitNull)
    {
        var document = JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject();
        foreach (var name in new[] { "riskAreas", "conventions", "verifications" })
        {
            if (explicitNull)
                document[name] = null;
            else
                document.Remove(name);
        }
        var restored = ProjectModelJson.Deserialize(document.ToJsonString());
        Assert.Empty(restored.RiskAreas);
        Assert.Empty(restored.Conventions);
        Assert.Empty(restored.Verifications);
    }

    [Theory]
    [InlineData("riskAreas")]
    [InlineData("verifications")]
    public void UnknownKind_RejectsMalformedFact(string list)
    {
        var document = JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject();
        document[list]![0]!["kind"] = "invented";
        Assert.Throws<JsonException>(() => ProjectModelJson.Deserialize(document.ToJsonString()));
    }

    private static ProjectModel Snapshot() => new(ProjectModel.CurrentSchemaVersion, ".", [], [], [], [], [], [], [], [],
        RiskAreas: [new("Library.csproj", ProjectRiskAreaKind.Persistence,
            new("Dapper", new("Library.csproj", 7), FactConfidence.High))],
        Conventions: [new("AGENTS.md", "Build rules", "Keep **all** evidence.",
            new("Keep **all** evidence.", new("AGENTS.md", 4), FactConfidence.Medium))],
        Verifications: [new("Tests.csproj", UnitVerificationKind.UnitTests,
            new("VSTest", new("Tests.csproj", 9), FactConfidence.High))]);
}
