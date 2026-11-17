using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = NodeProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: each test owns its fixture; answer application and comparison use immutable snapshots.
public sealed class NodeOwnerExtraChecksTests
{
    private const string Architecture = "node scripts/check-architecture.mjs";
    private const string Prose = "node scripts/check-prose.mjs";
    private const string LearnedChecks = """
        [
          {"name":"npm ci","type":"command","command":"npm","arguments":["ci"]},
          {"name":"npx tsc --noEmit","type":"command","command":"npx","arguments":["tsc","--noEmit"]},
          {"name":"crosswalk-test .","type":"crosswalk-test","project":".","arguments":[]}
        ]
        """;

    [Fact]
    public void Validate_ArchitectureExtra_ReturnsNormalizedLineAndLeavesNodeEmissionUnchanged()
    {
        const string key = "commands/architecture/extra/architecture";
        using var fixture = new Fixture();
        var model = fixture.Discover();

        Assert.True(OwnerAnswerValidator.HasRule(key));
        Assert.Equal(Architecture, OwnerAnswerValidator.Validate(key, "  node   scripts/check-architecture.mjs  "));

        AssertChecks(JsonNode.Parse(LearnedChecks)!, Emit(model, Extra(key, Architecture)));
    }

    [Theory]
    [InlineData("commands/./extra/prose", "", true)]
    [InlineData("commands/./extra/prose", "   ", true)]
    [InlineData("commands/./extra/prose", "node scripts/a.mjs | tee", true)]
    [InlineData("commands/./extra/prose", "node \"scripts/a.mjs\"", true)]
    [InlineData("commands/./extra/check_prose", Prose, false)]
    [InlineData("commands/./extra/check.prose", Prose, false)]
    [InlineData("commands/./extra/check prose", Prose, false)]
    [InlineData("commands/./extra/check/prose", Prose, false)]
    [InlineData("commands/./extra/é", Prose, false)]
    [InlineData("commands/./extra/١", Prose, false)]
    [InlineData("commands/./extra/", Prose, false)]
    [InlineData("commands//extra/prose", Prose, false)]
    public void Validate_RefusedExtra_NamesKeyAndEmitsOnlyLearnedChecks(string key, string value, bool hasRule)
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();

        Assert.Equal(hasRule, OwnerAnswerValidator.HasRule(key));
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(key, value));
        Assert.Contains(key, error.Message, StringComparison.Ordinal);
        if (hasRule)
            Assert.DoesNotContain("has no answer rule", error.Message, StringComparison.Ordinal);
        else
            Assert.Contains("commands/<unit>/extra/<name>", error.Message, StringComparison.Ordinal);

        // OwnerAnswer itself refuses whitespace-only values before they can reach the applier.
        OwnerAnswer[] refused = string.IsNullOrWhiteSpace(value) ? [] : [Extra(key, value)];
        AssertChecks(JsonNode.Parse(LearnedChecks)!, Emit(model, refused));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("timeoutMinutes")]
    [InlineData("-Az09-")]
    [InlineData("-")]
    public void Validate_ValidName_EmitsNormalizedCommandInsteadOfSelectingRunnerOrTimeoutRule(string name)
    {
        using var fixture = new Fixture();
        var key = $"commands/./extra/{name}";

        Assert.True(OwnerAnswerValidator.HasRule(key));
        Assert.Equal(Architecture, OwnerAnswerValidator.Validate(key, " node  scripts/check-architecture.mjs "));

        var expected = JsonNode.Parse(LearnedChecks)!.AsArray();
        expected.Add(Command(Architecture, "node", "scripts/check-architecture.mjs"));
        AssertChecks(expected, Emit(fixture.Discover(), Extra(key, " node  scripts/check-architecture.mjs ")));
    }

    [Fact]
    public void Build_TwoExtras_AppendsCommandChecksInAnswerKeyOrder()
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var expected = JsonNode.Parse(LearnedChecks)!.AsArray();
        expected.Add(Command(Architecture, "node", "scripts/check-architecture.mjs"));
        expected.Add(Command(Prose + " --strict", "node", "scripts/check-prose.mjs", "--strict"));

        var manifest = Emit(model,
            Extra("commands/./extra/a-prose", Prose + " --strict"),
            Extra("commands/./extra/Z-architecture", Architecture));

        AssertChecks(expected, manifest);
        AssertChecks(JsonNode.Parse(LearnedChecks)!, Emit(model));
    }

    [Fact]
    public void Build_ReansweredKeyAndDuplicateCommands_UsesLatestValueAndEmitsEachKey()
    {
        using var fixture = new Fixture();
        var expected = JsonNode.Parse(LearnedChecks)!.AsArray();
        expected.Add(Command(Prose, "node", "scripts/check-prose.mjs"));
        expected.Add(Command(Prose, "node", "scripts/check-prose.mjs"));
        var latest = new OwnerAnswer("commands/./extra/a", Prose, "fixture", DateTimeOffset.UnixEpoch.AddSeconds(1));

        AssertChecks(expected, Emit(fixture.Discover(), latest,
            Extra("commands/./extra/b", Prose), Extra("commands/./extra/a", Architecture)));
    }

    [Fact]
    public void Compare_HandAddedProseCheck_ReportsMissingFromLearned()
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var manifest = Emit(model);
        manifest["checks"]!.AsArray().Add(Command(Prose, "node", "scripts/check-prose.mjs"));

        var results = ManifestCheckComparer.Compare(model, manifest.ToJsonString());

        Assert.Equal(4, results.Count);
        var extra = Assert.Single(results, row => row.Kind == ManifestCheckResultKind.MissingFromLearned);
        Assert.Equal(Prose, extra.ProjectPath);
        Assert.Null(extra.LearnedRunner);
        Assert.Equal(Prose, extra.ManifestRunner);
        Assert.Equal(3, results.Count(row => row.Kind == ManifestCheckResultKind.Match));
        Assert.Equal(results, ManifestCheckComparer.Compare(model, manifest.ToJsonString(),
            OwnerAnswerApplier.Apply(model, Fixture.Answers())));
    }

    [Fact]
    public void Compare_ExtraWithoutManifestCheck_ReportsMissingFromManifest()
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var applied = OwnerAnswerApplier.Apply(model,
            [.. Fixture.Answers(), Extra("commands/./extra/architecture", Architecture)]);

        var results = ManifestCheckComparer.Compare(model, Emit(model).ToJsonString(), applied);

        Assert.Equal(4, results.Count);
        var extra = Assert.Single(results, row => row.Kind == ManifestCheckResultKind.MissingFromManifest);
        Assert.Equal(Architecture, extra.ProjectPath);
        Assert.Equal(Architecture, extra.LearnedRunner);
        Assert.Null(extra.ManifestRunner);
        Assert.Equal(3, results.Count(row => row.Kind == ManifestCheckResultKind.Match));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compare_EmittedWithSameAnswers_ReportsAllMatch(bool liveExtraQuestion)
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        if (liveExtraQuestion)
            model = model with
            {
                OwnerQuestions = [.. model.OwnerQuestions,
                    new ProjectOwnerQuestion("commands/./extra/architecture", "Which architecture command?", new FactSource("package.json", 1))]
            };
        OwnerAnswer[] extras = [Extra("commands/./extra/prose", Prose), Extra("commands/./extra/architecture", Architecture)];
        var manifest = Emit(model, extras);
        var applied = OwnerAnswerApplier.Apply(model, [.. Fixture.Answers(), .. extras]);

        var results = ManifestCheckComparer.Compare(model, manifest.ToJsonString(), applied);

        Assert.Equal(5, results.Count);
        Assert.All(results, row =>
        {
            Assert.Equal(ManifestCheckResultKind.Match, row.Kind);
            Assert.Equal(row.LearnedRunner, row.ManifestRunner);
        });
        Assert.Contains(results, row => row.LearnedRunner == Architecture);
        Assert.Contains(results, row => row.LearnedRunner == Prose);
    }

    private static OwnerAnswer Extra(string key, string value) => new(key, value, "fixture", DateTimeOffset.UnixEpoch);

    private static JsonNode Emit(ProjectModel model, params OwnerAnswer[] extras)
    {
        var result = LearnedAcceptanceManifest.Build(model, [.. Fixture.Answers(), .. extras]);
        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        return JsonNode.Parse(result.Json)!;
    }

    private static JsonObject Command(string name, string command, params string[] arguments) => new()
    {
        ["name"] = name,
        ["type"] = "command",
        ["command"] = command,
        ["arguments"] = new JsonArray(arguments.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray())
    };

    private static void AssertChecks(JsonNode expected, JsonNode manifest) =>
        Assert.True(JsonNode.DeepEquals(expected, manifest["checks"]), manifest["checks"]!.ToJsonString());
}
