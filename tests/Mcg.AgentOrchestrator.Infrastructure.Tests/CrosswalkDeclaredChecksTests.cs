using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = LuaProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: unique filesystem roots and injected crosswalk responses, no ambient state.
public sealed class CrosswalkDeclaredChecksTests
{
    [Fact]
    public void Build_CompleteAnswers_EmitsDeclaredFirstWithFieldParity()
    {
        using var fixture = new Fixture();
        var model = CrosswalkModel(fixture);
        var answers = Answers();

        var manifest = Manifest(model, answers);
        var checks = manifest["checks"]!.AsArray();
        Assert.Equal(["declared-build", "declared-test", "crosswalk-test ."], Names(checks));

        var declared = Manifest(new DeclaredProjectDiscoveryAdapter().Discover(fixture.Root), answers);
        var declaredChecks = declared["checks"]!.AsArray();
        Assert.Equal(2, declaredChecks.Count);
        Assert.True(JsonNode.DeepEquals(declaredChecks[0], checks[0]));
        Assert.True(JsonNode.DeepEquals(declaredChecks[1], checks[1]));

        var crosswalkOnly = Manifest(model, Fixture.Answers());
        Assert.True(JsonNode.DeepEquals(crosswalkOnly["checks"]![0], checks[2]));
    }

    [Fact]
    public void Build_BuildNone_OmitsBuildCheckAndTool()
    {
        using var fixture = new Fixture();
        var model = CrosswalkModel(fixture);
        var answers = Answers("none");

        var manifest = Manifest(model, answers);

        Assert.Equal(["declared-test", "crosswalk-test ."], Names(manifest["checks"]!.AsArray()));
        var contract = EnvironmentContractDeriver.ForModel(model, OwnerAnswerApplier.Apply(model, answers));
        Assert.Equal(["crosswalk", "gradle", "node"], contract.Tools.Select(tool => tool.Name));
        Assert.Equal(contract.Tools.Select(tool => tool.Name),
            EnvironmentContract.FromManifest(manifest.ToJsonString()).Tools.Select(tool => tool.Name));
    }

    [Theory]
    [InlineData("all")]
    [InlineData(DeclaredProjectDiscoveryAdapter.BuildQuestion)]
    [InlineData(DeclaredProjectDiscoveryAdapter.TestQuestion)]
    [InlineData(DeclaredProjectDiscoveryAdapter.ReportsQuestion)]
    public void Build_MissingDeclaredAnswer_PreservesCrosswalkOnly(string missing)
    {
        using var fixture = new Fixture();
        var model = CrosswalkModel(fixture);
        var answers = Answers().Where(answer => missing == "all"
            ? !answer.FactKey.StartsWith("commands/declared/", StringComparison.Ordinal)
            : answer.FactKey != missing).ToArray();

        Assert.Equal(["declared-build", "declared-test", "crosswalk-test ."],
            Names(Manifest(model, Answers())["checks"]!.AsArray()));
        AssertCrosswalkOnly(model, answers);
    }

    [Theory]
    [InlineData(DeclaredProjectDiscoveryAdapter.BuildQuestion, "compiler && other")]
    [InlineData(DeclaredProjectDiscoveryAdapter.TestQuestion, "node | other")]
    [InlineData(DeclaredProjectDiscoveryAdapter.ReportsQuestion, "../junit.xml")]
    public void Build_InvalidDeclaredAnswer_BlocksWithValidationReason(string key, string value)
    {
        using var fixture = new Fixture();
        var model = CrosswalkModel(fixture);
        var answers = Answers().Select(answer => answer.FactKey == key
            ? new OwnerAnswer(key, value, answer.Reason, answer.RecordedUtc) : answer).ToArray();

        var result = LearnedAcceptanceManifest.Build(model, answers);

        Assert.Null(result.Json);
        var blocker = Assert.Single(result.Blockers);
        Assert.Equal(key, blocker.FactKey);
        var validation = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(key, value));
        Assert.Equal(validation.Message, blocker.Reason);
        var tools = EnvironmentContractDeriver.ForModel(model, OwnerAnswerApplier.Apply(model, answers)).Tools;
        Assert.Equal(["crosswalk", "gradle"], tools.Select(tool => tool.Name));
    }

    [Theory]
    [InlineData(DeclaredProjectDiscoveryAdapter.BuildQuestion, DeclaredProjectDiscoveryAdapter.TestQuestion, "node | other")]
    [InlineData(DeclaredProjectDiscoveryAdapter.TestQuestion, DeclaredProjectDiscoveryAdapter.ReportsQuestion, "../junit.xml")]
    [InlineData(DeclaredProjectDiscoveryAdapter.ReportsQuestion, DeclaredProjectDiscoveryAdapter.BuildQuestion, "compiler && other")]
    public void Build_InvalidAndMissingDeclaredAnswers_PreservesCrosswalkOnly(string missing, string key, string value)
    {
        using var fixture = new Fixture();
        var model = CrosswalkModel(fixture);
        var answers = Answers().Where(answer => answer.FactKey != missing).Select(answer => answer.FactKey == key
            ? new OwnerAnswer(key, value, answer.Reason, answer.RecordedUtc) : answer).ToArray();

        AssertCrosswalkOnly(model, answers);
    }

    [Fact]
    public void ForModel_CompleteAnswers_IncludesDeclaredAndCrosswalkTools()
    {
        using var fixture = new Fixture();
        var model = CrosswalkModel(fixture);
        var answers = Answers();

        var contract = EnvironmentContractDeriver.ForModel(model, OwnerAnswerApplier.Apply(model, answers));

        Assert.Equal(["crosswalk", "gradle", "compiler", "node"], contract.Tools.Select(tool => tool.Name));
        var manifest = Manifest(model, answers);
        Assert.True(JsonNode.DeepEquals(manifest["environmentContract"], contract.ToJson()));
    }

    [Fact]
    public void ForModel_DuplicateTools_EmitsEachRequirementOnce()
    {
        using var fixture = new Fixture();
        var model = CrosswalkModel(fixture);
        var answers = Answers("crosswalk build");

        var contract = EnvironmentContractDeriver.ForModel(model, OwnerAnswerApplier.Apply(model, answers));

        Assert.Equal(["crosswalk", "gradle", "node"], contract.Tools.Select(tool => tool.Name));
        var manifest = Manifest(model, answers);
        Assert.Equal(["declared-build", "declared-test", "crosswalk-test ."], Names(manifest["checks"]!.AsArray()));
        Assert.True(JsonNode.DeepEquals(manifest["environmentContract"], contract.ToJson()));
    }

    [Fact]
    public void Build_RepeatedDeclaredAnswer_UsesLatestRecordedCommand()
    {
        using var fixture = new Fixture();
        var model = CrosswalkModel(fixture);
        var newer = new OwnerAnswer(DeclaredProjectDiscoveryAdapter.BuildQuestion, "newcompiler compile",
            "fixture", DateTimeOffset.UnixEpoch.AddDays(1));
        OwnerAnswer[] answers = [newer, .. Answers()];

        var checks = Manifest(model, answers)["checks"]!.AsArray();

        Assert.Equal(["declared-build", "declared-test", "crosswalk-test ."], Names(checks));
        Assert.Equal("newcompiler", checks[0]!["command"]!.GetValue<string>());
        Assert.Equal(["compile"], checks[0]!["arguments"]!.AsArray().Select(argument => argument!.GetValue<string>()));
    }

    private static void AssertCrosswalkOnly(ProjectModel model, OwnerAnswer[] answers)
    {
        var result = LearnedAcceptanceManifest.Build(model, answers);
        Assert.DoesNotContain(result.Blockers,
            blocker => blocker.FactKey.StartsWith("commands/declared/", StringComparison.Ordinal));
        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        var checks = JsonNode.Parse(result.Json)!["checks"]!.AsArray();
        Assert.Equal(["crosswalk-test ."], Names(checks));
        Assert.False(Assert.Single(checks)!.AsObject().ContainsKey("command"));
        Assert.Equal(LearnedAcceptanceManifest.Build(model, Fixture.Answers()).Json, result.Json);
        var tools = EnvironmentContractDeriver.ForModel(model, OwnerAnswerApplier.Apply(model, answers)).Tools;
        Assert.Equal(["crosswalk", "gradle"], tools.Select(tool => tool.Name));
    }

    private static ProjectModel CrosswalkModel(Fixture fixture)
    {
        using var crosswalk = new FakeCrosswalkProcess("jvm", "gradle");
        crosswalk.ListTests("module:Suite#first", "module:Suite#second");
        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);
        Assert.IsType<CrosswalkProjectDiscoveryAdapter>(adapter);
        var model = adapter.Discover(fixture.Root);
        Assert.Equal(ProjectStack.Crosswalk, model.Stack);
        return model;
    }

    private static OwnerAnswer[] Answers(string build = "compiler build") => Fixture.Answers(
        new(DeclaredProjectDiscoveryAdapter.BuildQuestion, build, "fixture", DateTimeOffset.UnixEpoch),
        new(DeclaredProjectDiscoveryAdapter.TestQuestion,
            "node --test --test-reporter=junit --test-reporter-destination=reports/junit.xml", "fixture", DateTimeOffset.UnixEpoch),
        new(DeclaredProjectDiscoveryAdapter.ReportsQuestion, "reports/*.xml,results/**/*.xml", "fixture", DateTimeOffset.UnixEpoch));

    private static JsonNode Manifest(ProjectModel model, OwnerAnswer[] answers)
    {
        var result = LearnedAcceptanceManifest.Build(model, answers);
        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        return JsonNode.Parse(result.Json)!;
    }

    private static IEnumerable<string> Names(JsonArray checks) =>
        checks.Select(check => check!["name"]!.GetValue<string>());
}
