using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: fixture-owned files and ProcessStartInfo only; no process or global mutation.
public sealed class GrokClaudeConfigIsolationTests
{
    [Theory]
    [InlineData(null, null, "plan")]
    [InlineData(null, "true", "bypassPermissions")]
    [InlineData("[cli]\nauto_update = false\n", null, "plan")]
    [InlineData("[cli]\nauto_update = false\n", "true", "bypassPermissions")]
    [InlineData("[compat.claude]\nhooks = true\nskills = true\n", null, "plan")]
    [InlineData("[compat.claude]\nhooks = true\nskills = true\n", "true", "bypassPermissions")]
    public void SeedGrok_HookedWorktree_DisablesClaudeHooks(
        string? userConfig,
        string? inheritedHookSetting,
        string permissionMode)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-grok-hooks", Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, "worktree");
        var userGrok = Path.Combine(root, "user-grok");
        Directory.CreateDirectory(Path.Combine(worktree, ".claude"));
        Directory.CreateDirectory(userGrok);
        try
        {
            var settingsPath = Path.Combine(worktree, ".claude", "settings.json");
            const string settings = """
                {"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"echo hook-sentinel"}]}]}}
                """;
            File.WriteAllText(settingsPath, settings);
            const string auth = "{\"token\":\"fixture-grok-token\"}\n";
            var trustedFolders = $"trusted_folders = ['{worktree.Replace('\\', '/')}']\n";
            File.WriteAllText(Path.Combine(userGrok, "auth.json"), auth);
            File.WriteAllText(Path.Combine(userGrok, "trusted_folders.toml"), trustedFolders);
            if (userConfig is not null)
                File.WriteAllText(Path.Combine(userGrok, "config.toml"), userConfig);

            var profile = WorkerProfileCatalog.Default().GetRequired("grok-cli");
            var preparation = WorkerCommandTemplate.Prepare(
                new TaskBrief(GoalId.New(), TaskId.New(), AgentRole.Tester, "Hook isolation", "Fixture prompt"),
                profile.Name,
                profile.CommandTemplate,
                Path.Combine(root, "prompts"),
                new Dictionary<string, string?>
                {
                    ["subscriptionModelName"] = "grok-fixture-model",
                    ["permissionMode"] = permissionMode,
                    ["workingDirectory"] = worktree
                });
            var startInfo = new ProcessStartInfo { WorkingDirectory = worktree, CreateNoWindow = true };
            startInfo.ArgumentList.Add(preparation.Command);
            startInfo.Environment.Clear();
            if (inheritedHookSetting is not null)
                startInfo.Environment["GROK_CLAUDE_HOOKS_ENABLED"] = inheritedHookSetting;

            DispatchProcessHost.SeedGrokEnvironment(
                startInfo,
                Path.Combine(worktree, ".mcg-sandbox"),
                grokHomeDirectoryAccessor: () => userGrok);

            // Installed Grok 1.0.67: config reference compat.claude.hooks and
            // 05-configuration.md#harness-compatibility document env > config > default.
            Assert.Equal("false", startInfo.Environment["GROK_CLAUDE_HOOKS_ENABLED"]);
            Assert.Contains($"--permission-mode '{permissionMode}'", Assert.Single(startInfo.ArgumentList), StringComparison.Ordinal);
            Assert.Contains($"--cwd '{worktree}'", Assert.Single(startInfo.ArgumentList), StringComparison.Ordinal);
            Assert.Equal(worktree, startInfo.WorkingDirectory);
            Assert.Equal(settings, File.ReadAllText(settingsPath));
            var grokHome = Path.Combine(worktree, ".mcg-sandbox", "grok-home");
            Assert.Equal(grokHome, startInfo.Environment["GROK_HOME"]);
            Assert.Equal(auth, File.ReadAllText(Path.Combine(grokHome, "auth.json")));
            Assert.Equal(trustedFolders, File.ReadAllText(Path.Combine(grokHome, "trusted_folders.toml")));
            var sandboxConfig = Path.Combine(grokHome, "config.toml");
            if (userConfig is null)
            {
                Assert.False(File.Exists(sandboxConfig));
            }
            else
            {
                Assert.Equal(userConfig, File.ReadAllText(sandboxConfig));
                Assert.Equal(userConfig, File.ReadAllText(Path.Combine(userGrok, "config.toml")));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(WorkerSandboxProvider.Claude)]
    [InlineData(WorkerSandboxProvider.Codex)]
    public void SeedProvider_OtherProviders_PreservesPreparationWithoutGrokOverride(
        WorkerSandboxProvider provider)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-grok-controls", Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, "worktree");
        var sourceCredentials = Path.Combine(root, "credentials");
        Directory.CreateDirectory(Path.Combine(worktree, ".claude"));
        Directory.CreateDirectory(sourceCredentials);
        try
        {
            const string settings = """
                {"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"echo hook-sentinel"}]}]}}
                """;
            var settingsPath = Path.Combine(worktree, ".claude", "settings.json");
            File.WriteAllText(settingsPath, settings);
            const string claudeAuth = "{\"claudeAiOauth\":{\"accessToken\":\"fixture-claude-token\"}}";
            const string codexAuth = "{\"tokens\":{\"access_token\":\"fixture-codex-token\"}}";
            File.WriteAllText(Path.Combine(sourceCredentials, ".credentials.json"), claudeAuth);
            File.WriteAllText(Path.Combine(sourceCredentials, "auth.json"), codexAuth);
            var startInfo = new ProcessStartInfo { WorkingDirectory = worktree, CreateNoWindow = true };
            startInfo.Environment.Clear();
            var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
            string? seededCodexHome = null;

            var resolution = DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                provider,
                sandboxRoot,
                anthropicApiKeyAccessor: () => null,
                claudeCredentialDirectoryAccessor: () => sourceCredentials,
                environmentReader: _ => null,
                defaultHomeProvider: () => throw new InvalidOperationException("Unexpected default credentials"),
                seedCodexAuth: home =>
                {
                    seededCodexHome = home;
                    File.Copy(Path.Combine(sourceCredentials, "auth.json"), Path.Combine(home, "auth.json"));
                });

            Assert.False(startInfo.Environment.ContainsKey("GROK_CLAUDE_HOOKS_ENABLED"));
            Assert.False(startInfo.Environment.ContainsKey("GROK_HOME"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "grok-home")));
            Assert.Equal(worktree, startInfo.WorkingDirectory);
            Assert.Equal(settings, File.ReadAllText(settingsPath));
            if (provider == WorkerSandboxProvider.Claude)
            {
                var claudeHome = Path.Combine(sandboxRoot, "claude-config");
                Assert.NotNull(resolution);
                Assert.Null(seededCodexHome);
                Assert.Equal(claudeHome, startInfo.Environment["CLAUDE_CONFIG_DIR"]);
                Assert.Equal(claudeAuth, File.ReadAllText(Path.Combine(claudeHome, ".credentials.json")));
                Assert.True(File.Exists(Path.Combine(claudeHome, "settings.json")));
                Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
            }
            else
            {
                var codexHome = Path.Combine(sandboxRoot, "codex-home");
                Assert.Null(resolution);
                Assert.Equal(codexHome, seededCodexHome);
                Assert.Equal(codexHome, startInfo.Environment["CODEX_HOME"]);
                Assert.Equal(codexAuth, File.ReadAllText(Path.Combine(codexHome, "auth.json")));
                Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
