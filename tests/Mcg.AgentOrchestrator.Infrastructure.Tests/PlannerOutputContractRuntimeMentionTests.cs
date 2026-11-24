using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its checkout and index.
public sealed class PlannerOutputContractRuntimeMentionTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void Resolve_NamespaceExcerpt_Succeeds()
    {
        const string excerpt = "  - a pure `FailureClusterInputParser` (`ParseGoalEventLine` and `ParseConductEventLine` over `System.Text.Json`) that skips torn or malformed lines.";

        var result = ResolveExcerpt(excerpt);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains(excerpt, result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("  - It is stored camelCase in `landing-escalations.json` and locked through `landing-escalations.lock` (`src/OperatorInbox.cs:106`, `src/OperatorInbox.cs:107`).")]
    [Xunit.InlineData("  - The host keeps its state file `host-health-state.json` in `OrchestratorDirectory`.")]
    [Xunit.InlineData("  - Observe `/.orchestrator/conductor-policy.json` for runtime policy state.")]
    [Xunit.InlineData("  - Observe `$artifactsPath\\worker-build-receipt.json` for build evidence.")]
    [Xunit.InlineData("  - Observe `.orchestrator/conductor-policy.json` for runtime policy state.")]
    [Xunit.InlineData("  - Observe `.orchestrator\\conductor-policy.json` for runtime policy state.")]
    [Xunit.InlineData("  - Observe `\\.ORCHESTRATOR\\conductor-policy.json` for runtime policy state.")]
    [Xunit.InlineData("  - Inspect `$sourcePath\\Missing.cs` for the script-provided source path.")]
    public void Resolve_RuntimeArtifactExcerpt_Succeeds(string excerpt)
    {
        var result = ResolveExcerpt(excerpt);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains(excerpt.Trim(), result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerDirective_AdvertisesFixturePathMarker()
    {
        var directive = AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner)[0];

        Xunit.Assert.Contains("`(fixture path)`", directive, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("  - Inspect `tests/Shared.cs` (fixture path).")]
    [Xunit.InlineData("  - Inspect `tests/Fixtures/Nested/Shared.cs`\t(fixture path) for shared test code.")]
    public void Resolve_FixturePathMarker_PreservesExcerpt(string excerpt)
    {
        var result = ResolveExcerpt(excerpt);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains(excerpt.Trim(), result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("  - Inspect `tests/Shared.cs`.", "tests/Shared.cs")]
    [Xunit.InlineData("  - Inspect `Shared.cs` (fixture path).", "Shared.cs")]
    [Xunit.InlineData("  - Inspect `tests/*.cs` (fixture path).", "tests/*.cs")]
    [Xunit.InlineData("  - Inspect `tests/Shared.cs` alongside `src/Present.cs` (fixture path).", "tests/Shared.cs")]
    public void Resolve_FixturePathWithoutAdjacentEligibleMarker_KeepsDiagnostic(string excerpt, string citation)
    {
        var result = ResolveExcerpt(excerpt);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains(
            $"target citation '{citation}' does not exist and is not marked as a new file",
            result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/Absent/settings.json")]
    [Xunit.InlineData("Missing.cs")]
    [Xunit.InlineData("Missing.csproj")]
    [Xunit.InlineData("Missing.ps1")]
    [Xunit.InlineData("Missing.props")]
    [Xunit.InlineData("Missing.targets")]
    [Xunit.InlineData("Missing.md")]
    [Xunit.InlineData("Missing.txt")]
    [Xunit.InlineData("Missing.yml")]
    [Xunit.InlineData("Missing.yaml")]
    [Xunit.InlineData("lease/lease.json")]
    [Xunit.InlineData(".orchestrator-other/settings.json")]
    [Xunit.InlineData("*.json")]
    [Xunit.InlineData(".orchestrator/*.json")]
    [Xunit.InlineData(".orchestrator/settings?.json")]
    [Xunit.InlineData("$1.cs")]
    [Xunit.InlineData("${var}/Missing.cs")]
    public void Resolve_MissingCitation_KeepsDiagnostic(string citation)
    {
        var result = ResolveExcerpt($"  - Inspect `{citation}` for the concrete implementation seam.");

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains(
            $"target citation '{citation}' does not exist and is not marked as a new file",
            result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Resolve_AmbiguousBareJson_KeepsDiagnostic()
    {
        var workingDirectory = CreateFixtureRepository();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "src", "Other"));
        File.WriteAllText(Path.Combine(workingDirectory, "src", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(workingDirectory, "src", "Other", "settings.json"), "{}");
        ReadGit(workingDirectory, ["add", "--", "src/settings.json", "src/Other/settings.json"]);
        var plan = PlanWithExcerpt("  - Inspect `settings.json` for configuration.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'settings.json' is ambiguous", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("'src/settings.json'", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("'src/Other/settings.json'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("global.json", "./global.json")]
    [Xunit.InlineData("config/global.json", "config/global.json")]
    public void Resolve_WrongCaseTrackedJson_RejectsWithCanonicalPath(string trackedPath, string explicitPath)
    {
        var root = CreateFixtureRepository();
        var path = Path.Combine(root, trackedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        StagePlannerCitationFixtures(root, trackedPath);

        var plan = PlanWithExcerpt("  - Inspect `Global.json:43` for configuration.");
        var result = PlannerOutputContract.Resolve(plan, string.Empty, root);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains(
            "target citation 'Global.json:43' does not exist and is not marked as a new file",
            result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Did you mean `{explicitPath}:43`?", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.True(PlannerOutputContract.TryValidatePlan(
            plan.Replace("Global.json:43", $"{explicitPath}:43", StringComparison.Ordinal),
            root, out _, out var diagnostic), diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Global.json:43", "No tracked file matches the short citation.", new[] { "config/global.json" })]
    [Xunit.InlineData("global.json:43", "is ambiguous", new[] { "config/global.json", "other/global.json" })]
    public void ValidatePlan_UnavailableIndexRejectsBareJsonCitation(
        string citation, string expectedHealthyDiagnostic, string[] trackedPaths)
    {
        var root = CreateFixtureRepository();
        var plan = PlanWithExcerpt($"  - Inspect `{citation}` for configuration.");
        var healthyIndex = new GitCli.GitResult(0, string.Join('\0', trackedPaths) + '\0', string.Empty);

        Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(plan, root, out _, out var healthyDiagnostic,
            readTrackedIndex: () => healthyIndex));
        Xunit.Assert.Contains(expectedHealthyDiagnostic, healthyDiagnostic, StringComparison.Ordinal);

        var failedIndex = healthyIndex with { ExitCode = 1, Error = "injected index read failure" };
        Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(plan, root, out _, out var failedDiagnostic,
            readTrackedIndex: () => failedIndex));
        Xunit.Assert.Contains("the checkout's tracked file list was unavailable", failedDiagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", failedDiagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void BareRuntimeJsonMentionDoesNotResolveToAnUntrackedSourceFile()
    {
        var root = CreateFixtureRepository();
        File.WriteAllText(Path.Combine(root, "settings.json.cs"), "// untracked source decoy");
        const string excerpt = "  - Inspect `settings.json` for the runtime configuration.";

        var result = PlannerOutputContract.Resolve(PlanWithExcerpt(excerpt), string.Empty, root);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains(excerpt.Trim(), result.Plan, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("settings.json.cs", result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/Present.cs:12-14")]
    [Xunit.InlineData("src/Present.cs#l12-l14")]
    [Xunit.InlineData("src/Present.cs::SomeMethod")]
    public void Resolve_SourceSuffix_Succeeds(string citation)
    {
        var result = ResolveExcerpt($"  - Inspect `{citation}` for the concrete implementation seam.");

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    private static PlannerOutputContractResult ResolveExcerpt(string excerpt) =>
        PlannerOutputContract.Resolve(PlanWithExcerpt(excerpt), string.Empty, CreateFixtureRepository());

    private static string CreateFixtureRepository()
    {
        var workingDirectory = CreateSeededDispatchRepository();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "src"));
        File.WriteAllText(Path.Combine(workingDirectory, "src", "Present.cs"), "// fixture");
        File.WriteAllText(Path.Combine(workingDirectory, "src", "OperatorInbox.cs"), "// fixture");
        return workingDirectory;
    }

    private static string PlanWithExcerpt(string excerpt)
    {
        var normalized = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var bodyStart = normalized.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var nextHeading = normalized.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return normalized[..bodyStart] + "\n\n- Inspect `src/Present.cs` for the concrete implementation seam.\n" +
            excerpt + "\n" + normalized[nextHeading..];
    }
}
