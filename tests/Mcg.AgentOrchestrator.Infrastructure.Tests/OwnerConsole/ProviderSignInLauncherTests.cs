using System.Diagnostics;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProviderSignInLauncherTests
{
    [Theory]
    [InlineData(ProviderKind.OpenAICodexCli, "codex", "CODEX_HOME", "login")]
    [InlineData(ProviderKind.OpenAICodexLuna, "codex", "CODEX_HOME", "login")]
    [InlineData(ProviderKind.AnthropicClaudeCli, "claude", "CLAUDE_CONFIG_DIR", "auth login")]
    [InlineData(ProviderKind.XaiGrokCli, "grok", "GROK_HOME", "login")]
    public void NativePlanUsesProviderArgumentsAndWorkerSource(ProviderKind kind, string cli, string variable, string arguments)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = $@"C:\provider tools\{cli}.exe";
        var info = ProviderSignInLauncher.BuildStartInfo(path, kind, @"C:\workspace", false);
        Assert.Equal(path, info.FileName);
        Assert.Equal(arguments.Split(' '), info.ArgumentList);
        Assert.Equal(@"C:\workspace", info.WorkingDirectory);
        var source = kind switch
        {
            ProviderKind.AnthropicClaudeCli => ClaudeCredentialSource.ResolveLocation(null, null).DirectoryPath,
            ProviderKind.XaiGrokCli => WorkerProviderHomeEnvironment.GrokSeedDirectory(),
            _ => WorkerProviderHomeEnvironment.CodexSeedDirectory()
        };
        Assert.Equal(source, info.Environment[variable]);
        Assert.False(info.RedirectStandardInput);
        Assert.False(info.RedirectStandardOutput);
        Assert.False(info.RedirectStandardError);
        Assert.False(info.CreateNoWindow);
    }

    [Theory]
    [InlineData("  --device-code  Authenticate with a device code", true)]
    [InlineData("  --device-code-format  Choose an output format", false)]
    [InlineData("  Usage examples mention --device-code without advertising an option.", false)]
    public async Task GrokDeviceCodeRequiresAdvertisedOption(string help, bool supported)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string path = @"C:\provider tools\grok.exe";
        var target = new ProviderSignInTarget(ProviderKind.XaiGrokCli, "grok");
        ProcessStartInfo? plan = null;
        var launcher = new ProviderSignInLauncher(@"C:\workspace", (request, _) =>
        {
            if (request.FileName == path)
            {
                Assert.Equal(["login", "--help"], request.Arguments);
                return Task.FromResult(new WorkerProcessRunResult(0, help, ""));
            }
            Assert.Contains("Get-Command -Name 'grok'", request.Arguments[^1]);
            return Task.FromResult(new WorkerProcessRunResult(0, path + "\r\n", ""));
        }, info =>
        {
            plan = info;
            return new Session();
        });

        Assert.Equal(supported, await launcher.SupportsDeviceCodeAsync(target, CancellationToken.None));
        var launch = await launcher.LaunchAsync(target, true, CancellationToken.None);
        using var session = launch.Session;
        if (supported)
        {
            Assert.NotNull(session);
            Assert.NotNull(plan);
            Assert.Equal(["login", "--device-code"], plan.ArgumentList);
        }
        else
        {
            Assert.Null(plan);
            Assert.Equal(ProviderSignInOutcome.Unsupported, launch.Failure);
        }
    }

    [Fact]
    public async Task MissingToolAndStartFailureAreDistinct()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = new ProviderSignInTarget(ProviderKind.AnthropicClaudeCli, "claude");
        var missing = new ProviderSignInLauncher(@"C:\workspace", (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "missing\n", "")));
        var failure = new ProviderSignInLauncher(@"C:\workspace",
            (_, _) => Task.FromResult(new WorkerProcessRunResult(0, @"C:\tools\claude.exe" + "\n", "")),
            _ => throw new InvalidOperationException("raw provider diagnostic"));
        Assert.Equal(ProviderSignInOutcome.ToolMissing, (await missing.LaunchAsync(target, false, CancellationToken.None)).Failure);
        Assert.Equal(ProviderSignInOutcome.LaunchFailed, (await failure.LaunchAsync(target, false, CancellationToken.None)).Failure);
    }

    [Fact]
    public void ProviderExitRequiresPositiveAuthenticationEvidence()
    {
        Assert.Equal(ProviderSignInOutcome.Unknown, ProviderSignInExit.FromExitCode(0).Outcome);
        Assert.Equal(ProviderSignInOutcome.Unknown, ProviderSignInExit.FromExitCode(1).Outcome);
        Assert.Equal(ProviderSignInOutcome.Cancelled, ProviderSignInExit.FromExitCode(unchecked((int)0xc000013a)).Outcome);
    }

    [Theory]
    [InlineData("$env:CODEX_HOME='C:\\different'; codex exec")]
    [InlineData("codex exec -c cli_auth_credentials_store=file")]
    [InlineData("codex.ps1 exec")]
    public void ProfileCanNeverPassItsCommandToNativeSignIn(string template) =>
        Assert.Null(ProviderSignInTarget.ForProfile(new("codex-cli", template)));

    [Fact]
    public async Task StoreLoadedGrokTurnLimitPreservesWorkerDispatchAndUsesNativeLogin()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var stock = WorkerProfileCatalog.Default().GetRequired("grok-cli");
            var configured = stock.CommandTemplate.Replace("--max-turns 32", "--max-turns 64", StringComparison.Ordinal);
            var path = Path.Combine(root, "workers.json");
            WorkerProfileStore.Save(path, new([stock with { CommandTemplate = configured }]));
            var storedBytes = File.ReadAllBytes(path);
            var loaded = WorkerProfileStore.Load(path).GetRequired("grok-cli");
            Assert.EndsWith("--max-turns 64", loaded.CommandTemplate);
            Assert.Equal(configured, loaded.CommandTemplate);

            var stockTarget = Assert.IsType<ProviderSignInTarget>(ProviderSignInTarget.ForProfile(stock));
            var loadedTarget = Assert.IsType<ProviderSignInTarget>(ProviderSignInTarget.ForProfile(loaded));
            Assert.Equal(stockTarget, loadedTarget);
            Assert.Equal(new ProviderSignInTarget(ProviderKind.XaiGrokCli, "grok"), loadedTarget);

            var brief = new TaskBrief(new("grok-goal"), new("grok-task"), AgentRole.Developer, "Fixture", "Fixture prompt");
            var variables = new Dictionary<string, string?>
            {
                ["subscriptionModelName"] = "fixture-model",
                ["permissionMode"] = "fixture-mode",
                ["workingDirectory"] = root
            };
            var prepared = WorkerDispatchPromptPreparation.Prepare(brief, loaded, loadedTarget.Kind, root,
                variables, DateTimeOffset.UnixEpoch, retainedDispatch: null, workerToolsAvailable: false);
            Assert.EndsWith("--max-turns 64", prepared.Command);
            Assert.Single(prepared.Command.Split(' ').Where(token => token == "--max-turns"));
            Assert.Equal(storedBytes, File.ReadAllBytes(path));

            var adapter = new ProviderConnectionStatusAdapters(root, (_, _) => throw new InvalidOperationException("must not run"),
                _ => throw new InvalidOperationException("must not locate"));
            Assert.Equal(ProviderConnectionResult.Unsupported, (await adapter.CheckAsync(loaded)).Result);

            if (OperatingSystem.IsWindows())
            {
                var executable = Path.Combine(root, "grok.exe");
                var probes = new List<WorkerProcessDirectRunRequest>();
                var starts = new List<ProcessStartInfo>();
                var launcher = new ProviderSignInLauncher(root, (request, _) =>
                {
                    probes.Add(request);
                    return Task.FromResult(new WorkerProcessRunResult(0, executable + "\n", ""));
                }, info =>
                {
                    starts.Add(info);
                    return new Session();
                });
                var launch = await launcher.LaunchAsync(loadedTarget, false, CancellationToken.None);
                using var session = launch.Session;
                Assert.NotNull(session);
                var probe = Assert.Single(probes);
                Assert.Contains("Get-Command -Name 'grok'", probe.Arguments[^1]);
                Assert.DoesNotContain("--max-turns", probe.Arguments[^1]);
                var start = Assert.Single(starts);
                Assert.Equal(executable, start.FileName);
                Assert.Equal(["login"], start.ArgumentList);
                Assert.Equal(WorkerProviderHomeEnvironment.GrokSeedDirectory(), start.Environment["GROK_HOME"]);
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("00064")]
    [InlineData("4294967295")]
    public void NativeGrokTurnLimitAcceptsPositiveAsciiUInt32(string value)
    {
        var stock = WorkerProfileCatalog.Default().GetRequired("grok-cli");
        var template = stock.CommandTemplate.Replace("--max-turns 32", "--max-turns " + value, StringComparison.Ordinal);
        Assert.Equal(new ProviderSignInTarget(ProviderKind.XaiGrokCli, "grok"),
            ProviderSignInTarget.ForProfile(stock with { CommandTemplate = template }));
    }

    [Theory]
    [InlineData("--max-turns 32", "--max-turns")]
    [InlineData("--max-turns 32", "--max-turns 0")]
    [InlineData("--max-turns 32", "--max-turns +64")]
    [InlineData("--max-turns 32", "--max-turns -64")]
    [InlineData("--max-turns 32", "--max-turns 64.0")]
    [InlineData("--max-turns 32", "--max-turns ６４")]
    [InlineData("--max-turns 32", "--max-turns '64'")]
    [InlineData("--max-turns 32", "--max-turns 4294967296")]
    [InlineData("--max-turns 32", "--max-turns 64 --max-turns 32")]
    [InlineData("--verbatim --max-turns 32", "--max-turns 64 --verbatim")]
    [InlineData("--verbatim --max-turns 32", "--VERBATIM --max-turns 64")]
    [InlineData("--max-turns 32", "--max-turns 64 --help")]
    [InlineData("--max-turns 32", "--max-turns 64; Write-Output extra")]
    [InlineData("--max-turns 32", "--max-turns 64 | Write-Output")]
    [InlineData("--max-turns 32", "--max-turns 64 > output.txt")]
    [InlineData("--max-turns 32", "--max-turns 64\nWrite-Output extra")]
    [InlineData("grok ", "wrapper grok ")]
    [InlineData("grok ", "codex ")]
    [InlineData("grok ", ".\\grok.exe ")]
    [InlineData("grok ", "grok.ps1 ")]
    [InlineData("grok ", "Set-Location elsewhere; grok ")]
    [InlineData("grok ", "$env:PATH='elsewhere'; grok ")]
    [InlineData("grok ", "$env:GROK_HOME='fixture-home'; grok ")]
    [InlineData("grok ", "$env:XAI_API_KEY='fixture-key'; grok ")]
    public void NativeGrokTurnLimitRejectsUnsafeTemplateChanges(string before, string after)
    {
        var stock = WorkerProfileCatalog.Default().GetRequired("grok-cli");
        var template = stock.CommandTemplate.Replace(before, after, StringComparison.Ordinal)
            .Replace("--max-turns 32", "--max-turns 64", StringComparison.Ordinal);
        Assert.NotEqual(stock.CommandTemplate, template);
        Assert.Null(ProviderSignInTarget.ForProfile(stock with { CommandTemplate = template }));
    }

    private sealed class Session : IProviderSignInSession
    {
        public Task<ProviderSignInExit> WaitAsync(CancellationToken token) => Task.FromResult(ProviderSignInExit.FromExitCode(0));
        public void Dispose() { }
    }
}
