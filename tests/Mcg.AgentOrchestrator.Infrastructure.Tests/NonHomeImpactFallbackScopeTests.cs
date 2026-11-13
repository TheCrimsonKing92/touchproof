using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifest = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifest;

// Parallel-safe: each case owns a foreign candidate and project home; no processes or ambient switches.
public sealed class NonHomeImpactFallbackScopeTests
{
    [Theory]
    [InlineData(NonHomeSelectionFixture.Source, "unmapped")]
    [InlineData(NonHomeSelectionFixture.Test, "unmapped")]
    [InlineData("tools/build.ps1", "script-config")]
    [InlineData("settings/widget.json", "script-config")]
    [InlineData("Directory.Build.props", "build")]
    [InlineData("src/Widget/Product/Credential.cs", "security")]
    public void ForeignFallback_ReturnsOnlyDeclaredProjectsOrNoChecks(string changed, string branch)
    {
        using var fixture = new NonHomeSelectionFixture();
        Assert.False(GoalAcceptanceVerifierHomeImpactScope.Applies(fixture.Home));
        Assert.Same(CandidateTreeProbe.AssumeAllPresent, CandidateTreeProbe.ForRepositoryRoot(fixture.Worktree));
        var raw = RepositoryTestImpactPlanner.Plan([changed], fixture.Worktree);
        Assert.True(raw.RequiresBuild);
        Assert.NotEmpty(raw.Checks);
        Assert.All(raw.Checks, check => Assert.Contains(ProjectArgument(check.Command),
            RepositoryTestImpactPlanner.BuiltInTestProjectPaths));
        switch (branch)
        {
            case "unmapped":
                Assert.Equal("Changed files do not map to a narrower test target; run the full relevant dotnet suite.", raw.Summary);
                break;
            case "script-config":
                Assert.Equal("Infrastructure, script, or configuration behavior changed.", Assert.Single(raw.Checks).Reason);
                break;
            case "build":
                Assert.Equal("Build configuration changed; run the full relevant dotnet suite.", raw.Summary);
                break;
            case "security":
                Assert.Equal("Security, sandbox, credential, or policy-sensitive paths changed; run broad tests and manual review.", raw.Summary);
                break;
            default: throw new InvalidOperationException("Unknown fallback branch.");
        }

        var manifest = AcceptanceManifest.Load(fixture.Worktree, [changed], fixture.Home);
        var declared = ManifestDeclaredTestProjects.From(manifest.Checks.Select(check => check.Project));
        Assert.True(declared.Declares(NonHomeSelectionFixture.Project));
        var scoped = ManifestScopedImpactPlan.Plan([changed], fixture.Worktree, manifest.Checks);
        Assert.All(scoped.Checks, check => Assert.True(declared.Declares(ProjectArgument(check.Command))));
        Assert.Empty(scoped.Checks); // All raw fallback projects were undeclared.

        foreach (var json in new[] { "{\"checks\":[]}", "{\"checks\":[{\"type\":\"command\",\"command\":\"widget-tool\"}]}" })
        {
            fixture.WriteManifest(json);
            var empty = AcceptanceManifest.Load(fixture.Worktree, [changed], fixture.Home);
            Assert.Empty(ManifestScopedImpactPlan.Plan([changed], fixture.Worktree, empty.Checks).Checks);
        }
    }

    private static string? ProjectArgument(IReadOnlyList<string> command)
    {
        for (var index = 2; index + 1 < command.Count; index++)
            if (command[index].Equals("--project", StringComparison.OrdinalIgnoreCase)) return command[index + 1];
        return command.Count > 2 && !command[2].StartsWith('-') ? command[2] : null;
    }
}
