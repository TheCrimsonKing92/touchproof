using Mcg.AgentOrchestrator.App.Orchestration;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Behavioral wrapper coverage is selected by its changed file and deferred class,
// rather than the repository-wide source-policy *GuardTests selection.
public sealed class WrapperChildFlagCollisionTests
{
    private static readonly string[] Wrappers =
        ["Invoke-RepoScript.ps1", "Invoke-OrchestratorCommand.ps1", "Start-OrchestratorCommand.ps1"];

    [Xunit.Theory]
    [Xunit.InlineData("Invoke-RepoScript.ps1", null, false, false)]
    [Xunit.InlineData("Invoke-OrchestratorCommand.ps1", null, false, false)]
    [Xunit.InlineData("Invoke-OrchestratorCommand.ps1", null, true, true)]
    [Xunit.InlineData("Start-OrchestratorCommand.ps1", null, false, false)]
    [Xunit.InlineData("Start-OrchestratorCommand.ps1", null, true, false)]
    [Xunit.InlineData("Start-OrchestratorCommand.ps1", "guard-batch", false, true)]
    [Xunit.InlineData("Start-OrchestratorCommand.ps1", "guard-batch", true, true)]
    public void AdvertisedFlags_EachEntryPoint_PreservesArgvAndControls(
        string wrapper, string? batch, bool throughRepoScript, bool explicitBinding)
    {
        var flags = ReadAdvertisedFlags();
        var wrapperPath = Path.Combine(LauncherScriptTests.FindLauncherSourceRoot(), "scripts", wrapper);
        var bindableNames = ReadBindableNames(wrapperPath);
        Assert.Empty(FindCollisions(flags, bindableNames));
        // Also reject reintroducing binding for names that are not advertised yet.
        Assert.Empty(bindableNames);

        // Bound native command length below cmd.exe's limit, including quoting
        // expansion in the fake dotnet host. Each chunk still uses the real wrappers.
        foreach (var chunk in flags.Chunk(16))
        {
            LauncherScriptTests.AssertChildArguments(wrapper, batch, throughRepoScript, root =>
            {
                var arguments = new List<string> { "probe" };
                foreach (var flag in chunk)
                {
                    arguments.Add(flag);
                    arguments.Add(flag switch
                    {
                        "--name" => "foo",
                        "--repository-root" => root,
                        "--expected-state-directory" => Path.Combine(root, ".orchestrator"),
                        _ => "value \"quoted\" with spaces"
                    });
                }
                // Pin repetition and a value that itself looks like a wrapper control.
                arguments.AddRange(["--name", "-AppDll", "--name", "value \"quoted\" with spaces"]);
                return arguments.ToArray();
            }, explicitBinding);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("--name", "Name", "foo")]
    [Xunit.InlineData("--pipeline", "Pipeline", "five-role")]
    [Xunit.InlineData("--erroract", null, "Stop")]
    public void OldBinding_EachCollision_IsReportedAndConsumesChildFlag(
        string flag, string? parameter, string value)
    {
        using var baseline = new BindingStandIn(parameter);
        var bindableNames = ReadBindableNames(baseline.Path);
        var matchingName = Assert.Single(bindableNames.Where(name =>
            name.StartsWith(flag[2..], StringComparison.OrdinalIgnoreCase)));
        Assert.Equal($"{flag} -> {matchingName}", Assert.Single(FindCollisions([flag], bindableNames)));

        var stdout = RunPowerShell(["-File", baseline.Path, flag, value]);
        using var capture = JsonDocument.Parse(stdout);
        var actual = capture.RootElement.GetProperty("args").EnumerateArray()
            .Select(argument => argument.GetString()).ToArray();
        Assert.False(new[] { flag, value }.SequenceEqual(actual),
            $"Old binding unexpectedly preserved {flag}; the negative control did not exercise consumption.");
        Assert.DoesNotContain(flag, actual);

        // Each independent control must pass both checks on every changed entry point.
        foreach (var wrapper in Wrappers)
        {
            var path = Path.Combine(LauncherScriptTests.FindLauncherSourceRoot(), "scripts", wrapper);
            Assert.Empty(FindCollisions([flag], ReadBindableNames(path)));
            LauncherScriptTests.AssertChildArguments(wrapper, null, false,
                _ => ["probe", flag, value]);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("--guardwrappercontrol", "GuardWrapperControl")]
    [Xunit.InlineData("--guardw", "GuardWrapperControl")]
    [Xunit.InlineData("--probea", "ProbeAlias")]
    [Xunit.InlineData("--warningact", "WarningAction")]
    public void LexicalGuard_NewExactPrefixOrAliasCollision_ReportsFlag(string flag, string name)
    {
        using var baseline = new BindingStandIn("GuardWrapperControl");
        var names = ReadBindableNames(baseline.Path);
        Assert.Contains(name, names);
        // The lexical check does not depend on attempting the child invocation.
        Assert.Equal($"{flag} -> {name}", Assert.Single(FindCollisions([flag], names)));
        Assert.Empty(FindCollisions(["--unrelated-child-option"], names));
    }

    private static string[] ReadAdvertisedFlags()
    {
        var source = File.ReadAllText(Path.Combine(LauncherScriptTests.FindLauncherSourceRoot(),
            "src", "Mcg.AgentOrchestrator.App", "Cli", "CliCommandHelp.cs"));
        var flags = Regex.Matches(source, @"--[a-z0-9][a-z0-9-]*", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            // CliCommandHelp also advertises these through constant references.
            .Concat([ConductorContinuitySupervisor.ChildFlag, ConductorContinuitySupervisor.ExitArtifactFlag])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(flags);
        Assert.Contains("--name", flags);
        Assert.Contains("--pipeline", flags);
        Assert.Contains("--out", flags);
        // Windows PowerShell has ErrorAction; ProgressAction is not a prerequisite.
        return flags.Concat(["--progress", "--erroract"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] FindCollisions(IEnumerable<string> flags, IEnumerable<string> bindableNames)
        => flags.SelectMany(flag => bindableNames
                .Where(name => name.StartsWith(flag[2..], StringComparison.OrdinalIgnoreCase))
                .Select(name => $"{flag} -> {name}"))
            .ToArray();

    private static string[] ReadBindableNames(string path)
    {
        var command = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $command = Get-Command -Name '{{path.Replace("'", "''", StringComparison.Ordinal)}}' -ErrorAction Stop
            $names = @($command.Parameters.Values | Where-Object { $null -ne $_ } | ForEach-Object {
                $_.Name
                $_.Aliases
            } | Sort-Object -Unique)
            ConvertTo-Json -InputObject $names -Compress
            """;
        var stdout = RunPowerShell(["-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))]);
        return JsonSerializer.Deserialize<string[]>(stdout)
            ?? throw new InvalidOperationException($"Missing parameter metadata for {path}.");
    }

    private static string RunPowerShell(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the collision-guard host.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60000), "Collision-guard host did not emit its final result.");
        var error = stderr.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"Collision-guard host exited {process.ExitCode}: {error}");
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        return stdout.GetAwaiter().GetResult();
    }

    private sealed class BindingStandIn : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("wrapper-old-binding-").FullName;
        public string Path { get; }

        public BindingStandIn(string? parameter)
        {
            Path = System.IO.Path.Combine(directory, "old-binding.ps1");
            var declaration = parameter is null ? "" : $"[Alias('ProbeAlias')][string]${parameter} = 'original',";
            File.WriteAllText(Path, $$"""
                [CmdletBinding(PositionalBinding = $false)]
                param(
                    {{declaration}}
                    [Parameter(ValueFromRemainingArguments = $true)]
                    [string[]]$Arguments
                )
                $ProgressPreference = 'SilentlyContinue'
                [pscustomobject]@{ args = @($Arguments) } | ConvertTo-Json -Compress
                """);
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
