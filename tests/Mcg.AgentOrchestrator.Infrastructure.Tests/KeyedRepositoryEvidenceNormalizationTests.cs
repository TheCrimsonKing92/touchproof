using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all goals, settings and target resolution are in memory.
public sealed class KeyedRepositoryEvidenceNormalizationTests
{
    internal static AcceptanceGateEngineSettings HomeSettings => AcceptanceGateEngineSettings.Parse("""
        {"checks":[{"type":"dotnet-test","project":"tests/Sample.Tests/Sample.Tests.csproj"}]}
        """);

    [Theory]
    [InlineData("crosswalk")]
    [InlineData("Crosswalk")]
    public void Normalize_DeclaredGoRepository_UsesKeyedSettings(string key)
    {
        var goal = CreateGoal();
        var target = Target();
        var resolverCalls = 0;
        var siblingCalls = 0;
        var accepted = FindingEvidenceRequestNormalization.TryNormalizeForGoal(Request(key), goal, HomeSettings,
            (requestedGoal, suppliedKey) =>
            {
                Assert.Same(goal, requestedGoal);
                Assert.Equal(key, suppliedKey);
                resolverCalls++;
                return (target, string.Empty);
            }, (_, _) => { siblingCalls++; return ["HomeOnlyTests"]; },
            out var normalized, out var executor, out var resolved, out _, out var detail);
        Assert.True(accepted, detail);
        Assert.Equal(1, resolverCalls);
        Assert.Equal(0, siblingCalls);
        Assert.Same(target, resolved);
        Assert.Equal(key, normalized.Repository);
        Assert.Equal(new FindingEvidenceSelection("record", "TestReadInvalidFixtures"), Assert.Single(normalized.Selections));
        Assert.Equal("record:TestReadInvalidFixtures", executor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void Normalize_HomeRequest_RetainsHomeProjectResolution(string? key)
    {
        var goal = CreateGoal();
        Assert.False(FindingEvidenceRequestNormalization.TryNormalizeForGoal(Request(key), goal, HomeSettings,
            (_, _) => throw new InvalidOperationException("Home request called keyed resolver"), (_, _) => [],
            out _, out _, out var target, out var reason, out var detail));
        Assert.Null(target);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, reason);
        Assert.Contains("test project './record'", detail, StringComparison.Ordinal);

        var home = new FindingEvidenceRequest([new("Sample.Tests", "ProbeTests")], Repository: key);
        Assert.True(FindingEvidenceRequestNormalization.TryNormalizeForGoal(home, goal, HomeSettings,
            (_, _) => throw new InvalidOperationException("Home request called keyed resolver"), (_, _) => [],
            out var normalized, out var executor, out target, out _, out detail), detail);
        Assert.Null(target);
        Assert.Equal(key, normalized.Repository);
        Assert.Equal(new FindingEvidenceSelection("tests/Sample.Tests/Sample.Tests.csproj", "ProbeTests"), Assert.Single(normalized.Selections));
        Assert.True(FindingEvidenceRequestNormalization.TryNormalize(home, HomeSettings, (_, _) => [],
            out var legacy, out var legacyExecutor, out _, out detail), detail);
        Assert.Equal(legacy.Selections, normalized.Selections);
        Assert.Equal("tests/Sample.Tests/Sample.Tests.csproj:ProbeTests", executor);
        Assert.Equal(legacyExecutor, executor);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("crosswalk", false)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData(" crosswalk", true)]
    [InlineData("crosswalk ", true)]
    [InlineData("croſswalk", true)]
    public void Normalize_UnknownKey_RefusesAndListsActualDeclaration(string key, bool declared)
    {
        Assert.False(FindingEvidenceRequestNormalization.TryNormalizeForGoal(Request(key), CreateGoal(declared), HomeSettings,
            (_, _) => throw new InvalidOperationException("Invalid key reached resolver"), (_, _) => [],
            out _, out _, out var target, out var reason, out var detail));
        Assert.Null(target);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, reason);
        Assert.Contains(declared ? "Declared keys: crosswalk, default." : "Declared keys: none (no keys are declared).", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_MissingTarget_RefusesWithoutHomeFallback()
    {
        Assert.False(FindingEvidenceRequestNormalization.TryNormalizeForGoal(Request("crosswalk"), CreateGoal(), HomeSettings,
            (_, _) => (null, "crosswalk:missing-worktree"), (_, _) => throw new InvalidOperationException("Home sibling lookup"),
            out _, out _, out var target, out var reason, out var detail));
        Assert.Null(target);
        Assert.Equal(FindingEvidenceNotHonouredReason.ExecutorUnavailable, reason);
        Assert.Contains("crosswalk:missing-worktree", detail, StringComparison.Ordinal);
        Assert.Contains("home resources are never substituted", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_LegacyEntry_RefusesNonDefaultKeys()
    {
        var request = new FindingEvidenceRequest([new("Sample.Tests", "ProbeTests")], Repository: "crosswalk");
        Assert.False(FindingEvidenceRequestNormalization.TryNormalize(request, HomeSettings, (_, _) => [],
            out _, out _, out var reason, out var detail));
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, reason);
        Assert.Contains("goal-aware finding-evidence path", detail, StringComparison.Ordinal);
        Assert.True(FindingEvidenceRequestNormalization.TryNormalize(request with { Repository = null }, HomeSettings, (_, _) => [],
            out _, out _, out _, out detail), detail);
    }

    internal static Goal CreateGoal(bool declared = true)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Repository evidence", [new(TaskId.New(), "Test", AgentRole.Tester)]);
        if (declared) kernel.DeclareGoalRepositories(goal.Id, new GoalRepositoryDeclaration(["default", "crosswalk"],
            new Dictionary<string, string>
            {
                ["default"] = Path.Combine(Path.GetTempPath(), "evidence-home"),
                ["crosswalk"] = Path.Combine(Path.GetTempPath(), "evidence-crosswalk")
            }));
        return goal;
    }

    private static FindingEvidenceRequest Request(string? key) =>
        new([new("./record", "TestReadInvalidFixtures")], Repository: key);

    internal static KeyedRepositoryEvidenceTarget Target() => new("crosswalk", "crosswalk-worktree", "crosswalk-home", "trunk",
        AcceptanceGateEngineSettings.Parse(GoPackageFocusedEvidenceTests.Manifest), "bbbbbbb");
}
