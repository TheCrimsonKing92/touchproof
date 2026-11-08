using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: immutable manifests and planning without filesystem or global state.
public sealed class RustNextestFocusedEvidenceTests
{
    internal const string ModuleTest = "fleet::tests::parses_projects_and_rejects_malformed_registries";
    internal const string IntegrationTest = "parses_registry_from_disk";
    internal const string Report = "target/nextest/ci/junit.xml";
    internal const string Manifest = """
        {"version":1,"checks":[{"name":"declared-test","type":"junit-command","command":"cargo",
        "arguments":["nextest","run","--profile","ci"],"project":".","reports":["target/nextest/ci/junit.xml"]}]}
        """;

    [Theory]
    [InlineData(ModuleTest)]
    [InlineData(IntegrationTest)]
    public void Plan_CargoAlias_UsesExactNameAndDeclaredCiInvocation(string selector)
    {
        var settings = AcceptanceGateEngineSettings.Parse(Manifest);
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject("Cargo.toml", settings, out var project));
        Assert.Equal("declared-test", project);
        var check = Plan("Cargo.toml:" + selector, settings, out var coverage);
        Assert.Equal("junit-command", check.Type);
        Assert.Equal("cargo", check.Command);
        Assert.Equal(".", check.Project);
        Assert.Equal([Report], check.Reports);
        Assert.Equal(["nextest", "run", "--profile", "ci", "-E", $"test(={selector})"], check.Arguments);
        Assert.True(check.IsFocusedEvidenceSelection);
        Assert.True(RustNextestFocusedEvidence.IsEvidenceCheck(check));
        Assert.Equal("rust-nextest-focused", coverage.ExecutionReason);
        Assert.Equal("Cargo.toml:" + selector, Assert.Single(coverage.TargetToChecks).Target);
    }

    [Fact]
    public void Plan_MultipleSelectors_ProducesOneSortedUnionCheck()
    {
        var check = Plan($"Cargo.toml:{IntegrationTest};declared-test:{ModuleTest};Cargo.toml:{ModuleTest}",
            AcceptanceGateEngineSettings.Parse(Manifest), out var coverage);
        Assert.Equal(["nextest", "run", "--profile", "ci", "-E", $"test(={ModuleTest})|test(={IntegrationTest})"], check.Arguments);
        Assert.Equal(3, coverage.TargetToChecks.Count);
        Assert.All(coverage.TargetToChecks, target => Assert.Equal(check.Name, Assert.Single(target.CheckNames)));
    }

    [Theory]
    [InlineData("test(=name)")]
    [InlineData("first|second")]
    [InlineData("first&second")]
    [InlineData("has space")]
    [InlineData(" name")]
    [InlineData("name ")]
    [InlineData("a::::b")]
    [InlineData("a:b")]
    [InlineData("a::")]
    [InlineData("a::é")]
    [InlineData("=name")]
    [InlineData("a/b")]
    [InlineData("9name")]
    [InlineData("")]
    public void Plan_InvalidSelector_RefusesWithoutChecks(string selector)
    {
        Assert.False(RustNextestFocusedEvidence.IsSupportedSelector(selector));
        Assert.False(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks("Cargo.toml:" + selector,
            AcceptanceGateEngineSettings.Parse(Manifest), "unused", out var checks, out var coverage, out var rejection));
        Assert.Equal(FocusedEvidenceRejectionCode.UnsupportedToken, rejection.Code);
        Assert.Empty(checks);
        Assert.Empty(coverage.TargetToChecks);
    }

    [Theory]
    [InlineData("first|second")]
    [InlineData(" name")]
    [InlineData("name ")]
    [InlineData("")]
    public void Plan_MixedSelectors_RefusesEntireRequest(string invalidSelector)
    {
        var valid = "Cargo.toml:" + ModuleTest;
        var invalid = "Cargo.toml:" + invalidSelector;
        foreach (var request in new[] { valid + ";" + invalid, invalid + ";" + valid })
        {
            Assert.False(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(request,
                AcceptanceGateEngineSettings.Parse(Manifest), "unused", out var checks,
                out var coverage, out var rejection));
            Assert.Equal(FocusedEvidenceRejectionCode.UnsupportedToken, rejection.Code);
            Assert.Empty(checks);
            Assert.Empty(coverage.TargetToChecks);
        }
    }

    [Theory]
    [InlineData("nextest", "list")]
    [InlineData("nextest", "run", "--profile", "default")]
    [InlineData("nextest", "run", "--profile", "ci", "--profile=ci")]
    [InlineData("nextest", "run", "--profile")]
    [InlineData("nextest", "run", "-E", "all()")]
    [InlineData("nextest", "run", "-Eall()")]
    [InlineData("nextest", "run", "--filter-expr=all()")]
    [InlineData("nextest", "run", "--filterset", "all()")]
    [InlineData("nextest", "run", "--")]
    public void Plan_ConflictingInvocation_RefusesWithoutChecks(params string[] arguments)
    {
        var settings = Settings(arguments);
        Assert.True(RustNextestFocusedEvidence.Applies(settings));
        Assert.False(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks("Cargo.toml:" + ModuleTest,
            settings, "unused", out var checks, out _, out var rejection));
        Assert.Equal(FocusedEvidenceRejectionCode.UnsupportedToken, rejection.Code);
        Assert.Empty(checks);
    }

    [Theory]
    [InlineData("nextest", "run")]
    [InlineData("nextest", "run", "--profile=ci")]
    [InlineData("nextest", "run", "-P", "ci")]
    public void Plan_CompatibleProfile_PreservesDeclaration(params string[] arguments)
    {
        var check = Plan("Cargo.toml:" + IntegrationTest, Settings(arguments), out _);
        var expected = arguments.Concat(arguments.Length == 2 ? new[] { "--profile", "ci" } : [])
            .Concat(["-E", $"test(={IntegrationTest})"]);
        Assert.Equal(expected, check.Arguments);
        Assert.Equal(7, check.TimeoutMinutes);
        Assert.True(check.Advisory);
        Assert.Equal("value", check.Environment!["NEXTEST_FIXTURE"]);
        Assert.Equal("rust", check.Project);
    }

    [Theory]
    [InlineData()]
    [InlineData("../outside.xml")]
    [InlineData("/outside.xml")]
    [InlineData(Report, "second.xml")]
    public void Plan_UnusableReports_RefusesWithoutChecks(params string[] reports)
    {
        var settings = Settings(["nextest", "run"], reports);
        Assert.False(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks("Cargo.toml:" + ModuleTest,
            settings, "unused", out var checks, out _, out var rejection));
        Assert.Equal(FocusedEvidenceRejectionCode.UnsupportedToken, rejection.Code);
        Assert.Empty(checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_CommandDeclarationInEitherSettingsBranch_UsesFirstCheck(bool engine)
    {
        var manifest = Manifest.Replace("\"type\":\"junit-command\"", "\"type\":\"command\"");
        if (engine) manifest = manifest.Replace("\"version\":1", "\"version\":1,\"engine\":{}");
        var settings = AcceptanceGateEngineSettings.Parse(manifest);
        Assert.True(RustNextestFocusedEvidence.Applies(settings));
        Assert.Equal("declared-test", settings.DeclaredRustNextestCheck!.Name);
        Assert.Equal("junit-command", Plan("Cargo.toml:" + ModuleTest, settings, out _).Type);
        Assert.False(RustNextestFocusedEvidence.TryResolveProject(" ", settings, out _));
        var twice = manifest.Replace("\"checks\":[", "\"checks\":[{\"name\":\"first\",\"type\":\"command\",\"command\":\"cargo\",\"arguments\":[\"nextest\",\"run\"]},");
        Assert.Equal("first", AcceptanceGateEngineSettings.Parse(twice).DeclaredRustNextestCheck!.Name);
    }

    [Theory]
    [InlineData("cargo", "nextest-other")]
    [InlineData("cargo.exe", "nextest")]
    [InlineData("wrapper", "nextest")]
    public void Parse_UnrelatedCommand_DoesNotDeclareNextest(string command, string argument)
    {
        var manifest = Manifest.Replace("\"cargo\"", JsonSerializer.Serialize(command))
            .Replace("\"nextest\"", JsonSerializer.Serialize(argument));
        Assert.False(RustNextestFocusedEvidence.Applies(AcceptanceGateEngineSettings.Parse(manifest)));
    }

    [Theory]
    [InlineData("dotnet-test", false)]
    [InlineData("dotnet-test", true)]
    [InlineData("go", false)]
    [InlineData("go", true)]
    [InlineData("crosswalk-test", false)]
    [InlineData("crosswalk-test", true)]
    public void Applies_OtherEngineDeclarations_PreservesTheirRouting(string engine, bool nextest)
    {
        var declaration = engine switch
        {
            "go" => """{"name":"go test","type":"command","command":"go","arguments":["test","./..."]}""",
            "crosswalk-test" => """{"name":"crosswalk selection","type":"crosswalk-test","project":"."}""",
            _ => """{"name":"dotnet","type":"dotnet-test","project":"tests/Sample.Tests/Sample.Tests.csproj"}"""
        };
        var manifest = nextest ? Manifest.Replace("\"checks\":[", "\"checks\":[" + declaration + ",")
            : "{\"checks\":[" + declaration + "]}";
        var settings = AcceptanceGateEngineSettings.Parse(manifest);
        Assert.False(RustNextestFocusedEvidence.Applies(settings));
        Assert.False(RustNextestFocusedEvidence.TryResolveProject("Cargo.toml", settings, out _));
        var request = engine switch
        {
            "go" => "internal/drift:TestMissingColumn",
            "crosswalk-test" => ".:src/unit.test.ts::case",
            _ => "tests/Sample.Tests/Sample.Tests.csproj:mapped-project"
        };
        var check = Plan(request, settings, out _);
        Assert.False(RustNextestFocusedEvidence.IsEvidenceCheck(check));
        if (engine == "go") Assert.True(GoPackageFocusedEvidence.IsEvidenceCheck(check));
        else if (engine == "crosswalk-test") Assert.True(CrosswalkFocusedEvidence.IsEvidenceCheck(check));
        else Assert.Equal("dotnet-test", check.Type);
    }

    [Fact]
    public void Plan_ColonInDeclaredName_PreservesCompleteCanonicalLabel()
    {
        var settings = AcceptanceGateEngineSettings.Parse(Manifest.Replace("declared-test", "rust:ci"));
        var check = Plan("rust:ci:" + ModuleTest, settings, out _);
        Assert.Equal(["rust:ci", ModuleTest], RustNextestFocusedEvidence.SplitTarget(check, "rust:ci:" + ModuleTest));
        Assert.False(RustNextestFocusedEvidence.TryBuild(ModuleTest, settings, "unused", out var checks, out _, out _));
        Assert.Empty(checks);
    }

    internal static Check Plan(string request, AcceptanceGateEngineSettings settings, out FocusedEvidenceCoverage coverage)
    {
        Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(request, settings, "unused",
            out var checks, out coverage, out var rejection), rejection.Detail);
        return Assert.Single(checks);
    }

    private static AcceptanceGateEngineSettings Settings(string[] arguments, string[]? reports = null) =>
        AcceptanceGateEngineSettings.Parse(JsonSerializer.Serialize(new
        {
            checks = new[] { new { name = "declared-test", type = "junit-command", command = "cargo", arguments,
                reports = reports ?? [Report], project = "rust", timeoutMinutes = 7, advisory = true,
                environment = new Dictionary<string, string> { ["NEXTEST_FIXTURE"] = "value" } } }
        }));
}
