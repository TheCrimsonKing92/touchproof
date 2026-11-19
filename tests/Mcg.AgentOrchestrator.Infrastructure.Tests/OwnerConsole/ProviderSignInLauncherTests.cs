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

    private sealed class Session : IProviderSignInSession
    {
        public Task<ProviderSignInExit> WaitAsync(CancellationToken token) => Task.FromResult(ProviderSignInExit.FromExitCode(0));
        public void Dispose() { }
    }
}
