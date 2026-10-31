using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests;

public sealed class AcceptancePlanIdentityTests
{
    [Fact]
    public void Tool_home_only_change_invalidates_identity_but_equivalent_json_does_not()
    {
        var root = CreateWorkspace("""
            { "version": 1, "checks": [{ "name": "check", "type": "command", "command": "git", "arguments": ["diff", "--check"] }] }
            """);
        try
        {
            var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
            var baseline = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root);
            var original = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
            original["toolHomes"] = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { variable = "RUSTUP_HOME", directory = Path.Combine(root, "rustup") },
                new { variable = "CARGO_HOME", directory = Path.Combine(root, "cargo") }
            }));
            File.WriteAllText(manifestPath, original.ToJsonString());
            var declared = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root);
            Assert.NotEqual(baseline, declared);
            var homes = original["toolHomes"]!.AsArray();
            var first = homes[0]!.DeepClone();
            homes.RemoveAt(0);
            homes.Add(first);
            File.WriteAllText(manifestPath, original.ToJsonString(new() { WriteIndented = true }));
            Assert.Equal(declared, GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root));
            homes[0]!["directory"] = Path.Combine(root, "different-cargo");
            File.WriteAllText(manifestPath, original.ToJsonString());
            Assert.NotEqual(declared, GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Effective_plan_identity_uses_loaded_engine_settings()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": { "maxConcurrentShards": 2 },
              "checks": [{
                "name": "git diff whitespace",
                "type": "command",
                "command": "git",
                "arguments": ["diff", "--check"]
              }]
            }
            """);
        try
        {
            var settings = AcceptanceGateEngineSettings.Load(root);
            var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(root);
            var identityMethod = typeof(GoalAcceptanceVerifier).GetMethod(
                "ComputeEffectiveAcceptanceManifestIdentity",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var expectedInGateIdentity = (string)identityMethod.Invoke(null, [checks, settings])!;
            var defaultSettingsIdentity = (string)identityMethod.Invoke(
                null,
                [checks, new AcceptanceGateEngineSettings()])!;

            var effectivePlanIdentity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root);

            Assert.Equal(expectedInGateIdentity, effectivePlanIdentity);
            Assert.NotEqual(defaultSettingsIdentity, effectivePlanIdentity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-plan-identity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), manifest);
        return root;
    }
}
